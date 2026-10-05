using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Downloading;
using SkyrimVersionPatcher.Core.Identity;
using SkyrimVersionPatcher.Core.Patching;
using SkyrimVersionPatcher.Core.Transitions;
using SkyrimVersionPatcher.Core.Workflow;

namespace SkyrimVersionPatcher.Tests;

public static class AutomaticWorkflowTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("workflow: one request downloads, overlays Russian files and installs without saved originals", DownloadAndInstall);
        suite.Add("workflow catalog: disabling rename leaves the catalog intact", InstallWithoutCatalogRename);
        suite.Add("workflow catalog: an existing ContentCatalog.bak blocks rename before game writes", ExistingRenamedCatalog);
        suite.Add("workflow: interrupted deletion permits retry without restoring missing EXE", RetryInterruptedInstallation);
        suite.Add("workflow: interrupted empty EXE is repaired by full installs without weakening identity checks", RetryEmptyExecutable);
        suite.Add("workflow: unwritable changed profile INI blocks game-file replacement", UnwritableLocalizationIni);
        suite.Add("workflow: missing EXE requires an affected same-game diagnostic", MissingExecutableWithoutDiagnostic);
        suite.Add("workflow: disabling Russian requests only base depots", WithoutRussian);
        suite.Add("workflow: verified cached files that already match need no writes", UnchangedCache);
        suite.Add("workflow: successful install preserves incompatible Creation Club catalog byte for byte", ModernCatalog);
        suite.Add("workflow: unchanged cached depots still prepare the Creation Club profile", ModernCatalogWithUnchangedCache);
        suite.Add("workflow: malformed Creation Club catalog blocks installation before game changes", MalformedCatalog);
        suite.Add("workflow: failed game installation leaves the Creation Club catalog untouched", CatalogAfterEngineFailure);
        suite.Add("workflow: unexpected executable version prevents all game changes", WrongExecutable);
        suite.Add("workflow metadata: SE and AE resource strings permit installation with stale fixed fields", MetadataVersionStrings);
        suite.Add("workflow metadata: wrong release, private revision and missing resource still block installation", MetadataMismatchStillRejected);
        suite.Add("workflow: inventory changes are rejected even for files otherwise skipped", ChangedInventory);
        suite.Add("workflow: a different depot manifest is rejected before installation", WrongManifest);
        suite.Add("workflow: cancelled download never starts installation", CancelledDownload);
        suite.Add("workflow: interrupted same-game installation retries without restoring original files", InterruptedRetry);
        suite.Add("workflow: malformed same-game journal blocks download and game changes", DamagedJournal);
        suite.Add("workflow: unrelated damaged journal does not block this game", UnrelatedDamage);
        suite.Add("workflow: overlapping automatic storage is rejected before download", OverlappingStorage);
        suite.Add("workflow: simultaneous installs cannot share the same service", ConcurrentRequests);
        suite.Add("workflow modes: automatic forwards selected game for verified reuse", AutomaticReuse);
        suite.Add("workflow modes: explicit Steam depots bypass installed reuse and binary bundles", ForcedSteam);
        suite.Add("workflow clean: each run replaces every file and removes nested filename duplicates", CleanRepeatedInstallation);
        suite.Add("workflow clean: cached or outside-workspace sources cannot change the game", CleanDownloadBoundaries);
        suite.Add("workflow clean: verified Steam depot sources move directly into the game", CleanSteamSources);
        suite.Add("workflow modes: local depots forbid Steam fallback and installed-game reuse", LocalDepots);
        suite.Add("workflow identity: trusted source hash takes precedence over version metadata", SourceHashIdentity);
        suite.Add("workflow identity: mismatched target hash or trusted wrong version blocks changes", TargetHashIdentity);
        suite.Add("workflow statistics: verified download categories and installation totals remain distinct", DownloadStatistics);
        suite.Add("workflow binary: a self-authored complete bundle cannot prove the full target depot inventory", CompleteBundle);
        suite.Add("workflow binary: runtime transition preserves data and reports actual skipped and written bytes", RuntimeBundle);
        suite.Add("workflow binary: runtime mode requires a pinned bundle and unchanged localization", RuntimeRequirements);
        suite.Add("workflow binary: source, target, localization and depot bindings are mandatory", BundleIdentityMismatch);
        suite.Add("workflow binary: automatic cannot install a runtime-only or incomplete full package", FullTargetSafeguard);
        suite.Add("workflow binary: source files come only from the selected installed game", BundleSourceBinding);
        suite.Add("workflow binary: wrong package hash and changed installed source block mutation", BundleContentMismatch);
        suite.Add("workflow copies: installation changes only the selected independent copy", IndependentCopy);
        suite.Add("workflow runtime: missing interrupted EXE cannot identify a built-in transition", RuntimeMissingIdentity);
        suite.Add("workflow versions: nonzero private EXE revisions cannot satisfy a three-part release", PrivateRevisionMismatch);
    }

    private static async Task DownloadAndInstall()
    {
        using var fixture = new Fixture();
        var engine = new PatchEngine();
        var updates = new List<AutomaticPatchProgress>();
        var result = await fixture.CreateService(engine).RunAsync(fixture.Request(), new Observer(updates.Add));
        Assert.Equal(1, fixture.DownloadCalls);
        Assert.Equal(4, fixture.LastDownload!.Depots.Count);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("russian", fixture.ReadGame("Skyrim_Default.ini"));
        Assert.Equal("keep mod", fixture.ReadGame("Data/custom.esp"));
        Assert.True(updates.Any(update => update.Stage == "Downloading") && updates.Any(update => update.Stage == "Applying"));
        Assert.True(!result.FromCache);
        Assert.Equal("1.6.1170", result.SourceVersion);
        Assert.Equal("1.5.97", result.TargetVersion);
        Assert.Equal("", result.Installation.TransactionDirectory);
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Transactions)).Count);
        Assert.True(!Directory.Exists(Path.Combine(fixture.Storage, "Backups")));
        Assert.True(updates.All(value => value.Stage is not ("BackingUp" or "RollingBack")));
    }

    private static async Task WithoutRussian()
    {
        using var fixture = new Fixture();
        await fixture.CreateService().RunAsync(fixture.Request(includeRussian: false));
        Assert.Equal(3, fixture.LastDownload!.Depots.Count);
        Assert.True(fixture.LastDownload.Depots.All(depot => !depot.IsRussian));
        Assert.Equal("english", fixture.ReadGame("Skyrim_Default.ini"));
        Assert.True(!File.Exists(Path.Combine(fixture.Game, "Data", "russian.bsa")));
    }

    private static async Task InstallWithoutCatalogRename()
    {
        using var fixture = new Fixture();
        var catalog = fixture.WriteModernCatalog();
        var engine = new PatchEngine();
        var updates = new List<AutomaticPatchProgress>();
        var result = await fixture.CreateService(engine, profileDirectory: () => fixture.Profile)
            .RunAsync(fixture.Request() with { RenameContentCatalog = false }, new Observer(updates.Add));
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("new master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal("", result.Installation.TransactionDirectory);
        Assert.True(result.ContentCatalogRenamedPath is null);
        fixture.AssertCatalogUnchanged(catalog);
        Assert.True(updates.All(value => value.Stage is not ("BackingUp" or "RollingBack" or "Compatibility")));
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Transactions)).Count);
    }

    private static async Task ExistingRenamedCatalog()
    {
        using var fixture = new Fixture();
        var catalog = fixture.WriteModernCatalog();
        var renamed = Path.Combine(fixture.Profile, "ContentCatalog.bak");
        File.WriteAllText(renamed, "existing catalog");
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.CreateService(profileDirectory: () => fixture.Profile)
            .RunAsync(fixture.Request()));
        Assert.True(error.Message.Contains("не будет перезаписан", StringComparison.Ordinal));
        fixture.AssertGameUnchanged();
        Assert.True(File.ReadAllBytes(fixture.CatalogPath).SequenceEqual(catalog));
        Assert.Equal("existing catalog", File.ReadAllText(renamed));
        var result = await fixture.CreateService(profileDirectory: () => fixture.Profile)
            .RunAsync(fixture.Request() with { RenameContentCatalog = false });
        Assert.True(result.ContentCatalogRenamedPath is null);
        Assert.True(File.ReadAllBytes(fixture.CatalogPath).SequenceEqual(catalog));
        Assert.Equal("existing catalog", File.ReadAllText(renamed));
    }

    private static async Task RetryInterruptedInstallation()
    {
        using var fixture = new Fixture();
        var transaction = fixture.MakeJournal("Applying", fixture.Game);
        File.Delete(Path.Combine(fixture.Game, "SkyrimSE.exe"));
        var beforeDownloadWasMissing = false;
        var updates = new List<AutomaticPatchProgress>();
        var result = await fixture.CreateService(beforeDownload: () =>
            beforeDownloadWasMissing = !File.Exists(Path.Combine(fixture.Game, "SkyrimSE.exe")))
            .RunAsync(fixture.Request(), new Observer(updates.Add));
        Assert.True(beforeDownloadWasMissing);
        Assert.True(result.SourceVersion is null);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.True(updates.Any(value => value.Stage == "Retrying"));
        Assert.True(updates.All(value => value.Stage is not ("Recovering" or "RollingBack")));
        Assert.True(!Directory.Exists(transaction));
        Assert.Equal(0, (await new PatchEngine().ListDiagnosticsAsync(fixture.Transactions)).Count);
    }

    private static async Task MissingExecutableWithoutDiagnostic()
    {
        foreach (var marker in new[] { "unrelated", "not-affected", "none" })
        {
            using var fixture = new Fixture();
            if (marker != "none") fixture.MakeJournal("Applying", marker == "unrelated"
                ? Path.Combine(fixture.Root, "other-game") : fixture.Game, marker == "not-affected" ? 0 : 1);
            File.Delete(Path.Combine(fixture.Game, "SkyrimSE.exe"));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => fixture.CreateService()
                .RunAsync(fixture.Request()));
            Assert.Equal(0, fixture.DownloadCalls);
            Assert.Equal("old master", fixture.ReadGame("Data/Skyrim.esm"));
        }
    }

    private static async Task RetryEmptyExecutable()
    {
        var identities = new ExecutableIdentityService();
        Task<ExecutableIdentity> Identify(string directory, CancellationToken token) =>
            identities.IdentifyAsync(Path.Combine(directory, "SkyrimSE.exe"), token);
        foreach (var mode in new[] { InstallationMode.SteamDepots, InstallationMode.LocalDepots })
        {
            using var fixture = new Fixture();
            Fixture.Write(fixture.Game, "SkyrimSE.exe", "");
            fixture.MakeJournal("Interrupted", fixture.Game);
            var result = await fixture.CreateService(identify: Identify).RunAsync(fixture.Request(false) with { Mode = mode });
            Assert.Equal(1, fixture.DownloadCalls);
            Assert.True(result.SourceVersion is null);
            Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
            Assert.Equal(0, (await new PatchEngine().ListDiagnosticsAsync(fixture.Transactions)).Count);
        }
        foreach (var invalid in new[] { "without-diagnostic", "runtime", "target" })
        {
            using var fixture = new Fixture();
            Fixture.Write(invalid == "target" ? fixture.Source : fixture.Game, "SkyrimSE.exe", "");
            if (invalid != "without-diagnostic") fixture.MakeJournal("Interrupted", fixture.Game);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService(identify: Identify).RunAsync(
                fixture.Request(false) with { Mode = invalid == "runtime" ? InstallationMode.RuntimeOnly : InstallationMode.LocalDepots }));
            Assert.Equal(invalid == "target" ? 1 : 0, fixture.DownloadCalls);
            Assert.Equal("old master", fixture.ReadGame("Data/Skyrim.esm"));
            Assert.Equal(invalid == "target" ? "old executable" : "", fixture.ReadGame("SkyrimSE.exe"));
        }
    }

    private static async Task UnwritableLocalizationIni()
    {
        using var fixture = new Fixture();
        var ini = Path.Combine(fixture.Profile, "Skyrim.ini");
        var original = "[General]\nsLanguage=RUSSIAN\n[Archive]\nsResourceArchiveList2=Skyrim - Voices_ru0.bsa\n";
        Fixture.Write(fixture.Profile, "Skyrim.ini", original);
        Fixture.Write(fixture.Source, "Skyrim_Default.ini", "[General]\nsLanguage=ENGLISH\n");
        File.SetAttributes(ini, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateService(profileDirectory: () => fixture.Profile)
                .RunAsync(fixture.Request(false) with { Mode = InstallationMode.LocalDepots, GameLanguage = "english" }));
            fixture.AssertGameUnchanged();
            Assert.Equal(original, File.ReadAllText(ini));
        }
        finally { File.SetAttributes(ini, FileAttributes.Normal); }
    }

    private static async Task UnchangedCache()
    {
        using var fixture = new Fixture();
        foreach (var item in fixture.CreateDownload(fixture.Request()).Files)
        {
            var source = fixture.CreateDownload(fixture.Request()).SourceDirectories
                .Last(root => File.Exists(Path.Combine(root, item.RelativePath)));
            Fixture.Write(fixture.Game, item.RelativePath, File.ReadAllText(Path.Combine(source, item.RelativePath)));
        }
        fixture.FromCache = true;
        var result = await fixture.CreateService().RunAsync(fixture.Request());
        Assert.True(result.FromCache);
        Assert.Equal(0, result.Installation.FilesCopied);
        Assert.Equal(result.Plan.FileCount, result.Plan.SkippedFileCount);
        Assert.Equal("", result.Installation.TransactionDirectory);
        Assert.True(!Directory.Exists(fixture.Transactions));
    }

    private static async Task WrongExecutable()
    {
        using var fixture = new Fixture();
        var catalog = fixture.WriteModernCatalog();
        var service = fixture.CreateService(versionReader: root => root == fixture.Game ? "1.6.1170" : "1.0.0",
            profileDirectory: () => fixture.Profile);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RunAsync(fixture.Request()));
        fixture.AssertGameUnchanged();
        fixture.AssertCatalogUnchanged(catalog);
    }

    private static async Task MetadataVersionStrings()
    {
        foreach (var version in new[] { "1.5.97", "1.6.1170" })
        {
            using var fixture = new Fixture();
            var target = VersionCatalogService.LoadBuiltIn().Single(item => item.Id == version);
            var request = fixture.Request(false) with { Version = target };
            string? ReadVersion(string root) => root == fixture.Game && fixture.ReadGame("SkyrimSE.exe") != "new executable"
                ? ExecutableIdentityService.ResolveMetadataVersion("1.7.104.0", "1.7.104.0", "1.7.104.0")
                : ExecutableIdentityService.ResolveMetadataVersion(version + ".0", version + ".0", "1.0.0.0");
            var result = await fixture.CreateService(versionReader: ReadVersion, transform: value => value with
            {
                Depots = VersionCatalogService.GetDepots(target, false)
            }).RunAsync(request);
            Assert.Equal("1.7.104", result.SourceVersion);
            Assert.Equal(version, result.TargetVersion);
            Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
            Assert.Equal("new master", fixture.ReadGame("Data/Skyrim.esm"));
            Assert.Equal("keep mod", fixture.ReadGame("Data/custom.esp"));
            Assert.Equal(version, ReadVersion(fixture.Game));
        }
    }

    private static async Task MetadataMismatchStillRejected()
    {
        foreach (var metadata in new (string? File, string? Product, string? Fixed)[]
        {
            ("1.6.1170.0", "1.5.97.0", "1.5.97.0"),
            ("1.5.97.1", "1.5.97.0", "1.5.97.0"),
            (null, null, "0.0.0.0")
        })
        {
            using var fixture = new Fixture();
            var catalog = fixture.WriteModernCatalog();
            var service = fixture.CreateService(versionReader: root => root == fixture.Game ? "1.6.1170"
                : ExecutableIdentityService.ResolveMetadataVersion(metadata.File, metadata.Product, metadata.Fixed),
                profileDirectory: () => fixture.Profile);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.RunAsync(fixture.Request()));
            fixture.AssertGameUnchanged();
            fixture.AssertCatalogUnchanged(catalog);
        }
    }

    private static async Task ChangedInventory()
    {
        using var fixture = new Fixture();
        var catalog = fixture.WriteModernCatalog();
        Fixture.Write(fixture.Game, "SkyrimSE.exe", "new executable");
        var service = fixture.CreateService(transform: result => result with
        {
            Files = result.Files.Select(file => file.RelativePath == "SkyrimSE.exe" ? file with { Sha256 = new string('0', 64) } : file).ToArray()
        }, profileDirectory: () => fixture.Profile);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RunAsync(fixture.Request()));
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("old master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.True(!Directory.Exists(fixture.Transactions));
        fixture.AssertCatalogUnchanged(catalog);
    }

    private static async Task WrongManifest()
    {
        using var fixture = new Fixture();
        var catalog = fixture.WriteModernCatalog();
        var service = fixture.CreateService(transform: result => result with
        {
            Depots = result.Depots.Select((depot, index) => index == 0 ? depot with { ManifestId = "123" } : depot).ToArray()
        }, profileDirectory: () => fixture.Profile);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RunAsync(fixture.Request()));
        fixture.AssertGameUnchanged();
        fixture.AssertCatalogUnchanged(catalog);
    }

    private static async Task ModernCatalog()
    {
        using var fixture = new Fixture();
        var catalog = fixture.WriteModernCatalog();
        var result = await fixture.CreateService(profileDirectory: () => fixture.Profile).RunAsync(fixture.Request());
        Assert.True(result.Installation.FilesCopied > 0);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.True(!File.Exists(fixture.CatalogPath));
        Assert.True(result.ContentCatalogRenamedPath is not null);
        Assert.True(File.ReadAllBytes(result.ContentCatalogRenamedPath!).SequenceEqual(catalog));
    }

    private static async Task ModernCatalogWithUnchangedCache()
    {
        using var fixture = new Fixture();
        var catalog = fixture.WriteModernCatalog();
        var download = fixture.CreateDownload(fixture.Request());
        foreach (var item in download.Files)
        {
            var source = download.SourceDirectories.Last(root => File.Exists(Path.Combine(root, item.RelativePath)));
            Fixture.Write(fixture.Game, item.RelativePath, File.ReadAllText(Path.Combine(source, item.RelativePath)));
        }
        fixture.FromCache = true;
        var result = await fixture.CreateService(profileDirectory: () => fixture.Profile).RunAsync(fixture.Request());
        Assert.True(result.FromCache);
        Assert.Equal(0, result.Installation.FilesCopied);
        Assert.Equal(result.Plan.FileCount, result.Plan.SkippedFileCount);
        Assert.True(!Directory.Exists(fixture.Transactions));
        Assert.True(!File.Exists(fixture.CatalogPath));
        Assert.True(result.ContentCatalogRenamedPath is not null);
        Assert.True(File.ReadAllBytes(result.ContentCatalogRenamedPath!).SequenceEqual(catalog));
    }

    private static async Task MalformedCatalog()
    {
        using var fixture = new Fixture();
        var catalog = fixture.WriteCatalog("{\"CSV2_016105c0-50a6-4beb-851a-6d0f85c6e5f0\":");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService(profileDirectory: () => fixture.Profile)
            .RunAsync(fixture.Request()));
        Assert.Equal(1, fixture.DownloadCalls);
        fixture.AssertGameUnchanged();
        fixture.AssertCatalogUnchanged(catalog);
    }

    private static async Task CatalogAfterEngineFailure()
    {
        using var fixture = new Fixture();
        var catalog = fixture.WriteModernCatalog();
        var service = fixture.CreateService(new PatchEngine(_ => 0), profileDirectory: () => fixture.Profile);
        await Assert.ThrowsAsync<IOException>(() => service.RunAsync(fixture.Request()));
        fixture.AssertGameUnchanged();
        fixture.AssertCatalogUnchanged(catalog);
    }

    private static async Task CancelledDownload()
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        var service = new AutomaticPatchService(new PatchEngine(), (request, progress, token) =>
        {
            cancelled.Cancel();
            return Task.FromResult(fixture.CreateDownload(fixture.Request()));
        }, fixture.VersionReader);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunAsync(fixture.Request(), cancellationToken: cancelled.Token));
        fixture.AssertGameUnchanged();
    }

    private static async Task InterruptedRetry()
    {
        using var fixture = new Fixture();
        Fixture.Write(fixture.Game, "SkyrimSE.exe", "partial new executable");
        var interrupted = fixture.MakeJournal("Applying", fixture.Game);
        var engine = new PatchEngine();
        var partialBeforeDownload = false;
        var service = fixture.CreateService(engine, beforeDownload: () =>
        {
            partialBeforeDownload = fixture.ReadGame("SkyrimSE.exe") == "partial new executable";
        });
        var result = await service.RunAsync(fixture.Request());
        Assert.True(partialBeforeDownload);
        Assert.Equal("1.6.1170", result.SourceVersion);
        Assert.True(!Directory.Exists(interrupted));
        Assert.Equal(0, (await engine.ListDiagnosticsAsync(fixture.Transactions)).Count);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
    }

    private static async Task DamagedJournal()
    {
        using var fixture = new Fixture();
        fixture.MakeJournal("NotAValidState", fixture.Game);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().RunAsync(fixture.Request()));
        Assert.Equal(0, fixture.DownloadCalls);
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
    }

    private static async Task UnrelatedDamage()
    {
        using var fixture = new Fixture();
        fixture.MakeJournal("NotAValidState", Path.Combine(fixture.Root, "unrelated-game"));
        var result = await fixture.CreateService().RunAsync(fixture.Request());
        Assert.True(result.Installation.FilesCopied > 0);
        Assert.Equal(1, fixture.DownloadCalls);
    }

    private static async Task OverlappingStorage()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().RunAsync(fixture.Request() with
        {
            StorageDirectory = Path.Combine(fixture.Game, "cache")
        }));
        Assert.Equal(0, fixture.DownloadCalls);
        fixture.AssertGameUnchanged();
    }

    private static async Task ConcurrentRequests()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AutomaticPatchService(new PatchEngine(), async (request, progress, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return fixture.CreateDownload(fixture.Request());
        }, fixture.VersionReader);
        var first = service.RunAsync(fixture.Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(fixture.Request())); }
        finally { release.TrySetResult(); }
        await first;
    }

    private static async Task AutomaticReuse()
    {
        using var fixture = new Fixture();
        var result = await fixture.CreateService().RunAsync(fixture.Request());
        Assert.Equal(fixture.Game, fixture.LastDownload!.InstalledGameDirectory);
        Assert.True(fixture.LastDownload.ReuseInstalledFiles);
        Assert.Equal(InstallationMode.Automatic, result.Statistics!.Mode);
        Assert.Equal(result.Plan.TotalBytes, result.Statistics.WrittenBytes);
    }

    private static async Task ForcedSteam()
    {
        using var fixture = new Fixture();
        var result = await fixture.CreateService().RunAsync(fixture.Request() with
        {
            Mode = InstallationMode.SteamDepots,
            TransitionBundlePath = "An unused bundle is irrelevant to explicit Steam mode"
        });
        Assert.Equal(1, fixture.DownloadCalls);
        Assert.Equal(fixture.Game, fixture.LastDownload!.InstalledGameDirectory);
        Assert.True(!fixture.LastDownload.ReuseInstalledFiles);
        Assert.True(fixture.LastDownload.ForceFreshDownload);
        Assert.True(fixture.LastDownload.CacheDirectory.StartsWith(Path.Combine(fixture.Storage, "Temp", "clean-"), StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Plan.IsCleanInstall && result.Plan.Files.All(file => file.Action != PatchFileAction.Skip));
        Assert.Equal(result.Plan.FileCount, result.Installation.FilesCopied);
        Assert.Equal(0L, result.Statistics!.SkippedBytes);
        Assert.True(!Directory.Exists(fixture.LastDownload.CacheDirectory));
        Assert.Equal(InstallationMode.SteamDepots, result.Statistics!.Mode);
    }

    private static async Task CleanRepeatedInstallation()
    {
        using var fixture = new Fixture();
        Fixture.Write(fixture.Game, "mods/nested/SKYRIMSE.EXE", "duplicate executable");
        Fixture.Write(fixture.Game, "mods/deeper/Skyrim.esm", "duplicate master");
        var request = fixture.Request() with { Mode = InstallationMode.SteamDepots };
        var service = fixture.CreateService();
        var first = await service.RunAsync(request);
        var firstWorkspace = fixture.LastDownload!.CacheDirectory;
        Assert.True(!File.Exists(Path.Combine(fixture.Game, "mods/nested/SKYRIMSE.EXE")));
        Assert.True(!File.Exists(Path.Combine(fixture.Game, "mods/deeper/Skyrim.esm")));
        Assert.Equal("keep mod", fixture.ReadGame("Data/custom.esp"));
        var second = await service.RunAsync(request);
        Assert.True(firstWorkspace != fixture.LastDownload!.CacheDirectory);
        Assert.Equal(2, fixture.DownloadCalls);
        Assert.True(!first.FromCache && !second.FromCache);
        Assert.Equal(second.Plan.FileCount, second.Installation.FilesCopied);
        Assert.Equal(0L, second.Statistics!.SkippedBytes);
        Assert.True(!Directory.Exists(firstWorkspace) && !Directory.Exists(fixture.LastDownload.CacheDirectory));
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(fixture.Source, "SkyrimSE.exe")));
    }

    private static async Task CleanDownloadBoundaries()
    {
        foreach (var invalid in new Func<Fixture, DownloadResult, DownloadResult>[]
        {
            (_, result) => result with { FromCache = true },
            (fixture, result) => result with { SourceDirectories = [fixture.Source, fixture.Russian] },
            (_, result) => result with { Statistics = new(TargetBytes: 100, ReusedCacheBytes: 1) },
            (_, result) => result with { Files = result.Files.Skip(1).ToArray() }
        })
        {
            using var fixture = new Fixture();
            Fixture.Write(fixture.Game, "nested/SkyrimSE.exe", "duplicate must remain");
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService(transform: result => invalid(fixture, result))
                .RunAsync(fixture.Request() with { Mode = InstallationMode.SteamDepots }));
            fixture.AssertGameUnchanged();
            Assert.Equal("duplicate must remain", fixture.ReadGame("nested/SkyrimSE.exe"));
        }
    }

    private static async Task CleanSteamSources()
    {
        using var fixture = new Fixture();
        var steam = Path.Combine(fixture.Root, "steam");
        var result = await fixture.CreateService(transform: downloaded =>
        {
            var sources = downloaded.Depots.Select(depot => Path.Combine(steam, "steamapps", "content", "app_489830", "depot_" + depot.DepotId)).ToArray();
            Fixture.Write(sources[0], "Data/Skyrim.esm", "new master");
            Fixture.Write(sources[1], "Skyrim_Default.ini", "english");
            Fixture.Write(sources[2], "SkyrimSE.exe", "new executable");
            Fixture.Write(sources[3], "Skyrim_Default.ini", "russian");
            Fixture.Write(sources[3], "Data/russian.bsa", "russian archive");
            return downloaded with { SourceDirectories = sources, FreshSteamDirectory = steam };
        }).RunAsync(fixture.Request() with { Mode = InstallationMode.SteamDepots });
        Assert.True(result.Plan.IsCleanInstall && !result.FromCache);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("russian", fixture.ReadGame("Skyrim_Default.ini"));
        Assert.True(!File.Exists(Path.Combine(steam, "steamapps/content/app_489830/depot_489833/SkyrimSE.exe")));
        Assert.True(!Directory.Exists(fixture.LastDownload!.CacheDirectory));
        Assert.True(File.Exists(Path.Combine(steam, "steamapps/content/app_489830/depot_489832/Skyrim_Default.ini")));
    }

    private static async Task LocalDepots()
    {
        using var fixture = new Fixture();
        var result = await fixture.CreateService().RunAsync(fixture.Request() with { Mode = InstallationMode.LocalDepots });
        Assert.True(fixture.LastDownload!.LocalFilesOnly && !fixture.LastDownload.ReuseInstalledFiles);
        Assert.Equal(InstallationMode.LocalDepots, result.Statistics!.Mode);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
    }

    private static ExecutableIdentity Identity(string version, ExecutableIdentityStatus status) =>
        new(version, version, new string('A', 64), 1, status);

    private static async Task SourceHashIdentity()
    {
        using var fixture = new Fixture();
        var result = await fixture.CreateService(versionReader: root => root == fixture.Game ? "1.0.0" : "1.5.97.0",
            identify: (root, _) => Task.FromResult(Identity(root == fixture.Game ? "1.6.1170" : "1.5.97", ExecutableIdentityStatus.KnownOfficial)))
            .RunAsync(fixture.Request());
        Assert.Equal("1.6.1170", result.SourceVersion);
    }

    private static async Task TargetHashIdentity()
    {
        foreach (var invalid in new[]
        {
            Identity("1.5.97", ExecutableIdentityStatus.KnownVersionHashMismatch),
            Identity("1.0.0", ExecutableIdentityStatus.KnownOfficial)
        })
        {
            using var fixture = new Fixture();
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService(versionReader: root => root == fixture.Game ? "1.6.1170"
                : ExecutableIdentityService.ResolveMetadataVersion("1.5.97.0", "1.5.97.0", "1.0.0.0"), identify: (root, _) =>
                Task.FromResult(root == fixture.Game ? Identity("1.6.1170", ExecutableIdentityStatus.Unknown) : invalid))
                .RunAsync(fixture.Request()));
            fixture.AssertGameUnchanged();
        }
    }

    private static async Task DownloadStatistics()
    {
        using var fixture = new Fixture();
        var total = fixture.CreateDownload(fixture.Request()).Files.Sum(file => file.Length);
        var categories = new SkyrimVersionPatcher.Core.Downloading.DownloadStatistics(total, 7, 3, 2, total - 12);
        var updates = new List<AutomaticPatchProgress>();
        var result = await fixture.CreateService(transform: value => value with { Statistics = categories })
            .RunAsync(fixture.Request(), new Observer(updates.Add));
        var statistics = result.Statistics!;
        Assert.Equal(total, statistics.TargetBytes);
        Assert.Equal(7L, statistics.ReusedInstalledBytes);
        Assert.Equal(3L, statistics.ReusedCacheBytes);
        Assert.Equal(2L, statistics.ImportedLocalBytes);
        Assert.Equal(total - 12, statistics.SteamRequestedBytes);
        Assert.True(statistics.SteamDownloadedBytes is null);
        Assert.Equal(result.Installation.BytesCopied, statistics.WrittenBytes);
        Assert.Equal(0L, statistics.SkippedBytes);
        Assert.Equal(0L, statistics.BinaryPayloadBytes);
        Assert.True(updates.Any(value => value.Stage == "Downloading" && value.Statistics?.ReusedInstalledBytes == 7));
        Assert.True(updates.Any(value => value.Stage == "Prepared" && value.Statistics?.WrittenBytes == result.Plan.TotalBytes));
        Assert.True(updates.Any(value => value.Stage == "Completed" && value.Statistics?.WrittenBytes == result.Installation.BytesCopied));
    }

    private static Dictionary<string, string> FileMap(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(root, file), file => file, StringComparer.OrdinalIgnoreCase);

    private static async Task<BinaryTransitionBuildResult> BuildBundle(Fixture fixture, string mode = "Complete",
        bool includeRussian = false, bool executableOnly = false,
        Func<BinaryTransitionIdentity, BinaryTransitionIdentity>? changeIdentity = null,
        Dictionary<string, string>? sourceFiles = null)
    {
        var request = fixture.Request(includeRussian);
        var identity = new BinaryTransitionIdentity("1.6.1170", request.Version.Id, includeRussian, mode,
            VersionCatalogService.GetDepots(request.Version, includeRussian));
        var target = FileMap(fixture.Source);
        if (mode == "RuntimeOnly" || executableOnly)
            target = target.Where(pair => pair.Key == "SkyrimSE.exe" || !executableOnly &&
                (pair.Key == "SkyrimSELauncher.exe" || pair.Key == Path.Combine("Data", "Skyrim - Shaders.bsa")))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (includeRussian)
            foreach (var (relative, file) in FileMap(fixture.Russian)) target[relative] = file;
        return await BinaryTransitionBuilder.BuildAsync(Path.Combine(fixture.Root, "bundle-" + Guid.NewGuid().ToString("N") + ".svpt"),
            changeIdentity?.Invoke(identity) ?? identity, sourceFiles ?? FileMap(fixture.Game), target);
    }

    private static AutomaticPatchRequest BundleRequest(Fixture fixture, BinaryTransitionBuildResult bundle,
        InstallationMode mode = InstallationMode.Automatic, bool includeRussian = false) => fixture.Request(includeRussian) with
        { Mode = mode, TransitionBundlePath = bundle.Path, TransitionBundleSha256 = bundle.Sha256 };

    private static async Task CompleteBundle()
    {
        using var fixture = new Fixture();
        var request = fixture.Request(false);
        var targets = FileMap(fixture.Source);
        targets.Remove("Skyrim_Default.ini");
        Assert.Equal(2, targets.Count); // An EXE and master alone do not prove a complete Steam release.
        var bundle = await BinaryTransitionBuilder.BuildAsync(Path.Combine(fixture.Root, "incomplete-complete.svpt"),
            new("1.6.1170", request.Version.Id, false, "Complete", VersionCatalogService.GetDepots(request.Version, false)),
            FileMap(fixture.Game), targets);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().RunAsync(BundleRequest(fixture, bundle)));
        Assert.True(error.Message.Contains("независимой проверки состава", StringComparison.Ordinal));
        Assert.Equal(0, fixture.DownloadCalls);
        fixture.AssertGameUnchanged();
    }

    private static async Task RuntimeBundle()
    {
        using var fixture = new Fixture();
        Fixture.Write(fixture.Game, "Data/Skyrim - Shaders.bsa", "unchanged shaders");
        Fixture.Write(fixture.Source, "Data/Skyrim - Shaders.bsa", "unchanged shaders");
        Fixture.Write(fixture.Source, "SkyrimSELauncher.exe", "new launcher");
        var bundle = await BuildBundle(fixture, mode: "RuntimeOnly");
        var result = await fixture.CreateService().RunAsync(BundleRequest(fixture, bundle, InstallationMode.RuntimeOnly));
        Assert.Equal(0, fixture.DownloadCalls);
        Assert.Equal(3, result.Plan.FileCount);
        Assert.Equal("new executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("old master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal("keep mod", fixture.ReadGame("Data/custom.esp"));
        Assert.True(!File.Exists(Path.Combine(fixture.Game, "Skyrim_Default.ini")));
        Assert.True(!File.Exists(Path.Combine(fixture.Game, "Data", "russian.bsa")));
        Assert.Equal(bundle.ReusedBytes, result.Statistics!.ReusedInstalledBytes);
        Assert.True(result.Statistics.ReusedInstalledBytes > 0);
        Assert.Equal(bundle.PayloadBytes, result.Statistics.BinaryPayloadBytes);
        Assert.Equal((long)Encoding.UTF8.GetByteCount("unchanged shaders"), result.Statistics.SkippedBytes);
        Assert.Equal(2, result.Installation.FilesCopied);
        Assert.Equal(InstallationMode.RuntimeOnly, result.Statistics.Mode);
        Assert.Equal(result.Plan.TotalBytes, result.Statistics.WrittenBytes);
    }

    private static async Task RuntimeRequirements()
    {
        using var fixture = new Fixture();
        foreach (var request in new[]
        {
            fixture.Request(false) with { Mode = InstallationMode.RuntimeOnly },
            fixture.Request(false) with { Mode = InstallationMode.RuntimeOnly, TransitionBundlePath = Path.Combine(fixture.Root, "absent.svpt") },
            fixture.Request() with { Mode = InstallationMode.RuntimeOnly, TransitionBundlePath = Path.Combine(fixture.Root, "absent.svpt"), TransitionBundleSha256 = new string('0', 64) },
            fixture.Request() with { Mode = (InstallationMode)99 },
            fixture.Request() with { TransitionBundleSha256 = new string('0', 64) }
        }) await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().RunAsync(request));
        Assert.Equal(0, fixture.DownloadCalls);
        fixture.AssertGameUnchanged();
    }

    private static async Task BundleIdentityMismatch()
    {
        using var fixture = new Fixture();
        foreach (var change in new Func<BinaryTransitionIdentity, BinaryTransitionIdentity>[]
        {
            identity => identity with { SourceVersion = "1.0.0" },
            identity => identity with { TargetVersion = "1.0.0" },
            identity => identity with { Depots = identity.Depots.Select((depot, index) => index == 0 ? depot with { ManifestId = "123" } : depot).ToArray() }
        })
        {
            var bundle = await BuildBundle(fixture, mode: "RuntimeOnly", changeIdentity: change);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().RunAsync(BundleRequest(fixture, bundle, InstallationMode.RuntimeOnly)));
            fixture.AssertGameUnchanged();
        }
        var withoutRussian = await BuildBundle(fixture, mode: "RuntimeOnly");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().RunAsync(BundleRequest(fixture, withoutRussian, InstallationMode.RuntimeOnly, includeRussian: true)));
        Assert.Equal(0, fixture.DownloadCalls);
        fixture.AssertGameUnchanged();
    }

    private static async Task FullTargetSafeguard()
    {
        using var fixture = new Fixture();
        var runtime = await BuildBundle(fixture, "RuntimeOnly");
        var incomplete = await BuildBundle(fixture, executableOnly: true);
        foreach (var bundle in new[] { runtime, incomplete })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().RunAsync(BundleRequest(fixture, bundle)));
            fixture.AssertGameUnchanged();
        }
        Assert.Equal(0, fixture.DownloadCalls);
    }

    private static async Task BundleSourceBinding()
    {
        using var fixture = new Fixture();
        var external = Path.Combine(fixture.Root, "other-game");
        Fixture.Write(external, "SkyrimSE.exe", "new executable");
        var sources = FileMap(fixture.Game);
        sources["SkyrimSE.exe"] = Path.Combine(external, "SkyrimSE.exe");
        var bundle = await BuildBundle(fixture, mode: "RuntimeOnly", sourceFiles: sources);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().RunAsync(BundleRequest(fixture, bundle, InstallationMode.RuntimeOnly)));
        fixture.AssertGameUnchanged();
        Assert.Equal(0, fixture.DownloadCalls);
    }

    private static async Task BundleContentMismatch()
    {
        using var fixture = new Fixture();
        Fixture.Write(fixture.Source, "SkyrimSE.exe", "old executable");
        var bundle = await BuildBundle(fixture, mode: "RuntimeOnly");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().RunAsync(BundleRequest(fixture, bundle, InstallationMode.RuntimeOnly) with
        { TransitionBundleSha256 = new string('0', 64) }));
        fixture.AssertGameUnchanged();
        Fixture.Write(fixture.Game, "SkyrimSE.exe", "changed source executable");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().RunAsync(BundleRequest(fixture, bundle, InstallationMode.RuntimeOnly)));
        Assert.Equal("changed source executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("old master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.True(!Directory.Exists(fixture.Transactions));
    }

    private static async Task IndependentCopy()
    {
        using var fixture = new Fixture();
        var copy = Path.Combine(fixture.Root, "independent Skyrim");
        Fixture.Write(copy, "SkyrimSE.exe", "copy executable");
        Fixture.Write(copy, "Data/Skyrim.esm", "copy master");
        Fixture.Write(copy, "Data/copy.esp", "copy mod");
        var service = fixture.CreateService(versionReader: root => root == copy ? "1.6.1170" : "1.5.97.0");
        var result = await service.RunAsync(fixture.Request(false) with { GameDirectory = copy });
        Assert.Equal(copy, fixture.LastDownload!.InstalledGameDirectory);
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(copy, "SkyrimSE.exe")));
        Assert.Equal("new master", File.ReadAllText(Path.Combine(copy, "Data", "Skyrim.esm")));
        Assert.Equal("copy mod", File.ReadAllText(Path.Combine(copy, "Data", "copy.esp")));
        Assert.Equal("old executable", fixture.ReadGame("SkyrimSE.exe"));
        Assert.Equal("old master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal("", result.Installation.TransactionDirectory);
        Assert.Equal(0, (await new PatchEngine().ListDiagnosticsAsync(fixture.Transactions)).Count);
    }

    private static async Task RuntimeMissingIdentity()
    {
        using var fixture = new Fixture();
        var bundle = await BuildBundle(fixture, "RuntimeOnly");
        var metadata = await BinaryTransitionBundle.InspectAsync(bundle.Path, bundle.Sha256);
        var packageDirectory = Path.Combine(fixture.Root, "built-in-transitions");
        Directory.CreateDirectory(packageDirectory);
        var fileName = Path.GetFileName(bundle.Path);
        File.Copy(bundle.Path, Path.Combine(packageDirectory, fileName));
        var sourceReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("old executable")));
        var index = new TransitionPackageIndex(1, [new(fileName, bundle.Sha256, metadata.Identity,
            [new("SkyrimSE.exe", sourceReference, metadata.Files.Single().TargetSha256)])]);
        File.WriteAllText(Path.Combine(packageDirectory, "index.json"), JsonSerializer.Serialize(index,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var transaction = fixture.MakeJournal("Applying", fixture.Game);
        File.Delete(Path.Combine(fixture.Game, "SkyrimSE.exe"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService(packages: new(packageDirectory),
            versionReader: root => root == fixture.Game ? "1.6.1170.0" : "1.5.97.0").RunAsync(fixture.Request(false) with
        { Mode = InstallationMode.RuntimeOnly }));
        Assert.True(error.Message.Contains("неизвестная версия", StringComparison.Ordinal));
        Assert.Equal(0, fixture.DownloadCalls);
        Assert.True(!File.Exists(Path.Combine(fixture.Game, "SkyrimSE.exe")));
        Assert.Equal("old master", fixture.ReadGame("Data/Skyrim.esm"));
        Assert.Equal("keep mod", fixture.ReadGame("Data/custom.esp"));
        Assert.Equal("Interrupted", (await new PatchEngine().ListDiagnosticsAsync(fixture.Transactions))
            .Single(item => item.TransactionDirectory == transaction).State);
    }

    private static async Task PrivateRevisionMismatch()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService(versionReader: root =>
            root == fixture.Game ? "1.6.1170" : "1.5.97.1").RunAsync(fixture.Request()));
        fixture.AssertGameUnchanged();
        var bundle = await BuildBundle(fixture, mode: "RuntimeOnly");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService(versionReader: root =>
            root == fixture.Game ? "1.6.1170.1" : "1.5.97.0").RunAsync(BundleRequest(fixture, bundle, InstallationMode.RuntimeOnly)));
        fixture.AssertGameUnchanged();
    }

    private sealed class Observer(Action<AutomaticPatchProgress> report) : IProgress<AutomaticPatchProgress>
    {
        public void Report(AutomaticPatchProgress value) => report(value);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "SkyrimVersionPatcher-workflow-tests", Guid.NewGuid().ToString("N"));
            Write(Game, "SkyrimSE.exe", "old executable");
            Write(Game, "Data/Skyrim.esm", "old master");
            Write(Game, "Data/custom.esp", "keep mod");
            Write(Source, "SkyrimSE.exe", "new executable");
            Write(Source, "Data/Skyrim.esm", "new master");
            Write(Source, "Skyrim_Default.ini", "english");
            Write(Russian, "Skyrim_Default.ini", "russian");
            Write(Russian, "Data/russian.bsa", "russian archive");
        }
        public string Root { get; }
        public string Game => Path.Combine(Root, "game");
        public string Source => Path.Combine(Root, "source");
        public string Russian => Path.Combine(Root, "russian");
        public string Storage => Path.Combine(Root, "storage");
        public string Transactions => Path.Combine(Storage, "Transactions");
        public string Profile => Path.Combine(Root, "profile");
        public string CatalogPath => Path.Combine(Profile, "ContentCatalog.txt");
        public bool FromCache { get; set; }
        public int DownloadCalls { get; private set; }
        public DownloadRequest? LastDownload { get; private set; }
        public AutomaticPatchRequest Request(bool includeRussian = true) => new(Game, Storage,
            VersionCatalogService.LoadBuiltIn().Single(version => version.Id == "1.5.97"), includeRussian);
        public string ReadGame(string relative) => File.ReadAllText(Path.Combine(Game, relative));
        public string? VersionReader(string root) => root == Game && ReadGame("SkyrimSE.exe") != "new executable" ? "1.6.1170" : "1.5.97.0";
        public AutomaticPatchService CreateService(PatchEngine? engine = null, Func<DownloadResult, DownloadResult>? transform = null,
            Func<string, string?>? versionReader = null, Action? beforeDownload = null, Func<string?>? profileDirectory = null,
            Func<string, CancellationToken, Task<ExecutableIdentity>>? identify = null, TransitionPackageStore? packages = null) =>
            new(engine ?? new PatchEngine(), (request, progress, token) =>
            {
                DownloadCalls++;
                LastDownload = request;
                beforeDownload?.Invoke();
                var result = CreateDownload(Request(request.Depots.Any(depot => depot.IsRussian)));
                if (request.ForceFreshDownload)
                {
                    var stagedSources = result.SourceDirectories.Select((source, index) =>
                    {
                        var staged = Path.Combine(request.CacheDirectory, "downloaded-" + index);
                        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                        {
                            var destination = Path.Combine(staged, Path.GetRelativePath(source, file));
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            File.Copy(file, destination);
                        }
                        return staged;
                    }).ToArray();
                    result = result with { SourceDirectories = stagedSources };
                }
                result = transform?.Invoke(result) ?? result;
                if (result.Statistics is not null) progress?.Report(new("Verified download statistics", Statistics: result.Statistics));
                return Task.FromResult(result);
            }, versionReader ?? VersionReader, profileDirectory, identify, packages);
        public byte[] WriteModernCatalog() => WriteCatalog("\r\n{\r\n  \"CSV2_016105c0-50a6-4beb-851a-6d0f85c6e5f0\": { \"Files\": [\"cceejsse001-hstead.esm\"], \"Title\": \"Русское название\" }\r\n}\r\n");
        public byte[] WriteCatalog(string content)
        {
            Directory.CreateDirectory(Profile);
            var bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(CatalogPath, bytes);
            return bytes;
        }
        public void AssertCatalogUnchanged(byte[] expected)
        {
            Assert.True(File.Exists(CatalogPath));
            Assert.True(File.ReadAllBytes(CatalogPath).SequenceEqual(expected));
            Assert.Equal(1, Directory.EnumerateFiles(Profile, "*", SearchOption.AllDirectories).Count());
        }
        public DownloadResult CreateDownload(AutomaticPatchRequest request)
        {
            string[] sources = request.IncludeRussianLocalization ? [Source, Russian] : [Source];
            var inventory = new Dictionary<string, DownloadedFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                inventory[relative] = new(relative, new FileInfo(file).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            }
            return new(sources, VersionCatalogService.GetDepots(request.Version, request.IncludeRussianLocalization), inventory.Values.ToArray(), FromCache);
        }
        public string MakeJournal(string state, string targetGame, int affected = 1)
        {
            var transaction = Path.Combine(Transactions, "transaction-seeded");
            Write(transaction, "journal.json", JsonSerializer.Serialize(new
            {
                FormatVersion = 4, TransactionId = Guid.NewGuid().ToString("N"), Sequence = 1,
                GameDirectory = targetGame, CreatedAt = DateTimeOffset.UtcNow, State = state,
                AffectedCount = affected, Files = new[] { new { RelativePath = "SkyrimSE.exe" } }
            }));
            return transaction;
        }
        public void AssertGameUnchanged()
        {
            Assert.Equal("old executable", ReadGame("SkyrimSE.exe"));
            Assert.Equal("old master", ReadGame("Data/Skyrim.esm"));
            Assert.True(!Directory.Exists(Transactions));
        }
        public static void Write(string root, string path, string content)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SkyrimVersionPatcher-workflow-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup.");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
