using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Compatibility;
using SkyrimVersionPatcher.Core.Downloading;
using SkyrimVersionPatcher.Core.Patching;
using SkyrimVersionPatcher.Core.Workflow;

namespace SkyrimVersionPatcher.Tests;

public static class GameLocalizationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Localizations: every official language chooses one pinned overlay after the base depots", () =>
        {
            Assert.Equal(9, GameLocalizationCatalog.All.Count);
            foreach (var version in VersionCatalogService.LoadBuiltIn())
            foreach (var language in GameLocalizationCatalog.All)
            {
                var depots = VersionCatalogService.GetDepots(version with { Depots = version.Depots.Reverse().ToArray() }, language.Id);
                Assert.True(depots.Take(3).Select(item => item.DepotId).SequenceEqual(new uint[] { 489831, 489832, 489833 }));
                Assert.Equal(language.DepotId is null ? 3 : 4, depots.Count);
                if (language.DepotId is { } id) Assert.Equal(id, depots[^1].DepotId);
                foreach (var depot in depots) SteamConsoleCommands.ValidateDepot(depot);
            }
            Assert.Throws<ArgumentException>(() => VersionCatalogService.GetDepots(VersionCatalogService.LoadBuiltIn()[0], "unsupported"));
            var incomplete = VersionCatalogService.LoadBuiltIn()[0] with
            { Depots = VersionCatalogService.LoadBuiltIn()[0].Depots.Where(item => item.DepotId != 489834).ToArray() };
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.GetDepots(incomplete, "french"));
        });

        suite.Add("Localizations: system cultures select available languages and fall back to English", () =>
        {
            foreach (var pair in new[] { ("ru-RU", "russian"), ("en-GB", "english"), ("fr-CA", "french"),
                ("it-IT", "italian"), ("de-AT", "german"), ("es-MX", "spanish"), ("pl-PL", "polish"),
                ("zh-TW", "chinese"), ("zh-CN", "chinese"), ("ja-JP", "japanese"), ("uk-UA", "english") })
                Assert.Equal(pair.Item2, GameLocalizationCatalog.ResolveSystemLanguage(CultureInfo.GetCultureInfo(pair.Item1)));
            Assert.Equal("english", GameLocalizationCatalog.ResolveSystemLanguage(CultureInfo.InvariantCulture));
            Assert.Equal("Skyrim - Voices_en0.bsa", GameLocalizationCatalog.Get("chinese").VoiceArchiveName);
        });

        suite.Add("Localizations: legacy custom catalogs keep overrides and gain additional language depots", () =>
        {
            using var fixture = new Fixture();
            var baseline = VersionCatalogService.LoadBuiltIn()[0];
            var legacy = baseline with { Notes = "Keep custom metadata", Depots = baseline.Depots.Where(item =>
                item.DepotId is 489831 or 489832 or 489833 or 489838).Select(item => item with { ManifestId = "10" }).ToArray() };
            var path = Path.Combine(fixture.Root, "legacy.json");
            VersionCatalogService.Save(path, [legacy]);
            var merged = VersionCatalogService.LoadWithCustom(path).Single(item => item.Id == baseline.Id);
            Assert.Equal(legacy.Notes, merged.Notes);
            Assert.Equal("10", merged.Depots.Single(item => item.DepotId == 489838).ManifestId);
            Assert.Equal(baseline.Depots.Single(item => item.DepotId == 544861).ManifestId,
                VersionCatalogService.GetDepots(merged, "japanese")[^1].ManifestId);
        });

        suite.Add("Localizations: profile changes preserve mod archives, settings and BOM without saved copies", () =>
        {
            using var fixture = new Fixture();
            var path = Path.Combine(fixture.Root, "Skyrim.ini");
            var original = "[General]\r\nsLanguage=RUSSIAN\r\n; комментарий\r\n[Archive]\r\nsResourceArchiveList2=Skyrim - Voices_ru0.bsa, My Mod.bsa\r\n[Display]\r\nfGamma=1.2\r\n";
            File.WriteAllText(path, original, new UnicodeEncoding(false, true));
            var plans = GameLocalizationProfile.Prepare(fixture.Root, "german");
            Assert.Equal(1, plans.Count);
            GameLocalizationProfile.Apply(plans);
            var updated = File.ReadAllText(path);
            Assert.True(updated.Contains("sLanguage=GERMAN") && updated.Contains("Skyrim - Voices_de0.bsa, My Mod.bsa"));
            Assert.True(updated.Contains("; комментарий\r\n") && updated.Contains("fGamma=1.2\r\n"));
            Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }));
            Assert.Equal(0, GameLocalizationProfile.Prepare(fixture.Root, "german").Count);
            GameLocalizationProfile.Apply(GameLocalizationProfile.Prepare(fixture.Root, "chinese"));
            Assert.True(File.ReadAllText(path).Contains("sLanguage=CHINESE") && File.ReadAllText(path).Contains("Skyrim - Voices_en0.bsa, My Mod.bsa"));
            Assert.Equal(1, Directory.GetFiles(fixture.Root).Length);
        });

        suite.Add("Localizations: missing profile files stay absent and changed snapshots block all profile writes", () =>
        {
            using var fixture = new Fixture();
            Assert.Equal(0, GameLocalizationProfile.Prepare(fixture.Root, "french").Count);
            var path = Path.Combine(fixture.Root, "Skyrim.ini");
            var custom = Path.Combine(fixture.Root, "SkyrimCustom.ini");
            File.WriteAllText(path, "[General]\nsLanguage=RUSSIAN\n[Archive]\nsResourceArchiveList2=My Mod.bsa\n");
            File.WriteAllText(custom, "[General]\nsLanguage=RUSSIAN\n");
            var plans = GameLocalizationProfile.Prepare(fixture.Root, "french");
            File.AppendAllText(custom, "; changed");
            Assert.Throws<IOException>(() => GameLocalizationProfile.Apply(plans));
            Assert.True(File.ReadAllText(path).Contains("sLanguage=RUSSIAN"));
            Assert.Equal(0, Directory.GetFiles(fixture.Root, "*.backup").Length);
        });

        suite.Add("Localizations: writable access is required only for INIs that need changes", () =>
        {
            using var fixture = new Fixture();
            var path = Path.Combine(fixture.Root, "Skyrim.ini");
            var original = "[General]\nsLanguage=ENGLISH\n[Archive]\nsResourceArchiveList2=Skyrim - Voices_en0.bsa, My Mod.bsa\n";
            File.WriteAllText(path, original);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                Assert.Equal(0, GameLocalizationProfile.Prepare(fixture.Root, "english").Count);
                Assert.Throws<UnauthorizedAccessException>(() => GameLocalizationProfile.Prepare(fixture.Root, "german"));
                Assert.Equal(original, File.ReadAllText(path));
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
        });

        suite.Add("Localizations: voice archives are changed in active INI values before preserved inline comments", () =>
        {
            using var fixture = new Fixture();
            var path = Path.Combine(fixture.Root, "Skyrim.ini");
            foreach (var commentMarker in new[] { ";", "#" })
            {
                var comment = commentMarker + " Skyrim - Voices_ru0.bsa mentioned only in comment";
                File.WriteAllText(path, "[General]\nsLanguage = RUSSIAN " + commentMarker + " keep language note\n" +
                    "[Archive]\nsResourceArchiveList2=My Mod.bsa, My Skyrim - Voices_ru0.bsa " + comment + "\n");
                GameLocalizationProfile.Apply(GameLocalizationProfile.Prepare(fixture.Root, "french"));
                var updated = File.ReadAllText(path);
                Assert.True(updated.Contains("sLanguage = FRENCH " + commentMarker + " keep language note"));
                Assert.True(updated.Contains("sResourceArchiveList2=My Mod.bsa, My Skyrim - Voices_ru0.bsa, Skyrim - Voices_fr0.bsa " + comment), updated);
                File.WriteAllText(path, "[General]\nsLanguage=RUSSIAN\n[Archive]\n" +
                    "sResourceArchiveList2=Skyrim - Voices_ru0.bsa, My Mod.bsa " + comment + "\n");
                GameLocalizationProfile.Apply(GameLocalizationProfile.Prepare(fixture.Root, "german"));
                Assert.True(File.ReadAllText(path).Contains("sResourceArchiveList2=Skyrim - Voices_de0.bsa, My Mod.bsa " + comment));
            }
        });

        suite.Add("Localizations: verified depot default INI determines the exact profile language token", () =>
        {
            using var fixture = new Fixture();
            var defaults = Path.Combine(fixture.Root, "Skyrim_Default.ini");
            File.WriteAllText(Path.Combine(fixture.Root, "Skyrim.ini"), "[General]\nsLanguage=RUSSIAN\n");
            File.WriteAllText(defaults, "[Other]\nsLanguage=WRONG\n[General]\nsLanguage=TCHINESE ; depot token\n");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(defaults)));
            GameLocalizationProfile.Apply(GameLocalizationProfile.Prepare(fixture.Root, "chinese", defaults, hash));
            Assert.True(File.ReadAllText(Path.Combine(fixture.Root, "Skyrim.ini")).Contains("sLanguage=TCHINESE"));
            File.AppendAllText(defaults, "; changed\n");
            Assert.Throws<IOException>(() => GameLocalizationProfile.Prepare(fixture.Root, "chinese", defaults, hash));
            File.WriteAllText(defaults, "[General]\nsLanguage=../invalid\n");
            Assert.Throws<InvalidDataException>(() => GameLocalizationProfile.Prepare(fixture.Root, "chinese", defaults));
            File.WriteAllText(defaults, "[Other]\nsLanguage=ENGLISH\n");
            Assert.Throws<InvalidDataException>(() => GameLocalizationProfile.Prepare(fixture.Root, "english", defaults));
        });

        suite.Add("Localizations: explicit language overrides the legacy flag, installs its depot and updates profile", InstallSelectedLanguage);

        suite.Add("Localizations: merged local depot resolution accepts exact default INI aliases and whitespace", () =>
        {
            using var fixture = new Fixture();
            File.WriteAllText(Path.Combine(fixture.Root, "SkyrimSE.exe"), "executable");
            var version = VersionCatalogService.LoadBuiltIn()[0];
            foreach (var pair in new[] { ("chinese", "sLanguage=TCHINESE"), ("french", "sLanguage = FRENCH ; selected locale") })
            {
                File.WriteAllText(Path.Combine(fixture.Root, "Skyrim_Default.ini"), "[General]\n" + pair.Item2 + "\n");
                var sources = LocalDepotResolver.ResolveSources(fixture.Root, VersionCatalogService.GetDepots(version, pair.Item1));
                Assert.Equal(fixture.Root, sources.Single());
            }
            Assert.Throws<InvalidDataException>(() => LocalDepotResolver.ResolveSources(fixture.Root,
                VersionCatalogService.GetDepots(version, "english")));
        });
    }

    private static async Task InstallSelectedLanguage()
    {
        foreach (var language in GameLocalizationCatalog.All)
        {
            using var fixture = new Fixture();
            var game = Path.Combine(fixture.Root, "game");
            var source = Path.Combine(fixture.Root, "source");
            var profile = Path.Combine(fixture.Root, "profile");
            var catalogProfile = Path.Combine(fixture.Root, "local-app-data");
            Directory.CreateDirectory(game); Directory.CreateDirectory(Path.Combine(source, "Data")); Directory.CreateDirectory(profile);
            Directory.CreateDirectory(catalogProfile);
            File.WriteAllText(Path.Combine(catalogProfile, "Skyrim.ini"), "unrelated LocalAppData settings");
            File.WriteAllText(Path.Combine(game, "SkyrimSE.exe"), "old executable");
            File.WriteAllText(Path.Combine(source, "SkyrimSE.exe"), "new executable");
            File.WriteAllText(Path.Combine(source, "Data", "Skyrim.esm"), "master");
            File.WriteAllText(Path.Combine(source, "Data", language.VoiceArchiveName), "selected voice");
            File.WriteAllText(Path.Combine(source, "Skyrim_Default.ini"), "[General]\nsLanguage=" + language.IniLanguage);
            File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[General]\nsLanguage=RUSSIAN\n[Archive]\nsResourceArchiveList2=Skyrim - Voices_ru0.bsa\n");
            DownloadRequest? captured = null;
            var engine = new PatchEngine();
            var service = new AutomaticPatchService(engine, (request, _, _) =>
            {
                captured = request;
                var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(path =>
                    new DownloadedFile(Path.GetRelativePath(source, path), new FileInfo(path).Length,
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
                return Task.FromResult(new DownloadResult([source], request.Depots, files));
            }, directory => directory == game ? "1.6.1170" : "1.5.97", () => catalogProfile,
                getLocalizationProfileDirectory: () => profile);
            var result = await service.RunAsync(new(game, Path.Combine(fixture.Root, "storage"),
                VersionCatalogService.LoadBuiltIn().Single(item => item.Id == "1.5.97"),
                IncludeRussianLocalization: language.Id != "russian", GameLanguage: language.Id));
            Assert.True(captured!.Depots.SequenceEqual(VersionCatalogService.GetDepots(
                VersionCatalogService.LoadBuiltIn().Single(item => item.Id == "1.5.97"), language.Id)));
            Assert.True(File.ReadAllText(Path.Combine(profile, "Skyrim.ini")).Contains("sLanguage=" + language.IniLanguage));
            Assert.True(File.ReadAllText(Path.Combine(profile, "Skyrim.ini")).Contains(language.VoiceArchiveName));
            Assert.Equal("unrelated LocalAppData settings", File.ReadAllText(Path.Combine(catalogProfile, "Skyrim.ini")));
            Assert.Equal(1, Directory.GetFiles(profile).Length);
            Assert.Equal("", result.Installation.TransactionDirectory);
            Assert.Equal(0, (await engine.ListDiagnosticsAsync(Path.Combine(fixture.Root, "storage", "Transactions"))).Count);
            Assert.True(!Directory.Exists(Path.Combine(fixture.Root, "storage", "Backups")));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SkyrimLocalizationTests-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
