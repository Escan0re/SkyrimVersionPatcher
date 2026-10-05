using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Downloading;
using SkyrimVersionPatcher.Core.Transitions;

namespace SkyrimVersionPatcher.Tests;

public static class ReuseAndTransitionTests
{
    private static readonly BinaryTransitionIdentity Identity = new("1.5.97", "1.6.1170", false, "Complete", [new(489833, "10", false)]);

    public static void Register(TestSuite suite)
    {
        suite.Add("Blocks: public Steam manifest retains validated reordered chunk offsets", () =>
        {
            using var stream = Manifest("Data/test.bsa", "AAAABBBB", [new(4, 4, Sha1("BBBB")), new(0, 4, Sha1("AAAA"))]);
            var files = SteamManifestChunkReader.Read(stream, 489833, 10);
            Assert.Equal(1, files.Count); Assert.Equal(2, files[0].Chunks.Count);
            Assert.Equal(0L, files[0].Chunks[0].Offset); Assert.Equal(4L, files[0].Chunks[1].Offset);
            Assert.Equal(Sha1("AAAABBBB"), files[0].Sha1);
        });
        suite.Add("Blocks: manifest rejects gap overlap oversized and truncated chunks", () =>
        {
            SteamManifestChunk[][] invalid = [
                [new(1, 8, Sha1("AAAABBBB"))],
                [new(0, 4, Sha1("AAAA")), new(3, 4, Sha1("BBBB"))],
                [new(0, 4, Sha1("AAAA"))],
                [new(0, SteamManifestChunkReader.MaximumChunkLength + 1, Sha1("AAAA"))]];
            foreach (var chunks in invalid)
            {
                using var stream = Manifest("Data/test.bsa", "AAAABBBB", chunks);
                Assert.Throws<InvalidDataException>(() => SteamManifestChunkReader.Read(stream, 489833, 10));
            }
        });
        suite.Add("Blocks: changed BSA reuses authenticated ranges from another file and offset", async () =>
        {
            using var fixture = new Fixture();
            var sourceFile = fixture.Write("installed", "Data/old.bsa", "CCCCAAAABBBB");
            var source = ChunkFile("Data/old.bsa", "CCCCAAAABBBB", "CCCC", "AAAA", "BBBB");
            var target = ChunkFile("Data/new.bsa", "BBBBAAAA", "BBBB", "AAAA");
            var plan = await SteamChunkReuseService.AnalyzeAsync([target], [new(Path.GetDirectoryName(Path.GetDirectoryName(sourceFile)!)!, [source])]);
            Assert.True(plan.IsComplete); Assert.Equal(8L, plan.ReusedBytes); Assert.Equal(0L, plan.MissingBytes);
            Assert.Equal(8L, plan.Files[0].Chunks[0].SourceOffset);
            var staging = Path.Combine(fixture.Root, "staged");
            var files = await SteamChunkReuseService.PrepareAsync(plan, staging);
            Assert.Equal("BBBBAAAA", File.ReadAllText(Path.Combine(staging, "Data", "new.bsa")));
            Assert.Equal(Sha256("BBBBAAAA"), files[0].Sha256);
            Assert.Equal("CCCCAAAABBBB", File.ReadAllText(sourceFile));
        });
        suite.Add("Blocks: damaged source range stays missing and cannot produce a partial depot", async () =>
        {
            using var fixture = new Fixture();
            fixture.Write("installed", "Data/a.bsa", "AAAAXXXX");
            var target = ChunkFile("Data/a.bsa", "AAAABBBB", "AAAA", "BBBB");
            var plan = await SteamChunkReuseService.AnalyzeAsync([target], [new(Path.Combine(fixture.Root, "installed"), [target])]);
            Assert.Equal(4L, plan.ReusedBytes); Assert.Equal(4L, plan.MissingBytes);
            var staging = Path.Combine(fixture.Root, "missing-stage");
            await Assert.ThrowsAsync<InvalidOperationException>(() => SteamChunkReuseService.PrepareAsync(plan, staging));
            Assert.True(!Directory.Exists(staging));
        });
        suite.Add("Blocks: sources changing after planning fail staging without touching installed files", async () =>
        {
            using var fixture = new Fixture();
            var path = fixture.Write("installed", "SkyrimSE.exe", "AAAABBBB");
            var target = ChunkFile("SkyrimSE.exe", "AAAABBBB", "AAAA", "BBBB");
            var plan = await SteamChunkReuseService.AnalyzeAsync([target], [new(Path.Combine(fixture.Root, "installed"), [target])]);
            File.WriteAllText(path, "AAAAXXXX");
            await Assert.ThrowsAsync<InvalidDataException>(() => SteamChunkReuseService.PrepareAsync(plan, Path.Combine(fixture.Root, "stage")));
            Assert.Equal("AAAAXXXX", File.ReadAllText(path));
        });
        suite.Add("Blocks: files without chunk descriptors are reused only by complete SHA1", async () =>
        {
            using var fixture = new Fixture();
            fixture.Write("installed", "SkyrimSE.exe", "test");
            SteamChunkFile file = new("SkyrimSE.exe", 4, Sha1("test"), []);
            var plan = await SteamChunkReuseService.AnalyzeAsync([file], [new(Path.Combine(fixture.Root, "installed"), [file])]);
            Assert.True(plan.IsComplete);
            var result = await SteamChunkReuseService.PrepareAsync(plan, Path.Combine(fixture.Root, "stage"));
            Assert.Equal(Sha256("test"), result.Single().Sha256);
        });
        suite.Add("Blocks: malicious paths and overlapping public plans fail before creating staging", async () =>
        {
            using var fixture = new Fixture();
            var invalid = new SteamChunkFile("../escape.bsa", 1, Sha1("x"), []);
            await Assert.ThrowsAsync<InvalidDataException>(() => SteamChunkReuseService.AnalyzeAsync([invalid], []));
            var source = fixture.Write("source", "file", "x");
            var target = new SteamChunkFile("file", 1, Sha1("x"), []);
            var plan = await SteamChunkReuseService.AnalyzeAsync([target], [new(Path.Combine(fixture.Root, "source"), [target])]);
            var forged = plan with { Files = [plan.Files[0] with { Chunks = [plan.Files[0].Chunks[0] with { TargetOffset = 1 }] }] };
            var staging = Path.Combine(fixture.Root, "stage");
            await Assert.ThrowsAsync<InvalidDataException>(() => SteamChunkReuseService.PrepareAsync(forged, staging));
            Assert.True(!Directory.Exists(staging)); Assert.Equal("x", File.ReadAllText(source));
        });
        suite.Add("Transition: builder and reader roundtrip relocated blocks new data and empty files", async () =>
        {
            using var fixture = new Fixture();
            var a = Enumerable.Repeat((byte)'A', 65536).ToArray();
            var b = Enumerable.Repeat((byte)'B', 65536).ToArray();
            var c = Enumerable.Repeat((byte)'C', 65536).ToArray();
            var source = fixture.WriteBytes("source", "Data/test.bsa", [.. a, .. b]);
            var target = fixture.WriteBytes("target", "Data/test.bsa", [.. b, .. a, .. c]);
            var empty = fixture.Write("target", "empty", "");
            var package = await BinaryTransitionBuilder.BuildAsync(Path.Combine(fixture.Root, "patch.svpt"), Identity,
                new Dictionary<string, string> { ["Data/test.bsa"] = source },
                new Dictionary<string, string> { ["Data/test.bsa"] = target, ["empty"] = empty });
            Assert.Equal(131072L, package.ReusedBytes); Assert.Equal(65536L, package.PayloadBytes);
            var staging = Path.Combine(fixture.Root, "stage");
            var result = await BinaryTransitionBundle.PrepareAsync(package.Path,
                new Dictionary<string, string> { ["Data/test.bsa"] = source }, staging, Identity, package.Sha256);
            Assert.Equal(package.TargetBytes, result.WrittenBytes); Assert.Equal(package.ReusedBytes, result.ReusedBytes);
            Assert.True(File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(Path.Combine(staging, "Data", "test.bsa"))));
            Assert.Equal(0L, new FileInfo(Path.Combine(staging, "empty")).Length);
            Assert.Equal(131072L, new FileInfo(source).Length);
        });
        suite.Add("Transition: adjacent unchanged ranges coalesce and package does not duplicate full files", async () =>
        {
            using var fixture = new Fixture();
            var bytes = Enumerable.Repeat((byte)'A', 4 * 65536).ToArray();
            var source = fixture.WriteBytes("source", "SkyrimSE.exe", bytes);
            var target = fixture.WriteBytes("target", "SkyrimSE.exe", bytes);
            var package = await BinaryTransitionBuilder.BuildAsync(Path.Combine(fixture.Root, "patch.svpt"), Identity,
                new Dictionary<string, string> { ["SkyrimSE.exe"] = source }, new Dictionary<string, string> { ["SkyrimSE.exe"] = target });
            var metadata = await BinaryTransitionBundle.InspectAsync(package.Path, package.Sha256);
            Assert.Equal(1, metadata.Files.Single().Segments.Count); Assert.Equal(0L, package.PayloadBytes);
            Assert.True(new FileInfo(package.Path).Length < 4096);
        });
        suite.Add("Transition: wrong source hash rejects all inputs before creating staging", async () =>
        {
            using var fixture = new Fixture();
            var source = fixture.Write("source", "SkyrimSE.exe", "same");
            var target = fixture.Write("target", "SkyrimSE.exe", "same");
            var package = await BinaryTransitionBuilder.BuildAsync(Path.Combine(fixture.Root, "patch.svpt"), Identity,
                new Dictionary<string, string> { ["SkyrimSE.exe"] = source }, new Dictionary<string, string> { ["SkyrimSE.exe"] = target });
            File.WriteAllText(source, "nope");
            var staging = Path.Combine(fixture.Root, "stage");
            await Assert.ThrowsAsync<InvalidDataException>(() => BinaryTransitionBundle.PrepareAsync(package.Path,
                new Dictionary<string, string> { ["SkyrimSE.exe"] = source }, staging, Identity, package.Sha256));
            Assert.True(!Directory.Exists(staging)); Assert.Equal("nope", File.ReadAllText(source));
        });
        suite.Add("Transition: all-literal executable deltas remain bound to the complete original source hash", async () =>
        {
            using var fixture = new Fixture();
            var source = fixture.Write("source", "SkyrimSE.exe", "old-exe");
            var target = fixture.Write("target", "SkyrimSE.exe", "new-exe");
            var package = await BinaryTransitionBuilder.BuildAsync(Path.Combine(fixture.Root, "patch.svpt"), Identity,
                new Dictionary<string, string> { ["SkyrimSE.exe"] = source }, new Dictionary<string, string> { ["SkyrimSE.exe"] = target });
            Assert.Equal(0L, package.ReusedBytes);
            var metadata = await BinaryTransitionBundle.InspectAsync(package.Path, package.Sha256);
            Assert.Equal("SkyrimSE.exe", metadata.Files.Single().SourceRelativePath);
            Assert.Equal(Sha256("old-exe"), metadata.Files.Single().SourceSha256);
            File.WriteAllText(source, "mod-exe");
            var staging = Path.Combine(fixture.Root, "stage");
            await Assert.ThrowsAsync<InvalidDataException>(() => BinaryTransitionBundle.PrepareAsync(package.Path,
                new Dictionary<string, string> { ["SkyrimSE.exe"] = source }, staging, Identity, package.Sha256));
            Assert.True(!Directory.Exists(staging)); Assert.Equal("mod-exe", File.ReadAllText(source));
        });
        suite.Add("Transition: package digest selected version localization and mode are pinned", async () =>
        {
            using var fixture = new Fixture();
            var target = fixture.Write("target", "SkyrimSE.exe", "new");
            var package = await BinaryTransitionBuilder.BuildAsync(Path.Combine(fixture.Root, "patch.svpt"), Identity,
                new Dictionary<string, string>(), new Dictionary<string, string> { ["SkyrimSE.exe"] = target });
            await Assert.ThrowsAsync<InvalidDataException>(() => BinaryTransitionBundle.InspectAsync(package.Path, new string('0', 64)));
            await Assert.ThrowsAsync<InvalidDataException>(() => BinaryTransitionBundle.PrepareAsync(package.Path,
                new Dictionary<string, string>(), Path.Combine(fixture.Root, "stage"),
                Identity with { SourceVersion = "1.6.1170", TargetVersion = "1.5.97" }, package.Sha256));
            Assert.True(!Directory.Exists(Path.Combine(fixture.Root, "stage")));
        });
        suite.Add("Transition: traversal extra ZIP entries and duplicate payload entries are rejected", async () =>
        {
            using var fixture = new Fixture();
            var metadata = ValidLiteralMetadata("SkyrimSE.exe", "target");
            foreach (var extra in new[] { "../escape", "unused.txt", "payload/" + new string('0', 64), "payload/" + Sha256("target") })
            {
                var package = fixture.Archive(metadata, "target", extra);
                await Assert.ThrowsAsync<InvalidDataException>(() => BinaryTransitionBundle.InspectAsync(package.Path, package.Sha256));
            }
            var unsafeMetadata = ValidLiteralMetadata("../escape", "target");
            var invalid = fixture.Archive(unsafeMetadata, "target");
            await Assert.ThrowsAsync<InvalidDataException>(() => BinaryTransitionBundle.InspectAsync(invalid.Path, invalid.Sha256));
        });
        suite.Add("Transition: corrupted literal ranges fail before installation", async () =>
        {
            using var fixture = new Fixture();
            var metadata = ValidLiteralMetadata("SkyrimSE.exe", "target");
            var package = fixture.Archive(metadata, "broken");
            await Assert.ThrowsAsync<InvalidDataException>(() => BinaryTransitionBundle.PrepareAsync(package.Path,
                new Dictionary<string, string>(), Path.Combine(fixture.Root, "stage"), Identity, package.Sha256));
            Assert.True(!File.Exists(Path.Combine(fixture.Root, "SkyrimSE.exe")));
        });
        suite.Add("Transition: source offsets beyond input and overflowed target sizes reject metadata", async () =>
        {
            using var fixture = new Fixture();
            BinaryTransitionFile bad = new("SkyrimSE.exe", 1, Sha256("a"), "SkyrimSE.exe", 1, Sha256("a"), [new(1, Sha256("a"), 1)]);
            var package = fixture.Archive(new(1, Identity, [bad]), null);
            await Assert.ThrowsAsync<InvalidDataException>(() => BinaryTransitionBundle.InspectAsync(package.Path, package.Sha256));
        });
        suite.Add("Transition: runtime mode permits only explicit executable launcher and shader subset", async () =>
        {
            using var fixture = new Fixture();
            var runtime = Identity with { Mode = "RuntimeOnly" };
            var wrong = fixture.Archive(ValidLiteralMetadata("Data/Skyrim.esm", "target") with { Identity = runtime }, "target");
            await Assert.ThrowsAsync<InvalidDataException>(() => BinaryTransitionBundle.InspectAsync(wrong.Path, wrong.Sha256));
            var valid = fixture.Archive(ValidLiteralMetadata("SkyrimSE.exe", "target") with { Identity = runtime }, "target");
            Assert.Equal("RuntimeOnly", (await BinaryTransitionBundle.InspectAsync(valid.Path, valid.Sha256)).Identity.Mode);
        });
        suite.Add("Transition: cancelled preparation never writes to game source", async () =>
        {
            using var fixture = new Fixture();
            var source = fixture.Write("source", "SkyrimSE.exe", "same");
            var target = fixture.Write("target", "SkyrimSE.exe", "same");
            var package = await BinaryTransitionBuilder.BuildAsync(Path.Combine(fixture.Root, "patch.svpt"), Identity,
                new Dictionary<string, string> { ["SkyrimSE.exe"] = source }, new Dictionary<string, string> { ["SkyrimSE.exe"] = target });
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => BinaryTransitionBundle.PrepareAsync(package.Path,
                new Dictionary<string, string> { ["SkyrimSE.exe"] = source }, Path.Combine(fixture.Root, "stage"), Identity, package.Sha256,
                cancellationToken: cancellation.Token));
            Assert.Equal("same", File.ReadAllText(source));
        });
    }

    private static BinaryTransitionMetadata ValidLiteralMetadata(string relative, string text) => new(1, Identity,
        [new(relative.Replace('/', Path.DirectorySeparatorChar), Encoding.UTF8.GetByteCount(text), Sha256(text), null, null, null,
            [new(Encoding.UTF8.GetByteCount(text), Sha256(text), PayloadEntry: "payload/" + Sha256(text))])]);
    private static string Sha1(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static SteamChunkFile ChunkFile(string path, string whole, params string[] blocks)
    {
        long offset = 0;
        var chunks = blocks.Select(block => { var chunk = new SteamManifestChunk(offset, block.Length, Sha1(block)); offset += block.Length; return chunk; }).ToArray();
        return new(path.Replace('/', Path.DirectorySeparatorChar), whole.Length, Sha1(whole), chunks);
    }
    private static MemoryStream Manifest(string name, string text, IReadOnlyList<SteamManifestChunk> chunks)
    {
        using var file = new MemoryStream();
        Bytes(file, 1, Encoding.UTF8.GetBytes(name)); VarintField(file, 2, (ulong)text.Length); Bytes(file, 5, SHA1.HashData(Encoding.UTF8.GetBytes(text)));
        foreach (var chunk in chunks)
        {
            using var data = new MemoryStream(); Bytes(data, 1, Convert.FromHexString(chunk.Sha1));
            VarintField(data, 3, (ulong)chunk.Offset); VarintField(data, 4, (ulong)chunk.Length); Bytes(file, 6, data.ToArray());
        }
        using var payload = new MemoryStream(); Bytes(payload, 1, file.ToArray());
        using var metadata = new MemoryStream(); VarintField(metadata, 1, 489833); VarintField(metadata, 2, 10);
        var result = new MemoryStream(); using var writer = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true);
        foreach (var (magic, data) in new[] { (0x71F617D0u, payload.ToArray()), (0x1F4812BEu, metadata.ToArray()), (0x1B81B817u, Array.Empty<byte>()) })
        { writer.Write(magic); writer.Write(data.Length); writer.Write(data); }
        writer.Write(0x32C415ABu); writer.Flush(); result.Position = 0; return result;
    }
    private static void Bytes(Stream stream, int field, byte[] value) { Varint(stream, (ulong)(field << 3 | 2)); Varint(stream, (ulong)value.Length); stream.Write(value); }
    private static void VarintField(Stream stream, int field, ulong value) { Varint(stream, (ulong)(field << 3)); Varint(stream, value); }
    private static void Varint(Stream stream, ulong value) { while (value >= 128) { stream.WriteByte((byte)(value | 128)); value >>= 7; } stream.WriteByte((byte)value); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "svpt-reuse-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public string Write(string directory, string relative, string value) => WriteBytes(directory, relative, Encoding.UTF8.GetBytes(value));
        public string WriteBytes(string directory, string relative, byte[] bytes)
        {
            var path = Path.Combine(Root, directory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return path;
        }
        public BinaryTransitionBuildResult Archive(BinaryTransitionMetadata metadata, string? payload, string? extra = null)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".svpt");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var output = archive.CreateEntry("metadata.json").Open()) JsonSerializer.Serialize(output, metadata);
                if (payload is not null)
                {
                    using var output = archive.CreateEntry(metadata.Files[0].Segments[0].PayloadEntry!).Open();
                    output.Write(Encoding.UTF8.GetBytes(payload));
                }
                if (extra is not null) { using var output = archive.CreateEntry(extra).Open(); output.WriteByte(1); }
            }
            using var input = File.OpenRead(path); return new(path, Convert.ToHexString(SHA256.HashData(input)), 0, 0, 0);
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("svpt-reuse-", StringComparison.Ordinal))
                Directory.Delete(full, true);
        }
    }
}
