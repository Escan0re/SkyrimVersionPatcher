using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Compatibility;

namespace SkyrimVersionPatcher.Tests;

public static class ContentCatalogTests
{
    private const string ModernCatalog = "{\"CSV2_016105c0-50a6-4beb-851a-6d0f85c6e5f0\":{\"AchievementSafe\":false,\"Files\":[\"creation.bsa\",\"creation.esl\"]},\"ContentCatalog\":{\"Version\":2}}";

    public static void Register(TestSuite suite)
    {
        suite.Add("catalog compatibility: all built-in legacy targets produce read-only snapshots", SupportedTargets);
        suite.Add("catalog compatibility: unknown targets and historical or unknown formats are retained", UnrecognizedFormats);
        suite.Add("catalog compatibility: malformed, duplicate and oversized catalogs have explicit diagnostics", InvalidCatalogs);
        suite.Add("catalog compatibility: recognized entries require safe Files arrays", InvalidEntries);
        suite.Add("catalog compatibility: UTF-8 BOM and unrelated extra metadata are supported", Utf8Bom);
        suite.Add("catalog compatibility: atomic fixed-name rename preserves every byte and unrelated profile files", PreserveRename);
        suite.Add("catalog compatibility: existing ContentCatalog.bak is detectable and never overwritten", RenameCollision);
        suite.Add("catalog compatibility: changed or missing snapshots cannot be applied", ChangedSnapshot);
        suite.Add("catalog compatibility: absent profiles and catalogs need no changes", AbsentCatalog);
        suite.Add("catalog compatibility: root paths and directories masquerading as catalogs are rejected", InvalidPaths);
        suite.Add("catalog compatibility: profile junctions and replaced linked ancestors are rejected", LinkedPaths);
    }

    private static void SupportedTargets()
    {
        using var fixture = new Fixture();
        fixture.Write(ModernCatalog);
        foreach (var version in VersionCatalogService.LoadBuiltIn())
        {
            var plan = ContentCatalogCompatibility.Prepare(fixture.Profile, version.Id)
                ?? throw new InvalidOperationException("Expected a legacy catalog compatibility plan.");
            Assert.Equal(version.Id, plan.TargetVersion);
            Assert.Equal(fixture.Catalog, plan.CatalogPath);
            Assert.Equal((long)Encoding.UTF8.GetByteCount(ModernCatalog), plan.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ModernCatalog))), plan.Sha256);
            Assert.True(!File.Exists(plan.RenamedPath));
            Assert.Equal("ContentCatalog.bak", Path.GetFileName(plan.RenamedPath));
            Assert.Equal("1.7.99+", plan.SourceVersion);
            Assert.Equal(1, Directory.GetFiles(fixture.Profile).Length);
            Assert.Equal(ModernCatalog, File.ReadAllText(fixture.Catalog));
        }
    }

    private static void UnrecognizedFormats()
    {
        using var fixture = new Fixture();
        fixture.Write(ModernCatalog);
        foreach (var version in new[] { "1.6.640", "1.6.1130", "1.7.104", "1.6.999", "1.6.1170.0", "unknown" })
            Assert.True(ContentCatalogCompatibility.Prepare(fixture.Profile, version) is null);
        foreach (var json in new[]
        {
            "{\"CSV2_5679\":{\"Files\":[\"creation.esl\"]}}", "{}", "[]", "null",
            "{\"CSV2_016105c050a64beb851a6d0f85c6e5f0\":{\"Files\":[\"creation.esl\"]}}",
            "{\"CSV2_{016105c0-50a6-4beb-851a-6d0f85c6e5f0}\":{\"Files\":[\"creation.esl\"]}}"
        })
        {
            fixture.Write(json);
            Assert.True(ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170") is null);
            Assert.Equal(json, File.ReadAllText(fixture.Catalog));
        }
        Assert.Equal(1, Directory.GetFiles(fixture.Profile).Length);
    }

    private static void InvalidCatalogs()
    {
        using var fixture = new Fixture();
        foreach (var json in new[] { "{ broken", "", "{\"same\":1,\"same\":2}", ModernCatalog.Replace("\"AchievementSafe\":false", "\"AchievementSafe\":false,\"AchievementSafe\":true", StringComparison.Ordinal) })
        {
            fixture.Write(json);
            var error = Assert.Throws<InvalidDataException>(() => ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170"));
            Assert.True(error.Message.Contains("ContentCatalog.txt", StringComparison.Ordinal));
            Assert.Equal(json, File.ReadAllText(fixture.Catalog));
            Assert.Equal(1, Directory.GetFiles(fixture.Profile).Length);
        }
        using (var stream = new FileStream(fixture.Catalog, FileMode.Create, FileAccess.Write)) stream.SetLength(16 * 1024 * 1024 + 1);
        var oversized = Assert.Throws<InvalidDataException>(() => ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170"));
        Assert.True(oversized.Message.Contains("16 МиБ", StringComparison.Ordinal));
        Assert.Equal(16 * 1024 * 1024 + 1L, new FileInfo(fixture.Catalog).Length);
    }

    private static void InvalidEntries()
    {
        using var fixture = new Fixture();
        foreach (var entry in new[] { "null", "{}", "{\"Files\":null}", "{\"Files\":[42]}", "{\"Files\":[\"../outside.esl\"]}", "{\"Files\":[\"Data\\\\creation.esl\"]}" })
        {
            fixture.Write("{\"CSV2_016105c0-50a6-4beb-851a-6d0f85c6e5f0\":" + entry + "}");
            Assert.Throws<InvalidDataException>(() => ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170"));
            Assert.Equal(1, Directory.GetFiles(fixture.Profile).Length);
        }
    }

    private static void Utf8Bom()
    {
        using var fixture = new Fixture();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(ModernCatalog)).ToArray();
        File.WriteAllBytes(fixture.Catalog, bytes);
        var plan = ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170")!;
        Assert.Equal((long)bytes.Length, plan.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), plan.Sha256);
        var renamed = ContentCatalogCompatibility.Apply(plan);
        Assert.True(File.ReadAllBytes(renamed).SequenceEqual(bytes));
    }

    private static void PreserveRename()
    {
        using var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes(ModernCatalog + "\r\n");
        File.WriteAllBytes(fixture.Catalog, bytes);
        var plugins = Path.Combine(fixture.Profile, "Plugins.txt");
        File.WriteAllText(plugins, "*custom.esm\r\n");
        var plan = ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170")!;
        var originalTime = File.GetLastWriteTimeUtc(fixture.Catalog);
        var renamed = ContentCatalogCompatibility.Apply(plan);
        Assert.Equal(plan.RenamedPath, renamed);
        Assert.Equal(fixture.Profile, Path.GetDirectoryName(renamed));
        Assert.Equal("ContentCatalog.bak", Path.GetFileName(renamed));
        Assert.True(!File.Exists(fixture.Catalog));
        Assert.True(bytes.SequenceEqual(File.ReadAllBytes(renamed)));
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(renamed));
        Assert.Equal("*custom.esm\r\n", File.ReadAllText(plugins));
        Assert.True(ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170") is null);
        Assert.Equal(2, Directory.GetFiles(fixture.Profile).Length);
        // A later regenerated catalog is detected, but the existing .bak is retained.
        fixture.Write(ModernCatalog);
        var regenerated = ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170")!;
        Assert.Equal(renamed, regenerated.RenamedPath);
        Assert.Throws<IOException>(() => ContentCatalogCompatibility.ValidateRenameDestination(regenerated));
        Assert.Throws<IOException>(() => ContentCatalogCompatibility.Apply(regenerated));
        Assert.Equal(ModernCatalog, File.ReadAllText(fixture.Catalog));
        Assert.True(bytes.SequenceEqual(File.ReadAllBytes(renamed)));
    }

    private static void RenameCollision()
    {
        using var fixture = new Fixture();
        fixture.Write(ModernCatalog);
        var plan = ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170")!;
        File.WriteAllText(plan.RenamedPath, "retain existing catalog");
        Assert.True(ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170") is not null);
        var error = Assert.Throws<IOException>(() => ContentCatalogCompatibility.ValidateRenameDestination(plan));
        Assert.True(error.Message.Contains("не будет перезаписан", StringComparison.Ordinal));
        Assert.Throws<IOException>(() => ContentCatalogCompatibility.Apply(plan));
        Assert.Equal(ModernCatalog, File.ReadAllText(fixture.Catalog));
        Assert.Equal("retain existing catalog", File.ReadAllText(plan.RenamedPath));
    }

    private static void ChangedSnapshot()
    {
        using var fixture = new Fixture();
        foreach (var change in new[] { "same-length", "different-length", "missing" })
        {
            fixture.Write(ModernCatalog);
            var plan = ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170")!;
            if (change == "same-length") fixture.Write(ModernCatalog.Replace("false", " true", StringComparison.Ordinal));
            else if (change == "different-length") fixture.Write(ModernCatalog + " ");
            else File.Delete(fixture.Catalog);
            Assert.Throws<IOException>(() => ContentCatalogCompatibility.Apply(plan));
            Assert.True(!File.Exists(plan.RenamedPath));
        }
    }

    private static void AbsentCatalog()
    {
        using var fixture = new Fixture();
        Assert.True(ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170") is null);
        var absent = Path.Combine(fixture.Root, "absent");
        Assert.True(ContentCatalogCompatibility.Prepare(absent, "1.6.1170") is null);
        Assert.True(!Directory.Exists(absent));
    }

    private static void InvalidPaths()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => ContentCatalogCompatibility.Prepare("relative", "1.6.1170"));
        Assert.Throws<InvalidDataException>(() => ContentCatalogCompatibility.Prepare(Path.GetPathRoot(fixture.Profile)!, "1.6.1170"));
        Directory.CreateDirectory(fixture.Catalog);
        Assert.Throws<IOException>(() => ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170"));
        Directory.Delete(fixture.Catalog);
        fixture.Write(ModernCatalog);
        var plan = ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170")!;
        File.Delete(fixture.Catalog);
        Directory.CreateDirectory(fixture.Catalog);
        Assert.Throws<IOException>(() => ContentCatalogCompatibility.Apply(plan));
        Assert.True(!File.Exists(plan.RenamedPath));
    }

    private static void LinkedPaths()
    {
        using var fixture = new Fixture();
        fixture.Write(ModernCatalog);
        var linked = Path.Combine(fixture.Root, "linked-profile");
        CreateDirectoryLink(linked, fixture.Profile);
        try { Assert.Throws<IOException>(() => ContentCatalogCompatibility.Prepare(linked, "1.6.1170")); }
        finally { Directory.Delete(linked); }

        var plan = ContentCatalogCompatibility.Prepare(fixture.Profile, "1.6.1170")!;
        var relocated = Path.Combine(fixture.Root, "original-profile");
        Directory.Move(fixture.Profile, relocated);
        CreateDirectoryLink(fixture.Profile, relocated);
        try { Assert.Throws<IOException>(() => ContentCatalogCompatibility.Apply(plan)); }
        finally { Directory.Delete(fixture.Profile); }
        Assert.Equal(ModernCatalog, File.ReadAllText(Path.Combine(relocated, "ContentCatalog.txt")));
        Assert.Equal(1, Directory.GetFiles(relocated).Length);
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(process.StandardError.ReadToEnd());
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "SkyrimVersionPatcher-content-catalog-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Profile);
        }

        public string Root { get; }
        public string Profile => Path.Combine(Root, "profile");
        public string Catalog => Path.Combine(Profile, "ContentCatalog.txt");
        public void Write(string content) => File.WriteAllText(Catalog, content);

        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SkyrimVersionPatcher-content-catalog-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup.");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
