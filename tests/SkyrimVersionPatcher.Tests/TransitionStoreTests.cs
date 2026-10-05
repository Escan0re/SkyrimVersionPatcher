using System.Text.Json;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Downloading;
using SkyrimVersionPatcher.Core.Transitions;

namespace SkyrimVersionPatcher.Tests;

public static class TransitionStoreTests
{
    private static readonly BinaryTransitionIdentity Identity = new("1.5.97", "1.6.1170", false, "RuntimeOnly", [new(489833, "10", false)]);
    private static readonly TransitionPackageIndexEntry Entry = new("forward.svpt", new string('A', 64), Identity,
        [new("SkyrimSE.exe", new string('B', 64), new string('C', 64))], "https://www.nexusmods.com/skyrimspecialedition/mods/188916");

    public static void Register(TestSuite suite)
    {
        suite.Add("Transition store: exact source target mode RU and manifest find a pinned local package", () =>
        {
            using var fixture = new Fixture(); fixture.Save(new(1, [Entry]));
            var found = fixture.Store.Find("1.5.97", "1.6.1170", "RuntimeOnly", false, Identity.Depots);
            Assert.Equal(Path.Combine(fixture.Root, "forward.svpt"), found!.Path); Assert.Equal(Entry.Sha256, found.Sha256);
            Assert.Equal(Entry.Files[0].TargetSha256, found.Files[0].TargetSha256);
            Assert.True(fixture.Store.Find("1.0.0", "1.6.1170", "RuntimeOnly", false, Identity.Depots) is null);
            Assert.True(fixture.Store.Find("1.5.97", "1.6.1170", "Complete", false, Identity.Depots) is null);
            Assert.True(fixture.Store.Find("1.5.97", "1.6.1170", "RuntimeOnly", false, [new(489833, "11", false)]) is null);
        });
        suite.Add("Transition store: missing index is offline and empty", () =>
        { using var fixture = new Fixture(); Assert.Equal(0, fixture.Store.Load().Count); });
        suite.Add("Transition store: paths aliases foreign files and hashes are rejected", () =>
        {
            using var fixture = new Fixture();
            TransitionPackageIndexEntry[] invalid = [Entry with { FileName = "../bad.svpt" }, Entry with { FileName = "C:bad.svpt" },
                Entry with { FileName = "bad.svpt " }, Entry with { Sha256 = "short" },
                Entry with { Files = [new("Data/Skyrim.esm", new string('B', 64), new string('C', 64))] },
                Entry with { Files = [new("../escape.exe", new string('B', 64), new string('C', 64))] },
                Entry with { Files = [new("SkyrimSE.exe", "invalid", new string('C', 64))] },
                Entry with { SourceUrl = "http://example.test/untrusted" }, Entry with { FileName = "CON.svpt" }];
            foreach (var entry in invalid) { fixture.Save(new(1, [entry])); Assert.Throws<InvalidDataException>(() => fixture.Store.Load()); }
        });
        suite.Add("Transition store: repeated identity filename unknown and duplicate JSON members are rejected", () =>
        {
            using var fixture = new Fixture(); fixture.Save(new(1, [Entry, Entry with { FileName = "duplicate.svpt" }]));
            Assert.Throws<InvalidDataException>(() => fixture.Store.Load());
            fixture.Save(new(1, [Entry, Entry with { Identity = Identity with { SourceVersion = "1.6.1170", TargetVersion = "1.5.97" } }]));
            Assert.Throws<InvalidDataException>(() => fixture.Store.Load());
            File.WriteAllText(Path.Combine(fixture.Root, "index.json"), "{\"schemaVersion\":1,\"schemaVersion\":1,\"packages\":[]}");
            Assert.Throws<InvalidDataException>(() => fixture.Store.Load());
            File.WriteAllText(Path.Combine(fixture.Root, "index.json"), "{\"schemaVersion\":1,\"packages\":[],\"extra\":true}");
            Assert.Throws<InvalidDataException>(() => fixture.Store.Load());
        });
        suite.Add("Transition store: oversized index is rejected before parsing", () =>
        {
            using var fixture = new Fixture(); File.WriteAllText(Path.Combine(fixture.Root, "index.json"), new string(' ', 1024 * 1024 + 1));
            Assert.Throws<InvalidDataException>(() => fixture.Store.Load());
        });
        suite.Add("Transition store: null schemas and null depot entries fail with diagnostics", () =>
        {
            using var fixture = new Fixture();
            foreach (var json in new[] { "null", "{\"schemaVersion\":1,\"packages\":null}", "{\"schemaVersion\":2,\"packages\":[]}" })
            { File.WriteAllText(Path.Combine(fixture.Root, "index.json"), json); Assert.Throws<InvalidDataException>(() => fixture.Store.Load()); }
            fixture.Save(new(1, [Entry with { Identity = Identity with { Depots = [null!] } }]));
            Assert.Throws<InvalidDataException>(() => fixture.Store.Load());
        });
        suite.Add("Transition store: literal executables still require exact source subset hashes", async () =>
        {
            using var fixture = new Fixture();
            var executable = Path.Combine(fixture.Root, "SkyrimSE.exe"); File.WriteAllText(executable, "source");
            using var input = File.OpenRead(executable);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)); input.Dispose();
            var record = new TransitionPackageRecord(Path.Combine(fixture.Root, "forward.svpt"), Entry.Sha256, Identity,
                [new("SkyrimSE.exe", hash, new string('C', 64))]);
            var files = new Dictionary<string, string> { ["SkyrimSE.exe"] = executable };
            await TransitionPackageStore.ValidateSourceAsync(record, files);
            File.WriteAllText(executable, "modded");
            await Assert.ThrowsAsync<InvalidDataException>(() => TransitionPackageStore.ValidateSourceAsync(record, files));
        });
        suite.Add("Transition store: result inventory requires all exact target hashes and paths", () =>
        {
            var record = new TransitionPackageRecord("unused", Entry.Sha256, Identity, Entry.Files);
            TransitionPackageStore.ValidatePrepared(record, [new("SkyrimSE.exe", 1, new string('C', 64))]);
            Assert.Throws<InvalidDataException>(() => TransitionPackageStore.ValidatePrepared(record, []));
            Assert.Throws<InvalidDataException>(() => TransitionPackageStore.ValidatePrepared(record, [new("SkyrimSE.exe", 1, new string('B', 64))]));
            Assert.Throws<InvalidDataException>(() => TransitionPackageStore.ValidatePrepared(record, [new("SkyrimSE.exe", 1, new string('C', 64)), new("extra", 1, new string('C', 64))]));
        });
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "svpt-store-" + Guid.NewGuid().ToString("N"));
        public TransitionPackageStore Store => new(Root);
        public Fixture() => Directory.CreateDirectory(Root);
        public void Save(TransitionPackageIndex index) => File.WriteAllText(Path.Combine(Root, "index.json"),
            JsonSerializer.Serialize(index, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("svpt-store-", StringComparison.Ordinal))
                Directory.Delete(full, true);
        }
    }
}
