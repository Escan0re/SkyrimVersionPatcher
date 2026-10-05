using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkyrimVersionPatcher.Core.Patching;

namespace SkyrimVersionPatcher.Tests;

public static class PatchingTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("patch: deletes before copying, preserves unrelated files and saves no originals", InstallAndPreserveFiles);
        suite.Add("patch: required space covers new files and diagnostic reserve", CapacityForInstallation);
        suite.Add("patch: cancellation retains changed files and retry clears diagnostics", CancellationAndRetry);
        suite.Add("patch: failed copy retains diagnostics and a fresh installation succeeds", FailedCopyAndRetry);
        suite.Add("patch: cancellation before mutation leaves game and storage unchanged", CancellationBeforeMutation);
        suite.Add("patch: crash diagnostics permit an explicit runtime retry with a missing executable", CrashDiagnosticRetry);
        suite.Add("patch: localization overlay wins, downloader metadata excluded", OrderedOverlay);
        suite.Add("patch: source changes after planning do not modify game", ChangedSource);
        suite.Add("patch: destination changes after planning do not modify game", ChangedDestination);
        suite.Add("patch: overlap and incomplete depots are rejected", InvalidSource);
        suite.Add("patch: diagnostic storage cannot overlap source or game", DiagnosticOverlap);
        suite.Add("patch: file-directory conflicts are rejected before changes", DirectoryConflict);
        suite.Add("patch: source and destination reparse points are rejected", ReparsePoints);
        suite.Add("patch: unsafe and duplicate journal paths are never retryable", InvalidJournalPaths);
        suite.Add("patch: identical files retain handles, attributes and timestamps", UnchangedFiles);
        suite.Add("patch: unchanged source and target snapshots are still validated", UnchangedSnapshots);
        suite.Add("patch: unchanged installation needs no transaction or disk space", UnchangedInstallation);
        suite.Add("patch: valid and malformed diagnostics are independently listed", DiagnosticListing);
        suite.Add("patch: current secondary journal survives primary corruption", JournalReplica);
        suite.Add("patch: newest durable checkpoint retains retry evidence", LatestCheckpoint);
        suite.Add("patch: conflicting journal identities prevent game mutation", ConflictingJournals);
        suite.Add("patch: diagnostic cleanup preserves existing unrelated artifacts", PreserveExistingArtifacts);
        suite.Add("runtime patch: runtime files install while data and mods stay untouched", RuntimeInstall);
        suite.Add("runtime patch: unknown assets, metadata and nested executable paths are rejected", RuntimeUnknownFiles);
        suite.Add("runtime patch: missing executable and incomplete installed bases require a valid retry", RuntimeIncompleteGame);
        suite.Add("runtime patch: source overlaps and repeated sources are rejected", RuntimeOverlaps);
        suite.Add("runtime patch: cancellation retains installed runtime changes and game data", RuntimeCancellation);
        suite.Add("runtime patch: removed master after planning prevents game changes", RuntimeBaseRemoved);
        suite.Add("clean patch: every matching name is removed recursively before all staged files move", CleanInstall);
        suite.Add("clean patch: identical files are still replaced and moved", CleanIdenticalFiles);
        suite.Add("clean patch: same-volume moves require only the diagnostic reserve", CleanMoveCapacity);
        suite.Add("clean patch: changed staged files and nested game matches prevent all deletion", CleanChangedSnapshots);
        suite.Add("clean patch: locked nested matches prevent all deletion", CleanLockedMatch);
        suite.Add("clean patch: cancellation after deletion retains sources and retry diagnostics", CleanCancellationAndRetry);
        suite.Add("clean patch: game junctions and overlapping sources cannot escape selected directories", CleanPathSafety);
        suite.Add("clean patch: Steam sources move while names outside the game and unrelated names remain", CleanExternalSteamSource);
        suite.Add("clean patch: a failed move after deletion retains payload and retry diagnostics", CleanFailedMoveAndRetry);
    }

    private static async Task InstallAndPreserveFiles()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        fixture.WriteGame("Data/Skyrim.esm", "old master");
        fixture.WriteGame("Data/mod.esp", "keep mod");
        fixture.WriteSource("Data/new.bsa", "new archive");
        using var originalHandle = new FileStream(Path.Combine(fixture.Game, "SkyrimSE.exe"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var engine = new PatchEngine();
        var plan = await engine.PrepareAsync([fixture.Source], fixture.Game);
        var stages = new List<string>();
        var result = await engine.ApplyAsync(plan, fixture.Diagnostics, new InlineProgress(value => stages.Add(value.Stage)));
        Assert.Equal(3, result.FilesCopied);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("new master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal("keep mod", fixture.ReadGame("Data/mod.esp"));
        // Retained handles see the deleted original file object, proving the installer never overwrites in place.
        using var reader = new StreamReader(originalHandle, leaveOpen: true);
        Assert.Equal("old executable", await reader.ReadToEndAsync());
        Assert.Equal("", result.TransactionDirectory);
        Assert.Equal(plan.TotalBytes, result.BytesCopied);
        Assert.True(stages.All(stage => stage is "Validating" or "Applying" or "Completed"));
        Assert.Equal(0, Directory.EnumerateFileSystemEntries(fixture.Diagnostics).Count());
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Count);
    }

    private static async Task CapacityForInstallation()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", new string('A', 1024 * 1024));
        fixture.WriteGame("Data/Skyrim.esm", new string('B', 1024 * 1024));
        var plan = await new PatchEngine().PrepareAsync([fixture.Source], fixture.Game);
        var required = plan.TotalBytes + 16 * 1024 * 1024;
        await Assert.ThrowsAsync<IOException>(() => new PatchEngine(_ => required - 1).ApplyAsync(plan, fixture.Diagnostics));
        Assert.True(!Directory.Exists(fixture.Diagnostics));
        var result = await new PatchEngine(_ => required).ApplyAsync(plan, fixture.Diagnostics);
        Assert.Equal(2, result.FilesCopied);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
    }

    private static async Task CancellationAndRetry()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        fixture.WriteGame("Data/Skyrim.esm", "old master");
        using var cancellation = new CancellationTokenSource();
        var engine = new PatchEngine();
        var plan = await engine.PrepareAsync([fixture.Source], fixture.Game);
        var observer = new InlineProgress(value =>
        {
            if (value.Stage == "Applying" && value.FilesCompleted == 1) cancellation.Cancel();
        });
        var error = await Assert.ThrowsAsync<PatchIncompleteInstallationException>(() =>
            engine.ApplyAsync(plan, fixture.Diagnostics, observer, cancellation.Token));
        Assert.True(error.WasCancelled && error.InnerException is OperationCanceledException);
        Assert.Equal("new master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
        var diagnostic = (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Single();
        Assert.Equal("Interrupted", diagnostic.State);
        Assert.True(diagnostic.CanRetry && diagnostic.Error is not null);
        Assert.Equal(error.TransactionDirectory, diagnostic.TransactionDirectory);
        Assert.True(Directory.EnumerateFileSystemEntries(error.TransactionDirectory).All(path =>
            Path.GetFileName(path) is "journal.json" or "journal.secondary.json"));
        Assert.True(!File.ReadAllText(Path.Combine(error.TransactionDirectory, "journal.json")).Contains("Original"));
        var retry = await engine.ApplyAsync(await engine.PrepareAsync([fixture.Source], fixture.Game), fixture.Diagnostics);
        Assert.Equal(1, retry.FilesCopied);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Count);
    }

    private static async Task FailedCopyAndRetry()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        fixture.WriteGame("Data/Skyrim.esm", "old master");
        var engine = new PatchEngine();
        var observer = new InlineProgress(value =>
        {
            if (value.Stage == "Applying" && value.FilesCompleted == 1)
                fixture.WriteSource("SkyrimSE.exe", "changed during installation");
        });
        var error = await Assert.ThrowsAsync<PatchIncompleteInstallationException>(async () => await engine.ApplyAsync(
            await engine.PrepareAsync([fixture.Source], fixture.Game), fixture.Diagnostics, observer));
        Assert.True(!error.WasCancelled && error.InnerException is IOException);
        Assert.Equal("new master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal("changed during installation", fixture.ReadGame("SkyrimSE.exe"));
        Assert.True((await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Single().CanRetry);
        fixture.WriteSource("SkyrimSE.exe", "new executable");
        await engine.ApplyAsync(await engine.PrepareAsync([fixture.Source], fixture.Game), fixture.Diagnostics);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Count);
    }

    private static async Task CancellationBeforeMutation()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        fixture.WriteGame("Data/Skyrim.esm", "old master");
        var engine = new PatchEngine();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await engine.ApplyAsync(
            await engine.PrepareAsync([fixture.Source], fixture.Game), fixture.Diagnostics,
            new InlineProgress(_ => cancellation.Cancel()), cancellation.Token));
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("old master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.True(!Directory.Exists(fixture.Diagnostics));
    }

    private static async Task CrashDiagnosticRetry()
    {
        using var fixture = RuntimeFixture();
        File.Delete(Path.Combine(fixture.Game, "SkyrimSE.exe"));
        fixture.MakeJournal("Applying", 1, "SkyrimSE.exe");
        var engine = new PatchEngine();
        Assert.True((await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Single().CanRetry);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareRuntimeAsync([fixture.Source], fixture.Game));
        var plan = await engine.PrepareRuntimeAsync([fixture.Source], fixture.Game, allowMissingExecutable: true);
        await engine.ApplyAsync(plan, fixture.Diagnostics);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("existing master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal("existing mod", fixture.ReadGame("Data/mod.esp"));
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Count);
    }

    private static async Task DiagnosticOverlap()
    {
        using var fixture = new PatchFixture();
        var engine = new PatchEngine();
        var plan = await engine.PrepareAsync([fixture.Source], fixture.Game);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.ApplyAsync(plan, Path.Combine(fixture.Game, "transactions")));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.ApplyAsync(plan, Path.Combine(fixture.Source, "transactions")));
        Assert.True(!File.Exists(Path.Combine(fixture.Game, "SkyrimSE.exe")));
    }

    private static async Task InvalidJournalPaths()
    {
        foreach (var relative in new[] { "../outside.txt", ".. /outside.txt", "Data./file.txt", "Data /file.txt", "CON", "NUL.txt", "x:stream" })
        {
            using var fixture = new PatchFixture();
            var outside = Path.Combine(fixture.Root, "outside.txt");
            File.WriteAllText(outside, "safe");
            fixture.MakeJournal("Applying", 1, relative);
            var diagnostic = (await new PatchEngine().ListDiagnosticsAsync(fixture.Diagnostics)).Single();
            Assert.True(diagnostic.State == "Invalid" && !diagnostic.CanRetry);
            Assert.Equal("safe", File.ReadAllText(outside));
        }
        using var duplicate = new PatchFixture();
        duplicate.MakeJournal("Applying", 2, "Data/file.txt", "Data\\file.txt");
        Assert.True(!(await new PatchEngine().ListDiagnosticsAsync(duplicate.Diagnostics)).Single().CanRetry);
    }

    private static async Task UnchangedFiles()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "new executable");
        fixture.WriteGame("Data/Skyrim.esm", "old master");
        fixture.WriteSource("Data/added.bsa", "new archive");
        var executable = Path.Combine(fixture.Game, "SkyrimSE.exe");
        var originalTime = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(executable, originalTime);
        File.SetAttributes(executable, FileAttributes.ReadOnly);
        using var retainedHandle = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var engine = new PatchEngine();
        var plan = await engine.PrepareAsync([fixture.Source], fixture.Game);
        Assert.Equal(1, plan.AddedFileCount);
        Assert.Equal(1, plan.ReplacedFileCount);
        Assert.Equal(1, plan.SkippedFileCount);
        Assert.Equal(PatchFileAction.Skip, plan.Files.Single(file => file.RelativePath == "SkyrimSE.exe").Action);
        Assert.True(plan.Files.Single(file => file.Action == PatchFileAction.Replace).PossibleModCollision);
        Assert.Equal(2, (await engine.ApplyAsync(plan, fixture.Diagnostics)).FilesCopied);
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(executable));
        Assert.True((File.GetAttributes(executable) & FileAttributes.ReadOnly) != 0);
        Assert.Equal("new master", fixture.ReadGame("Data/Skyrim.esm"));
    }

    private static async Task DiagnosticListing()
    {
        using var fixture = new PatchFixture();
        var valid = fixture.MakeJournal("Interrupted", 1, "SkyrimSE.exe");
        var malformed = fixture.MakeJournal("Interrupted", 1, "Data/Skyrim.esm");
        File.WriteAllText(Path.Combine(malformed, "journal.json"), "{ invalid json");
        var engine = new PatchEngine();
        var listing = await engine.ListDiagnosticsAsync(fixture.Diagnostics);
        Assert.Equal(2, listing.Count);
        Assert.True(listing.Single(item => item.TransactionDirectory == valid).CanRetry);
        Assert.True(listing.Single(item => item.TransactionDirectory == malformed).State == "Invalid");
        // An unidentified malformed neighboring record must not block every game.
        await engine.ApplyAsync(await engine.PrepareAsync([fixture.Source], fixture.Game), fixture.Diagnostics);
        Assert.Equal(malformed, (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Single().TransactionDirectory);
    }

    private static async Task JournalReplica()
    {
        using var fixture = new PatchFixture();
        var transaction = fixture.MakeJournal("Applying", 1, "SkyrimSE.exe");
        File.Copy(Path.Combine(transaction, "journal.json"), Path.Combine(transaction, "journal.secondary.json"));
        File.WriteAllText(Path.Combine(transaction, "journal.json"), "truncated journal");
        Assert.True((await new PatchEngine().ListDiagnosticsAsync(fixture.Diagnostics)).Single().CanRetry);
    }

    private static async Task LatestCheckpoint()
    {
        using var fixture = new PatchFixture();
        var transaction = fixture.MakeJournal("Applying", 0, "SkyrimSE.exe", "Data/Skyrim.esm");
        var checkpoint = JsonNode.Parse(File.ReadAllText(Path.Combine(transaction, "journal.json")))!;
        checkpoint["Sequence"] = 2;
        checkpoint["AffectedCount"] = 2;
        File.WriteAllText(Path.Combine(transaction, "journal.secondary.json"), checkpoint.ToJsonString());
        Assert.True((await new PatchEngine().ListDiagnosticsAsync(fixture.Diagnostics)).Single().CanRetry);
    }

    private static async Task ConflictingJournals()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "current executable");
        var transaction = fixture.MakeJournal("Applying", 1, "SkyrimSE.exe");
        var replica = JsonNode.Parse(File.ReadAllText(Path.Combine(transaction, "journal.json")))!;
        replica["CreatedAt"] = DateTimeOffset.UtcNow.AddDays(-1);
        File.WriteAllText(Path.Combine(transaction, "journal.secondary.json"), replica.ToJsonString());
        var engine = new PatchEngine();
        var diagnostic = (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Single();
        Assert.True(diagnostic.State == "Invalid" && !diagnostic.CanRetry);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await engine.ApplyAsync(
            await engine.PrepareAsync([fixture.Source], fixture.Game), fixture.Diagnostics));
        Assert.Equal("current executable", fixture.ReadGame("SkyrimSE.exe"));
    }

    private static async Task PreserveExistingArtifacts()
    {
        using var fixture = new PatchFixture();
        var artifact = Path.Combine(fixture.Root, "backups", "transaction-legacy");
        Directory.CreateDirectory(Path.Combine(artifact, "files"));
        File.WriteAllText(Path.Combine(artifact, "files", "SkyrimSE.exe"), "saved original");
        File.WriteAllText(Path.Combine(artifact, "journal.json"), "legacy journal");
        var unfinished = fixture.MakeJournal("Preparing", 0, "SkyrimSE.exe");
        var engine = new PatchEngine();
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Count);
        await engine.ApplyAsync(await engine.PrepareAsync([fixture.Source], fixture.Game), fixture.Diagnostics);
        Assert.True(!Directory.Exists(unfinished));
        Assert.Equal("saved original", File.ReadAllText(Path.Combine(artifact, "files", "SkyrimSE.exe")));
        Assert.Equal("legacy journal", File.ReadAllText(Path.Combine(artifact, "journal.json")));
    }

    private static async Task RuntimeInstall()
    {
        using var fixture = RuntimeFixture();
        fixture.WriteGame("Data/Skyrim - Shaders.bsa", "old shaders");
        fixture.WriteSource("Data/Skyrim - Shaders.bsa", "new shaders");
        fixture.WriteSource("SkyrimSELauncher.exe", "new launcher");
        var preserved = new[] { "Data/Skyrim.esm", "Data/mod.esp", "Data/Skyrim - Textures0.bsa", "Data/Strings/Skyrim_Russian.STRINGS" }
            .Select(relative => (Relative: relative, Content: fixture.ReadGame(relative),
                LastWrite: File.GetLastWriteTimeUtc(Path.Combine(fixture.Game, relative)))).ToArray();
        var engine = new PatchEngine();
        var plan = await engine.PrepareRuntimeAsync([fixture.Source], fixture.Game);
        Assert.Equal(3, plan.FileCount);
        Assert.Equal(2, plan.ExistingFileCount);
        Assert.Equal(3, (await engine.ApplyAsync(plan, fixture.Diagnostics)).FilesCopied);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("new launcher", fixture.ReadGame("SkyrimSELauncher.exe"));
        Assert.Equal("new shaders", fixture.ReadGame("Data/Skyrim - Shaders.bsa"));
        foreach (var file in preserved)
        {
            Assert.Equal(file.Content, fixture.ReadGame(file.Relative));
            Assert.Equal(file.LastWrite, File.GetLastWriteTimeUtc(Path.Combine(fixture.Game, file.Relative)));
        }
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Count);
    }

    private static async Task RuntimeCancellation()
    {
        using var fixture = RuntimeFixture();
        fixture.WriteGame("Data/Skyrim - Shaders.bsa", "old shaders");
        fixture.WriteSource("Data/Skyrim - Shaders.bsa", "new shaders");
        fixture.WriteSource("SkyrimSELauncher.exe", "new launcher");
        using var cancellation = new CancellationTokenSource();
        var engine = new PatchEngine();
        var observer = new InlineProgress(value =>
        {
            if (value.Stage == "Applying" && value.FilesCompleted == 2) cancellation.Cancel();
        });
        await Assert.ThrowsAsync<PatchIncompleteInstallationException>(async () => await engine.ApplyAsync(
            await engine.PrepareRuntimeAsync([fixture.Source], fixture.Game), fixture.Diagnostics, observer, cancellation.Token));
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("new shaders", fixture.ReadGame("Data/Skyrim - Shaders.bsa"));
        Assert.True(!File.Exists(Path.Combine(fixture.Game, "SkyrimSELauncher.exe")));
        Assert.Equal("existing master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal("existing mod", fixture.ReadGame("Data/mod.esp"));
        Assert.True((await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Single().CanRetry);
    }

    private static async Task OrderedOverlay()
    {
        using var fixture = new PatchFixture();
        fixture.WriteSource("Skyrim_Default.ini", "english");
        fixture.WriteSource(".DepotDownloader/489831.manifest", "private metadata");
        var russian = Path.Combine(fixture.Root, "russian");
        Directory.CreateDirectory(russian);
        File.WriteAllText(Path.Combine(russian, "Skyrim_Default.ini"), "russian");
        var engine = new PatchEngine();
        var plan = await engine.PrepareAsync([fixture.Source, russian], fixture.Game);
        Assert.Equal(3, plan.FileCount);
        Assert.True(plan.Files.All(file => file.Sha256.Length == 64));
        await engine.ApplyAsync(plan, fixture.Diagnostics);
        Assert.Equal("russian", fixture.ReadGame("Skyrim_Default.ini"));
        Assert.True(!Directory.Exists(Path.Combine(fixture.Game, ".DepotDownloader")));
    }

    private static async Task ChangedSource()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        var engine = new PatchEngine();
        var plan = await engine.PrepareAsync([fixture.Source], fixture.Game);
        fixture.WriteSource("SkyrimSE.exe", "tampered");
        await Assert.ThrowsAsync<IOException>(() => engine.ApplyAsync(plan, fixture.Diagnostics));
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.True(!Directory.Exists(fixture.Diagnostics));
    }

    private static async Task ChangedDestination()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        var engine = new PatchEngine();
        var plan = await engine.PrepareAsync([fixture.Source], fixture.Game);
        fixture.WriteGame("SkyrimSE.exe", "external change");
        await Assert.ThrowsAsync<IOException>(() => engine.ApplyAsync(plan, fixture.Diagnostics));
        Assert.Equal("external change", fixture.ReadGame("SkyrimSE.exe"));
    }

    private static async Task InvalidSource()
    {
        using var fixture = new PatchFixture();
        var engine = new PatchEngine();
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareAsync([fixture.Game], fixture.Game));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareAsync([], fixture.Game));
        var empty = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(empty);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareAsync([empty], fixture.Game));
        File.WriteAllText(Path.Combine(empty, "SkyrimSE.exe"), "only exe");
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareAsync([empty], fixture.Game));
    }

    private static async Task DirectoryConflict()
    {
        using var fixture = new PatchFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Game, "SkyrimSE.exe"));
        await Assert.ThrowsAsync<IOException>(() => new PatchEngine().PrepareAsync([fixture.Source], fixture.Game));
    }

    private static async Task ReparsePoints()
    {
        using var fixture = new PatchFixture();
        var external = Path.Combine(fixture.Root, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "keep.txt"), "safe");
        var sourceLink = Path.Combine(fixture.Source, "linked");
        CreateDirectoryLink(sourceLink, external);
        try { await Assert.ThrowsAsync<IOException>(() => new PatchEngine().PrepareAsync([fixture.Source], fixture.Game)); }
        finally { Directory.Delete(sourceLink); }
        var targetLink = Path.Combine(fixture.Game, "Data");
        CreateDirectoryLink(targetLink, external);
        try { await Assert.ThrowsAsync<IOException>(() => new PatchEngine().PrepareAsync([fixture.Source], fixture.Game)); }
        finally { Directory.Delete(targetLink); }
        Assert.Equal("safe", File.ReadAllText(Path.Combine(external, "keep.txt")));
    }

    private static async Task UnchangedSnapshots()
    {
        foreach (var changeSource in new[] { true, false })
        {
            using var fixture = new PatchFixture();
            fixture.WriteGame("SkyrimSE.exe", "new executable");
            fixture.WriteGame("Data/Skyrim.esm", "old master");
            var engine = new PatchEngine();
            var plan = await engine.PrepareAsync([fixture.Source], fixture.Game);
            if (changeSource) fixture.WriteSource("SkyrimSE.exe", "tampered executable");
            else fixture.WriteGame("SkyrimSE.exe", "external executable");
            await Assert.ThrowsAsync<IOException>(() => engine.ApplyAsync(plan, fixture.Diagnostics));
            Assert.Equal("old master", fixture.ReadGame("Data/Skyrim.esm"));
            Assert.True(!Directory.Exists(fixture.Diagnostics));
        }
    }

    private static async Task UnchangedInstallation()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "new executable");
        fixture.WriteGame("Data/Skyrim.esm", "new master");
        var diskQueried = false;
        var engine = new PatchEngine(_ => { diskQueried = true; return 0; });
        var plan = await engine.PrepareAsync([fixture.Source], fixture.Game);
        Assert.Equal(2, plan.SkippedFileCount);
        Assert.Equal(0L, plan.TotalBytes);
        var result = await engine.ApplyAsync(plan, fixture.Diagnostics);
        Assert.Equal("", result.TransactionDirectory);
        Assert.Equal(0, result.FilesCopied);
        Assert.True(!diskQueried && !Directory.Exists(fixture.Diagnostics));
    }

    private static PatchFixture RuntimeFixture()
    {
        var fixture = new PatchFixture();
        File.Delete(Path.Combine(fixture.Source, "Data", "Skyrim.esm"));
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        fixture.WriteGame("Data/Skyrim.esm", "existing master");
        fixture.WriteGame("Data/mod.esp", "existing mod");
        fixture.WriteGame("Data/Skyrim - Textures0.bsa", "existing large archive");
        fixture.WriteGame("Data/Strings/Skyrim_Russian.STRINGS", "existing Russian strings");
        return fixture;
    }

    private static async Task RuntimeUnknownFiles()
    {
        foreach (var unexpected in new[]
        {
            "Data/Skyrim.esm", "Data/mod.esp", "Data/Skyrim - Textures0.bsa", "Data/Strings/Skyrim_Russian.STRINGS",
            "Skyrim_Default.ini", ".DepotDownloader/489833.manifest", "patch/SkyrimSE.exe", "Data/SkyrimSE.exe", "complete.json"
        })
        {
            using var fixture = RuntimeFixture();
            fixture.WriteSource(unexpected, "unexpected patch content");
            await Assert.ThrowsAsync<InvalidDataException>(() => new PatchEngine().PrepareRuntimeAsync([fixture.Source], fixture.Game));
            Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
            Assert.Equal("existing master", fixture.ReadGame("Data/Skyrim.esm"));
            Assert.True(!Directory.Exists(fixture.Diagnostics));
        }
    }

    private static async Task RuntimeIncompleteGame()
    {
        using var fixture = RuntimeFixture();
        var engine = new PatchEngine();
        File.Delete(Path.Combine(fixture.Source, "SkyrimSE.exe"));
        fixture.WriteSource("SkyrimSELauncher.exe", "launcher alone");
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareRuntimeAsync([fixture.Source], fixture.Game));
        fixture.WriteSource("SkyrimSE.exe", "new executable");
        File.Delete(Path.Combine(fixture.Game, "Data", "Skyrim.esm"));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareRuntimeAsync([fixture.Source], fixture.Game));
        fixture.WriteGame("Data/Skyrim.esm", "existing master");
        File.Delete(Path.Combine(fixture.Game, "SkyrimSE.exe"));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareRuntimeAsync([fixture.Source], fixture.Game));
        fixture.WriteGame("SkyrimSE.exe", "");
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareRuntimeAsync([fixture.Source], fixture.Game));
        Assert.True(!Directory.Exists(fixture.Diagnostics));
    }

    private static async Task RuntimeOverlaps()
    {
        using var fixture = RuntimeFixture();
        var engine = new PatchEngine();
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareRuntimeAsync([fixture.Game], fixture.Game));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareRuntimeAsync([fixture.Root], fixture.Game));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareRuntimeAsync([fixture.Source, fixture.Source], fixture.Game));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareRuntimeAsync([], fixture.Game));
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.True(!Directory.Exists(fixture.Diagnostics));
    }

    private static async Task RuntimeBaseRemoved()
    {
        using var fixture = RuntimeFixture();
        var engine = new PatchEngine();
        var plan = await engine.PrepareRuntimeAsync([fixture.Source], fixture.Game);
        File.Delete(Path.Combine(fixture.Game, "Data", "Skyrim.esm"));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.ApplyAsync(plan, fixture.Diagnostics));
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.True(!Directory.Exists(fixture.Diagnostics));
    }

    private static async Task CleanInstall()
    {
        using var fixture = new PatchFixture();
        fixture.WriteSource("Data/Textures/new.dds", "new texture");
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        fixture.WriteGame("Data/Skyrim.esm", "old master");
        fixture.WriteGame("mods/deep/SkyrimSE.EXE", "nested executable duplicate");
        fixture.WriteGame("another/deeper/Skyrim.esm", "nested master duplicate");
        fixture.WriteGame("mods/textures/new.dds", "nested texture duplicate");
        fixture.WriteGame("Data/mod.esp", "keep mod");
        var removals = new[] { "SkyrimSE.exe", "Data/Skyrim.esm", "mods/deep/SkyrimSE.EXE",
            "another/deeper/Skyrim.esm", "mods/textures/new.dds" };
        var engine = new PatchEngine();
        var plan = await engine.PrepareCleanAsync([fixture.Source], fixture.Game);
        Assert.True(plan.IsCleanInstall);
        var observedFirstMove = false;
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == "Removing")
            {
                Assert.True(plan.Files.All(file => File.Exists(file.SourcePath)), "Every source must remain staged until all removals finish.");
                Assert.True(!File.Exists(Path.Combine(fixture.Game, "Data/Textures/new.dds")));
            }
            if (value.Stage != "Applying" || observedFirstMove) return;
            observedFirstMove = true;
            Assert.True(removals.Where(relative => !string.Equals(Path.GetFullPath(Path.Combine(fixture.Game, relative)),
                    Path.GetFullPath(Path.Combine(fixture.Game, value.RelativePath!)), StringComparison.OrdinalIgnoreCase))
                .All(relative => !File.Exists(Path.Combine(fixture.Game, relative))), "The first move must follow every matching-file deletion.");
        });
        var result = await engine.ApplyAsync(plan, fixture.Diagnostics, progress);
        Assert.True(observedFirstMove);
        Assert.Equal(3, result.FilesCopied);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("new master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal("new texture", fixture.ReadGame("Data/Textures/new.dds"));
        Assert.Equal("keep mod", fixture.ReadGame("Data/mod.esp"));
        Assert.True(plan.Files.All(file => !File.Exists(file.SourcePath)));
        Assert.True(Directory.EnumerateFiles(fixture.Game, "*", SearchOption.AllDirectories)
            .All(file => !Path.GetFileName(file).StartsWith(".svp", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Count);
    }

    private static async Task CleanIdenticalFiles()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "new executable");
        fixture.WriteGame("Data/Skyrim.esm", "new master");
        var oldWrite = DateTime.UtcNow.AddYears(-1);
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Game, "SkyrimSE.exe"), oldWrite);
        var engine = new PatchEngine();
        var plan = await engine.PrepareCleanAsync([fixture.Source], fixture.Game);
        Assert.Equal(0, plan.SkippedFileCount);
        Assert.Equal(2, plan.ChangedFileCount);
        Assert.Equal(plan.SourceBytes, plan.TotalBytes);
        Assert.Equal(2, (await engine.ApplyAsync(plan, fixture.Diagnostics)).FilesCopied);
        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(fixture.Game, "SkyrimSE.exe")) != oldWrite);
        Assert.True(plan.Files.All(file => !File.Exists(file.SourcePath)));
    }

    private static async Task CleanMoveCapacity()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        fixture.WriteSource("Data/large.bsa", new string('A', 1024 * 1024));
        var plan = await new PatchEngine().PrepareCleanAsync([fixture.Source], fixture.Game);
        const long reserve = 16 * 1024 * 1024;
        await Assert.ThrowsAsync<IOException>(() => new PatchEngine(_ => reserve - 1).ApplyAsync(plan, fixture.Diagnostics));
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.True(!Directory.Exists(fixture.Diagnostics));
        Assert.Equal(3, (await new PatchEngine(_ => reserve).ApplyAsync(plan, fixture.Diagnostics)).FilesCopied);
    }

    private static async Task CleanChangedSnapshots()
    {
        foreach (var change in new[] { "source", "nested", "added", "overlay" })
        {
            using var fixture = new PatchFixture();
            fixture.WriteGame("SkyrimSE.exe", "old executable");
            fixture.WriteGame("mods/deep/Skyrim.esm", "duplicate master");
            var overlay = Path.Combine(fixture.Root, "overlay");
            Directory.CreateDirectory(overlay);
            File.WriteAllText(Path.Combine(overlay, "SkyrimSE.exe"), "overlay executable");
            var engine = new PatchEngine();
            var plan = await engine.PrepareCleanAsync([fixture.Source, overlay], fixture.Game);
            switch (change)
            {
                case "source": File.WriteAllText(Path.Combine(overlay, "SkyrimSE.exe"), "tampered source"); break;
                case "nested": fixture.WriteGame("mods/deep/Skyrim.esm", "changed duplicate"); break;
                case "added": fixture.WriteGame("new/location/SkyrimSE.exe", "new duplicate"); break;
                case "overlay": fixture.WriteSource("SkyrimSE.exe", "tampered overridden source"); break;
            }
            await Assert.ThrowsAsync<IOException>(() => engine.ApplyAsync(plan, fixture.Diagnostics));
            Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
            Assert.True(File.Exists(Path.Combine(fixture.Game, "mods/deep/Skyrim.esm")));
            Assert.True(!Directory.Exists(fixture.Diagnostics));
        }
    }

    private static async Task CleanLockedMatch()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        fixture.WriteGame("mods/deep/Skyrim.esm", "locked duplicate");
        var engine = new PatchEngine();
        var plan = await engine.PrepareCleanAsync([fixture.Source], fixture.Game);
        using var locked = new FileStream(Path.Combine(fixture.Game, "mods/deep/Skyrim.esm"),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(() => engine.ApplyAsync(plan, fixture.Diagnostics));
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("locked duplicate", fixture.ReadGame("mods/deep/Skyrim.esm"));
        Assert.True(!Directory.Exists(fixture.Diagnostics));
    }

    private static async Task CleanCancellationAndRetry()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        fixture.WriteGame("mods/deep/Skyrim.esm", "duplicate master");
        using var cancellation = new CancellationTokenSource();
        var engine = new PatchEngine();
        var plan = await engine.PrepareCleanAsync([fixture.Source], fixture.Game);
        var observer = new InlineProgress(value =>
        {
            if (value.Stage == "Removing" && value.FilesCompleted == 1) cancellation.Cancel();
        });
        var error = await Assert.ThrowsAsync<PatchIncompleteInstallationException>(() =>
            engine.ApplyAsync(plan, fixture.Diagnostics, observer, cancellation.Token));
        Assert.True(error.WasCancelled);
        Assert.True(plan.Files.All(file => File.Exists(file.SourcePath)));
        Assert.True((await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Single().CanRetry);
        Assert.True(Directory.EnumerateFiles(error.TransactionDirectory).All(file =>
            Path.GetFileName(file) is "journal.json" or "journal.secondary.json"));
        await engine.ApplyAsync(await engine.PrepareCleanAsync([fixture.Source], fixture.Game), fixture.Diagnostics);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("new master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.True(!File.Exists(Path.Combine(fixture.Game, "mods/deep/Skyrim.esm")));
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Count);
    }

    private static async Task CleanPathSafety()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        var engine = new PatchEngine();
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareCleanAsync([fixture.Game], fixture.Game));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.PrepareCleanAsync([fixture.Root], fixture.Game));
        var external = Path.Combine(fixture.Root, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "SkyrimSE.exe"), "external executable");
        var link = Path.Combine(fixture.Game, "mods", "outside");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateDirectoryLink(link, external);
        try { await Assert.ThrowsAsync<IOException>(() => engine.PrepareCleanAsync([fixture.Source], fixture.Game)); }
        finally { Directory.Delete(link); }
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("external executable", File.ReadAllText(Path.Combine(external, "SkyrimSE.exe")));
        var plan = await engine.PrepareCleanAsync([fixture.Source], fixture.Game);
        CreateDirectoryLink(link, external);
        try { await Assert.ThrowsAsync<IOException>(() => engine.ApplyAsync(plan, fixture.Diagnostics)); }
        finally { Directory.Delete(link); }
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("external executable", File.ReadAllText(Path.Combine(external, "SkyrimSE.exe")));
    }

    private static async Task CleanExternalSteamSource()
    {
        using var fixture = new PatchFixture();
        var steamSource = Path.Combine(fixture.Root, "steam", "steamapps", "content", "app_489830", "depot_489831");
        Directory.CreateDirectory(Path.GetDirectoryName(steamSource)!);
        Directory.Move(fixture.Source, steamSource);
        var otherDepot = Path.Combine(Path.GetDirectoryName(steamSource)!, "depot_unselected");
        Directory.CreateDirectory(otherDepot);
        File.WriteAllText(Path.Combine(otherDepot, "SkyrimSE.exe"), "unselected Steam executable");
        fixture.WriteGame("old/nested/SkyrimSE.exe", "nested duplicate");
        fixture.WriteGame("SkyrimSE.exe.old", "old extension");
        fixture.WriteGame("mods/prefix-SkyrimSE.exe", "different filename");
        fixture.WriteGame("Data/Skyrim.esm.backup", "backup extension");
        var engine = new PatchEngine();
        var plan = await engine.PrepareCleanAsync([steamSource], fixture.Game);
        await engine.ApplyAsync(plan, fixture.Diagnostics);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("new master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.True(!File.Exists(Path.Combine(fixture.Game, "old/nested/SkyrimSE.exe")));
        Assert.True(plan.Files.All(file => !File.Exists(file.SourcePath)));
        Assert.Equal("unselected Steam executable", File.ReadAllText(Path.Combine(otherDepot, "SkyrimSE.exe")));
        Assert.Equal("old extension", fixture.ReadGame("SkyrimSE.exe.old"));
        Assert.Equal("different filename", fixture.ReadGame("mods/prefix-SkyrimSE.exe"));
        Assert.Equal("backup extension", fixture.ReadGame("Data/Skyrim.esm.backup"));
    }

    private static async Task CleanFailedMoveAndRetry()
    {
        using var fixture = new PatchFixture();
        fixture.WriteGame("SkyrimSE.exe", "old executable");
        fixture.WriteGame("Data/Skyrim.esm", "old master");
        fixture.WriteGame("mods/deep/Skyrim.esm", "duplicate master");
        var engine = new PatchEngine();
        var plan = await engine.PrepareCleanAsync([fixture.Source], fixture.Game);
        FileStream? lockedSource = null;
        var observer = new InlineProgress(value =>
        {
            if (value.Stage == "Removing" && value.FilesCompleted == value.FilesTotal)
                lockedSource = new FileStream(plan.Files[0].SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        });
        PatchIncompleteInstallationException error;
        try
        {
            error = await Assert.ThrowsAsync<PatchIncompleteInstallationException>(() =>
                engine.ApplyAsync(plan, fixture.Diagnostics, observer));
        }
        finally { lockedSource?.Dispose(); }
        Assert.True(error.InnerException is IOException && !error.WasCancelled);
        Assert.True(plan.Files.All(file => File.Exists(file.SourcePath)));
        Assert.True(!Directory.EnumerateFiles(fixture.Game, "*", SearchOption.AllDirectories).Any());
        Assert.True((await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Single().CanRetry);
        Assert.True(Directory.EnumerateFiles(error.TransactionDirectory).All(file =>
            Path.GetFileName(file) is "journal.json" or "journal.secondary.json"));
        await engine.ApplyAsync(await engine.PrepareCleanAsync([fixture.Source], fixture.Game), fixture.Diagnostics);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("new master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Diagnostics)).Count);
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        // NTFS junction creation does not require Developer Mode or administrative privileges.
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(process.StandardError.ReadToEnd());
    }

    private sealed class InlineProgress(Action<PatchProgress> observer) : IProgress<PatchProgress>
    {
        public void Report(PatchProgress value) => observer(value);
    }

    private sealed class PatchFixture : IDisposable
    {
        private int journalCount;
        public PatchFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "SkyrimVersionPatcher-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Game);
            Directory.CreateDirectory(Source);
            WriteSource("SkyrimSE.exe", "new executable");
            WriteSource("Data/Skyrim.esm", "new master");
        }
        public string Root { get; }
        public string Source => Path.Combine(Root, "source");
        public string Game => Path.Combine(Root, "game");
        public string Diagnostics => Path.Combine(Root, "Storage", "Transactions");
        public void WriteSource(string path, string content) => Write(Source, path, content);
        public void WriteGame(string path, string content) => Write(Game, path, content);
        public string ReadGame(string path) => File.ReadAllText(Path.Combine(Game, path));
        private static void Write(string root, string path, string content)
        {
            var fullPath = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
        public string MakeJournal(string state, int affected, params string[] files)
        {
            var transaction = Path.Combine(Diagnostics, "transaction-simulated-" + ++journalCount);
            Directory.CreateDirectory(transaction);
            File.WriteAllText(Path.Combine(transaction, "journal.json"), JsonSerializer.Serialize(new
            {
                FormatVersion = 4, TransactionId = Guid.NewGuid().ToString("N"), Sequence = 1,
                GameDirectory = Game, CreatedAt = DateTimeOffset.UtcNow, State = state,
                AffectedCount = affected, Files = files.Select(relative => new { RelativePath = relative })
            }));
            return transaction;
        }
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SkyrimVersionPatcher-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup.");
            if (!Directory.Exists(Root)) return;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
