using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Downloading;
using SkyrimVersionPatcher.Core.Identity;

namespace SkyrimVersionPatcher.Tests;

public static class ExecutableIdentityTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Identity: built-in database does not claim unverified executable hashes", () =>
        {
            Assert.Equal(0, ExecutableIdentityService.LoadBuiltIn().Count);
        });
        suite.Add("Identity metadata: Skyrim resource strings override stale fixed version fields", () =>
        {
            foreach (var version in new[] { "1.5.97", "1.6.1170" })
                Assert.Equal(version, ExecutableIdentityService.ResolveMetadataVersion(version + ".0", version + ".0", "1.0.0.0"));
            Assert.Equal("1.5.97", ExecutableIdentityService.ResolveMetadataVersion("1.5.97.0", "1.6.1170.0", "1.0.0.0"));
            Assert.Equal("1.0.0", ExecutableIdentityService.ResolveMetadataVersion("1.0.0.0", "1.5.97.0", "1.5.97.0"));
            Assert.Equal("1.7.104", ExecutableIdentityService.ResolveMetadataVersion(" \t1.7.104.0\r\n", null, null));
        });
        suite.Add("Identity metadata: malformed strings fall back without accepting partial numeric prefixes", () =>
        {
            foreach (var invalid in new string?[]
            {
                null, "", " \r\n\t ", "1.5", "1.5.97.0.1", "1.5.97 release", "1.5.97,0", "1,5,97,0",
                "01.5.97", "1.05.97", "1.5.097", "1.5.97.00", "+1.5.97", "-1.5.97", "0.5.97",
                "1.-5.97", "1.5.2147483648", "1.5.97\0", "１.５.９７", "1.5.97" + new string(' ', 59)
            })
            {
                Assert.Equal("1.5.97", ExecutableIdentityService.ResolveMetadataVersion(invalid, "1.5.97.0", "1.0.0.0"));
                Assert.Equal("1.6.1170", ExecutableIdentityService.ResolveMetadataVersion(invalid, invalid, "1.6.1170.0"));
                Assert.True(ExecutableIdentityService.ResolveMetadataVersion(invalid, invalid, invalid) is null);
            }
            Assert.True(ExecutableIdentityService.ResolveMetadataVersion(null, null, "0.0.0.0") is null);
        });
        suite.Add("Identity metadata: zero revisions normalize but nonzero private revisions remain distinct", () =>
        {
            Assert.Equal("1.5.97", ExecutableIdentityService.ResolveMetadataVersion("1.5.97", null, null));
            Assert.Equal("1.5.97", ExecutableIdentityService.ResolveMetadataVersion(null, "1.5.97.0", null));
            Assert.Equal("1.5.97", ExecutableIdentityService.ResolveMetadataVersion(null, null, "1.5.97.0"));
            Assert.Equal("1.5.97.1", ExecutableIdentityService.ResolveMetadataVersion("1.5.97.1", "1.5.97.0", "1.5.97.0"));
            Assert.Equal("1.6.1170.2", ExecutableIdentityService.ResolveMetadataVersion(null, "1.6.1170.2", "1.6.1170.0"));
            Assert.Equal("1.5.97.3", ExecutableIdentityService.ResolveMetadataVersion(null, null, "1.5.97.3"));
        });
        suite.Add("Identity: exact SHA256 identifies official bytes even when metadata disagrees", async () =>
        {
            using var fixture = new Fixture();
            var bytes = Encoding.UTF8.GetBytes("Official executable fixture");
            File.WriteAllBytes(fixture.GameExe, bytes);
            var reference = Reference(bytes);
            var service = new ExecutableIdentityService([reference], _ => "1.6.1170");
            var identity = await service.IdentifyAsync(fixture.GameExe);
            Assert.Equal(ExecutableIdentityStatus.KnownOfficial, identity.Status);
            Assert.Equal("1.5.97", identity.Version);
            Assert.Equal("1.6.1170", identity.MetadataVersion);
            Assert.Equal(reference.Sha256, identity.Sha256);
            Assert.True(!identity.MetadataMatchesHash);
            Assert.Equal(reference, identity.Reference);
        });
        suite.Add("Identity: version resource matching a reference cannot hide changed EXE bytes", async () =>
        {
            using var fixture = new Fixture();
            File.WriteAllText(fixture.GameExe, "modified executable");
            var service = new ExecutableIdentityService([Reference(Encoding.UTF8.GetBytes("original executable"))], _ => "1.5.97");
            var identity = await service.IdentifyAsync(fixture.GameExe);
            Assert.Equal(ExecutableIdentityStatus.KnownVersionHashMismatch, identity.Status);
            Assert.Equal("1.5.97", identity.Version);
            Assert.True(identity.Reference is null);
        });
        suite.Add("Identity: an unknown build remains unverified without claiming modification", async () =>
        {
            using var fixture = new Fixture();
            File.WriteAllText(fixture.GameExe, "Unrecognized legitimate build");
            var service = new ExecutableIdentityService([Reference(Encoding.UTF8.GetBytes("known build"))], _ => "1.0.0");
            var identity = await service.IdentifyAsync(fixture.GameExe);
            Assert.Equal(ExecutableIdentityStatus.Unknown, identity.Status);
            Assert.Equal("1.0.0", identity.Version);
        });
        suite.Add("Identity: missing or malformed version metadata remains unknown", async () =>
        {
            using var fixture = new Fixture();
            File.WriteAllText(fixture.GameExe, "No version resource");
            foreach (var metadata in new string?[] { null, "1.6", "1.5.97 -run command", "01.5.97" })
            {
                var service = new ExecutableIdentityService([], _ => metadata);
                var identity = await service.IdentifyAsync(fixture.GameExe);
                Assert.Equal(ExecutableIdentityStatus.Unknown, identity.Status);
                Assert.True(identity.Version is null);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.GameExe))), identity.Sha256);
            }
        });
        suite.Add("Identity: hashing supports cancellation without reading metadata", async () =>
        {
            using var fixture = new Fixture();
            File.WriteAllText(fixture.GameExe, "Data");
            var service = new ExecutableIdentityService([], _ => throw new InvalidOperationException("Metadata must not be read"));
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.IdentifyAsync(fixture.GameExe, new CancellationToken(true)));
        });
        suite.Add("Identity: database rejects corruption, injection, duplicate fields and conflicting hashes", () =>
        {
            var reference = Reference(Encoding.UTF8.GetBytes("known"));
            var valid = Database([reference]);
            Assert.Equal(reference, ExecutableIdentityService.ParseVerifiedDatabase(valid).Single());
            foreach (var invalid in new[]
            {
                "null", "{", valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
                valid.Replace("\"appId\":489830", "\"appId\":10"),
                valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
                valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"execute\":\"download anything\""),
                Database([reference, reference]), Database([reference with { Version = null! }]),
                Database([reference with { Version = "../1.5.97" }]),
                Database([reference with { ManifestId = "1\nquit" }]),
                Database([reference with { ManifestId = "01" }]),
                Database([reference with { DepotId = 489838 }]),
                Database([reference with { Sha256 = "A" }]),
                Database([reference with { Sha1 = "F" }]),
                Database([reference with { Length = 0 }]),
                Database([reference with { SourceUrl = "file:///C:/fake-proof" }]),
                Database([reference with { SourceUrl = "https://user:password@example.com/proof" }])
            }) Assert.Throws<InvalidDataException>(() => ExecutableIdentityService.ParseVerifiedDatabase(invalid));
        });
        suite.Add("Identity: learns new bytes only with cache receipt and exact public manifest", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteCache();
            fixture.WriteManifest();
            var service = new ExecutableIdentityService([], _ => "1.5.97");
            var reference = await service.LearnFromSteamCacheAsync(fixture.Cache, "1.5.97", fixture.Depot, [fixture.Steam]);
            Assert.True(reference is not null);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Bytes)), reference!.Sha256);
            Assert.Equal(Convert.ToHexString(SHA1.HashData(fixture.Bytes)), reference.Sha1);
            File.WriteAllBytes(fixture.GameExe, fixture.Bytes);
            Assert.Equal(ExecutableIdentityStatus.KnownOfficial, (await service.IdentifyAsync(fixture.GameExe)).Status);
        });
        suite.Add("Identity: Skyrim resource strings allow manifest-backed learning despite stale fixed fields", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteCache();
            fixture.WriteManifest();
            var service = new ExecutableIdentityService([], _ =>
                ExecutableIdentityService.ResolveMetadataVersion("1.5.97.0", "1.5.97.0", "1.0.0.0"));
            var reference = await service.LearnFromSteamCacheAsync(fixture.Cache, "1.5.97", fixture.Depot, [fixture.Steam]);
            Assert.True(reference is not null);
            Assert.Equal("1.5.97", reference!.Version);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Bytes)), reference.Sha256);
            File.WriteAllBytes(fixture.GameExe, fixture.Bytes);
            var identity = await service.IdentifyAsync(fixture.GameExe);
            Assert.Equal(ExecutableIdentityStatus.KnownOfficial, identity.Status);
            Assert.Equal("1.5.97", identity.MetadataVersion);
            Assert.True(identity.MetadataMatchesHash);
            File.AppendAllText(fixture.GameExe, "changed executable");
            Assert.Equal(ExecutableIdentityStatus.KnownVersionHashMismatch, (await service.IdentifyAsync(fixture.GameExe)).Status);
        });
        suite.Add("Identity: installed bytes and receipt alone do not establish trusted hashes", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteCache();
            File.WriteAllBytes(fixture.GameExe, fixture.Bytes);
            var service = new ExecutableIdentityService([], _ => "1.5.97");
            Assert.True(await service.LearnFromSteamCacheAsync(fixture.Cache, "1.5.97", fixture.Depot, [fixture.Steam]) is null);
            Assert.Equal(ExecutableIdentityStatus.Unknown, (await service.IdentifyAsync(fixture.GameExe)).Status);
        });
        suite.Add("Identity: wrong, encrypted and truncated Steam manifests never establish proof", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteCache();
            var service = new ExecutableIdentityService([], _ => "1.5.97");
            foreach (var manifest in new[] { fixture.MakeManifest(11), fixture.MakeManifest(10, true), fixture.MakeManifest(10)[..^2] })
            {
                fixture.WriteManifest(manifest);
                Assert.True(await service.LearnFromSteamCacheAsync(fixture.Cache, "1.5.97", fixture.Depot, [fixture.Steam]) is null);
            }
        });
        suite.Add("Identity: wrong receipt identity and malformed receipt never establish proof", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteManifest();
            var service = new ExecutableIdentityService([], _ => "1.5.97");
            foreach (var change in new Func<DepotDownloadReceipt, DepotDownloadReceipt>[]
            {
                receipt => receipt with { AppId = 10 }, receipt => receipt with { DepotId = 489831 },
                receipt => receipt with { ManifestId = "11" }, receipt => receipt with { SchemaVersion = 2 },
                receipt => receipt with { Files = [receipt.Files[0], receipt.Files[0]] },
                receipt => receipt with { Files = [receipt.Files[0] with { RelativePath = "../SkyrimSE.exe" }] },
                receipt => receipt with { Files = [receipt.Files[0] with { RelativePath = null! }] }
            })
            {
                fixture.WriteCache(change);
                await Assert.ThrowsAsync<InvalidDataException>(() => service.LearnFromSteamCacheAsync(fixture.Cache, "1.5.97", fixture.Depot, [fixture.Steam]));
            }
            File.WriteAllText(fixture.ReceiptPath, "broken JSON");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LearnFromSteamCacheAsync(fixture.Cache, "1.5.97", fixture.Depot, [fixture.Steam]));
        });
        suite.Add("Identity: mismatched SHA1, SHA256, length or version refuses learned reference", async () =>
        {
            using var fixture = new Fixture();
            var service = new ExecutableIdentityService([], _ => "1.5.97");
            fixture.WriteCache(receipt => receipt with { Files = [receipt.Files[0] with { Sha256 = new string('0', 64) }] });
            fixture.WriteManifest();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LearnFromSteamCacheAsync(fixture.Cache, "1.5.97", fixture.Depot, [fixture.Steam]));
            fixture.WriteCache(receipt => receipt with { Files = [receipt.Files[0] with { Length = 99 }] });
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LearnFromSteamCacheAsync(fixture.Cache, "1.5.97", fixture.Depot, [fixture.Steam]));
            fixture.WriteCache();
            fixture.WriteManifest(fixture.MakeManifest(10, bytes: Encoding.UTF8.GetBytes(new string('Z', fixture.Bytes.Length))));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LearnFromSteamCacheAsync(fixture.Cache, "1.5.97", fixture.Depot, [fixture.Steam]));
            fixture.WriteManifest();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LearnFromSteamCacheAsync(fixture.Cache, "1.6.1170", fixture.Depot, [fixture.Steam]));
        });
    }

    private static VerifiedExecutableHash Reference(byte[] bytes) => new("1.5.97", Convert.ToHexString(SHA256.HashData(bytes)),
        bytes.Length, 489833, "10", Convert.ToHexString(SHA1.HashData(bytes)), "https://steamdb.info/depot/489833/?manifest=10");
    private static string Database(VerifiedExecutableHash[] references) => JsonSerializer.Serialize(new
    { schemaVersion = 1, appId = 489830, executables = references }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "svp-identity-" + Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Root, "cache");
        public string Steam => Path.Combine(Root, "steam");
        public string GameExe => Path.Combine(Root, "SkyrimSE.exe");
        public string DepotRoot => SteamClientDownloadService.GetDepotCacheDirectory(Cache, Depot);
        public string ReceiptPath => Path.Combine(DepotRoot, "complete.json");
        public DepotManifest Depot { get; } = new(489833, "10", false);
        public byte[] Bytes { get; } = Encoding.UTF8.GetBytes("An official executable cache fixture");
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
        public void WriteCache(Func<DepotDownloadReceipt, DepotDownloadReceipt>? change = null)
        {
            Directory.CreateDirectory(Path.Combine(DepotRoot, "content"));
            File.WriteAllBytes(Path.Combine(DepotRoot, "content", "SkyrimSE.exe"), Bytes);
            var receipt = new DepotDownloadReceipt(1, 489830, Depot.DepotId, Depot.ManifestId, DateTimeOffset.UtcNow,
                [new("SkyrimSE.exe", Bytes.Length, Convert.ToHexString(SHA256.HashData(Bytes)))]);
            File.WriteAllText(ReceiptPath, JsonSerializer.Serialize(change?.Invoke(receipt) ?? receipt));
        }
        public void WriteManifest(byte[]? bytes = null)
        {
            Directory.CreateDirectory(Path.Combine(Steam, "depotcache"));
            File.WriteAllBytes(Path.Combine(Steam, "depotcache", "489833_10.manifest"), bytes ?? MakeManifest(10));
        }
        public byte[] MakeManifest(ulong manifest, bool encrypted = false, byte[]? bytes = null)
        {
            bytes ??= Bytes;
            using var mapping = new MemoryStream();
            Blob(mapping, 1, Encoding.UTF8.GetBytes("SkyrimSE.exe")); Number(mapping, 2, (ulong)bytes.Length);
            Blob(mapping, 5, SHA1.HashData(bytes));
            using var payload = new MemoryStream(); Blob(payload, 1, mapping.ToArray());
            using var metadata = new MemoryStream(); Number(metadata, 1, 489833); Number(metadata, 2, manifest);
            Number(metadata, 4, encrypted ? 1UL : 0UL);
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
            writer.Write(0x71F617D0U); writer.Write((uint)payload.Length); writer.Write(payload.ToArray());
            writer.Write(0x1F4812BEU); writer.Write((uint)metadata.Length); writer.Write(metadata.ToArray());
            writer.Write(0x1B81B817U); writer.Write(0U); writer.Write(0x32C415ABU);
            return output.ToArray();
        }
        private static void Blob(Stream stream, uint field, byte[] bytes)
        { Varint(stream, (field << 3) | 2); Varint(stream, (ulong)bytes.Length); stream.Write(bytes); }
        private static void Number(Stream stream, uint field, ulong value) { Varint(stream, field << 3); Varint(stream, value); }
        private static void Varint(Stream stream, ulong value)
        {
            while (value >= 128) { stream.WriteByte((byte)((value & 127) | 128)); value >>= 7; }
            stream.WriteByte((byte)value);
        }
    }
}
