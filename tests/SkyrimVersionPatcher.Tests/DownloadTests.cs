using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.Tests;

public static class DownloadTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Clean Steam: every depot downloads fresh despite verified cache, local and installed files", async () =>
        {
            using var fixture = new Fixture();
            DepotManifest[] depots = [new(489833, "10", false), new(489838, "20", true)];
            FixtureFile[] gameFiles = [new("SkyrimSE.exe", "game", ChunkSizes: [4]), new("Data\\Nested\\Skyrim.esm", "master", ChunkSizes: [6])];
            FixtureFile[] languageFiles = [new("Skyrim_Default.ini", "russian", ChunkSizes: [7])];
            var installed = Path.Combine(fixture.Root, "game");
            WriteFixtureFiles(installed, gameFiles.Concat(languageFiles).ToArray());
            WriteCache(fixture.Cache, depots[0], gameFiles);
            WriteCache(fixture.Cache, depots[1], languageFiles);
            WriteRawDepot(fixture.Root, depots[0], gameFiles);
            WriteRawDepot(fixture.Root, depots[1], languageFiles);
            var stale = RawDepotFile(fixture.Root, depots[0], "Data\\Nested\\obsolete.dll");
            File.WriteAllText(stale, "stale data");
            File.SetAttributes(stale, File.GetAttributes(stale) | FileAttributes.ReadOnly);
            var sibling = RawDepotFile(fixture.Root, new(489832, "30", false), "Data\\other.bsa");
            Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
            File.WriteAllText(sibling, "untouched depot");
            var bridge = new FakeSteam(fixture.Root)
            {
                BeforeDispatch = (steam, depot) => Assert.True(!Directory.Exists(Path.GetDirectoryName(RawDepotFile(steam.SteamRoot, depot, "SkyrimSE.exe"))))
            };
            bridge.Payloads[489833] = gameFiles;
            bridge.Payloads[489838] = languageFiles;
            var request = new DownloadRequest(fixture.Cache, "test", depots, fixture.Root, installed,
                ReuseInstalledFiles: true, ForceFreshDownload: true);
            var service = Service(bridge);
            var first = await service.DownloadAsync(request);
            var second = await service.DownloadAsync(request);
            Assert.True(bridge.Commands.SequenceEqual(depots.Concat(depots)));
            Assert.Equal(fixture.Root, first.FreshSteamDirectory);
            Assert.True(first.SourceDirectories.SequenceEqual(depots.Select(depot =>
                Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!)));
            Assert.Equal(2, Directory.GetDirectories(fixture.Cache, "fresh-*").Length);
            Assert.Equal(6, Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Count());
            Assert.True(!first.FromCache && !second.FromCache);
            Assert.Equal(17L, first.Statistics!.SteamRequestedBytes);
            Assert.Equal(0L, first.Statistics.ReusedCacheBytes);
            Assert.Equal(0L, first.Statistics.ReusedInstalledBytes);
            Assert.Equal(0L, first.Statistics.ImportedLocalBytes);
            Assert.Equal("master", File.ReadAllText(Path.Combine(first.SourceDirectories[0], "Data", "Nested", "Skyrim.esm")));
            Assert.Equal("game", File.ReadAllText(Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, depots[0]), "content", "SkyrimSE.exe")));
            Assert.Equal("game", File.ReadAllText(RawDepotFile(fixture.Root, depots[0], "SkyrimSE.exe")));
            Assert.Equal("game", File.ReadAllText(Path.Combine(installed, "SkyrimSE.exe")));
            Assert.True(!File.Exists(stale));
            Assert.Equal("untouched depot", File.ReadAllText(sibling));
        });
        suite.Add("Clean Steam: installed game cannot overlap the raw depot being cleared", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            WriteRawDepot(fixture.Root, depot, [new("SkyrimSE.exe", "installed game")]);
            var bridge = new FakeSteam(fixture.Root);
            await Assert.ThrowsAsync<ArgumentException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot],
                InstalledGameDirectory: Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")), ForceFreshDownload: true)));
            Assert.Equal("installed game", File.ReadAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")));
            Assert.Equal(0, bridge.Commands.Count);
        });
        suite.Add("Clean Steam: cannot combine fresh network download with local-only source", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            await Assert.ThrowsAsync<ArgumentException>(() => Service(bridge).DownloadAsync(
                new(fixture.Cache, "test", [new(489833, "10", false)], LocalFilesOnly: true, ForceFreshDownload: true)));
            Assert.True(!Directory.Exists(fixture.Cache));
            Assert.Equal(0, bridge.SessionQueries);
        });
        suite.Add("Clean Steam: all selected raw folders are checked for game overlap before the first deletion", async () =>
        {
            using var fixture = new Fixture();
            DepotManifest[] depots = [new(489833, "10", false), new(489838, "20", true)];
            WriteRawDepot(fixture.Root, depots[0], [new("SkyrimSE.exe", "first depot")]);
            WriteRawDepot(fixture.Root, depots[1], [new("SkyrimSE.exe", "selected game")]);
            var bridge = new FakeSteam(fixture.Root);
            await Assert.ThrowsAsync<ArgumentException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", depots,
                InstalledGameDirectory: Path.GetDirectoryName(RawDepotFile(fixture.Root, depots[1], "SkyrimSE.exe")), ForceFreshDownload: true)));
            Assert.Equal("first depot", File.ReadAllText(RawDepotFile(fixture.Root, depots[0], "SkyrimSE.exe")));
            Assert.Equal("selected game", File.ReadAllText(RawDepotFile(fixture.Root, depots[1], "SkyrimSE.exe")));
            Assert.Equal(0, bridge.Commands.Count);
            Assert.True(!Directory.Exists(fixture.Cache));
        });
        suite.Add("Clean Steam: a junction inside stale raw output stops cleanup before deleting any file", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            WriteRawDepot(fixture.Root, depot, [new("SkyrimSE.exe", "old depot"), new("Data\\old.bsa", "old archive")]);
            var outside = Path.Combine(fixture.Root, "outside");
            WriteFixtureFiles(outside, [new("SkyrimSE.exe", "untouched game")]);
            var link = RawDepotFile(fixture.Root, depot, "Data\\linked");
            CreateDirectoryLink(link, outside);
            try
            {
                var bridge = new FakeSteam(fixture.Root);
                await Assert.ThrowsAsync<IOException>(() => Service(bridge).DownloadAsync(
                    new(fixture.Cache, "test", [depot], ForceFreshDownload: true)));
                Assert.Equal("old depot", File.ReadAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")));
                Assert.Equal("old archive", File.ReadAllText(RawDepotFile(fixture.Root, depot, "Data\\old.bsa")));
                Assert.Equal("untouched game", File.ReadAllText(Path.Combine(outside, "SkyrimSE.exe")));
                Assert.Equal(0, bridge.Commands.Count);
            }
            finally { Directory.Delete(link, recursive: false); }
        });
        suite.Add("Clean Steam: a completion from another Steam root cannot authorize the raw download", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root)
            {
                ReportedCompletionDirectory = Path.Combine(fixture.Root, "foreign", "steamapps", "content", "app_489830", "depot_489833")
            };
            bridge.Payloads[489833] = [new("SkyrimSE.exe", "game")];
            await Assert.ThrowsAsync<InvalidDataException>(() => Service(bridge).DownloadAsync(
                new(fixture.Cache, "test", [new(489833, "10", false)], ForceFreshDownload: true)));
            Assert.Equal(1, bridge.Commands.Count);
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
            Assert.True(!Directory.Exists(Path.Combine(fixture.Root, "foreign")));
        });
        suite.Add("Clean Steam: offline session leaves previous raw downloads intact", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            WriteRawDepot(fixture.Root, depot, [new("SkyrimSE.exe", "old depot")]);
            var bridge = new FakeSteam(fixture.Root) { Online = false };
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(bridge).DownloadAsync(
                new(fixture.Cache, "test", [depot], ForceFreshDownload: true)));
            Assert.Equal("old depot", File.ReadAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")));
            Assert.Equal(0, bridge.Commands.Count);
        });
        suite.Add("Clean Steam: only authenticated manifest bytes can truncate a newly downloaded BSA tail", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            FixtureFile[] verified = [new("SkyrimSE.exe", "game"), new("Data\\Skyrim.bsa", "archive")];
            WritePublicManifest(fixture.Root, depot, verified);
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[489833] = [verified[0], new("Data\\Skyrim.bsa", "archivetrailing Steam bytes")];
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot], ForceFreshDownload: true));
            Assert.Equal(fixture.Root, result.FreshSteamDirectory);
            Assert.Equal("archive", File.ReadAllText(RawDepotFile(fixture.Root, depot, "Data\\Skyrim.bsa")));
            Assert.Equal(7L, result.Files.Single(file => file.RelativePath == "Data\\Skyrim.bsa").Length);
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "Skyrim.bsa", SearchOption.AllDirectories).Any());
        });
        suite.Add("Local only: missing or absent selected folder never queries Steam", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var depot = new DepotManifest(489833, "10", false);
            await Assert.ThrowsAsync<ArgumentException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot],
                LocalFilesOnly: true)));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot],
                Path.Combine(fixture.Root, "absent"), LocalFilesOnly: true)));
            Assert.Equal(0, bridge.SessionQueries);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.Equal(0, bridge.ReadProcessIds.Count);
        });
        suite.Add("Local only: selected complete depots and public manifests import without Steam", async () =>
        {
            using var fixture = new Fixture();
            var selected = Path.Combine(fixture.Root, "my-depots");
            DepotManifest[] depots = [new(489833, "10", false), new(489838, "20", true)];
            WriteRawDepot(selected, depots[0], [new("SkyrimSE.exe", "game"), new("Skyrim_Default.ini", "english")]);
            WriteRawDepot(selected, depots[1], [new("Skyrim_Default.ini", "russian")]);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var service = new SteamClientDownloadService(bridge, () => throw new InvalidOperationException("Selected manifests need no Steam discovery"));
            var result = await service.DownloadAsync(new(fixture.Cache, "test", depots,
                Path.Combine(selected, "steamapps", "content", "app_489830", "depot_489833"), LocalFilesOnly: true));
            Assert.True(result.FromCache);
            Assert.Equal(2, result.SourceDirectories.Count);
            Assert.Equal(Receipt("Skyrim_Default.ini", "russian").Sha256,
                result.Files.Single(file => file.RelativePath == "Skyrim_Default.ini").Sha256);
            Assert.Equal(18L, result.Statistics!.ImportedLocalBytes);
            Assert.Equal(0L, result.Statistics.SteamRequestedBytes);
            Assert.Equal(0L, result.Statistics.ReusedInstalledBytes);
            Assert.Equal("game", File.ReadAllText(RawDepotFile(selected, depots[0], "SkyrimSE.exe")));
            Assert.Equal(0, bridge.PrepareCalls);
            Assert.Equal(0, bridge.SessionQueries);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.Equal(0, bridge.ReadProcessIds.Count);
        });
        suite.Add("Local only: incomplete or changed selected depots never substitute available Steam files", async () =>
        {
            foreach (var invalid in new[] { "missing-depot", "missing-file", "changed-file" })
            {
                using var fixture = new Fixture();
                var selected = Path.Combine(fixture.Root, "my-depots");
                var game = new DepotManifest(489833, "10", false);
                var russian = new DepotManifest(489838, "20", true);
                FixtureFile[] gameFiles = [new("SkyrimSE.exe", "game"), new("Data\\Skyrim.esm", "master")];
                WriteRawDepot(fixture.Root, game, gameFiles);
                WriteRawDepot(fixture.Root, russian, [new("Skyrim_Default.ini", "russian")]);
                WriteRawDepot(selected, game, gameFiles);
                WriteRawDepot(selected, russian, [new("Skyrim_Default.ini", "russian")]);
                if (invalid == "missing-depot") Directory.Delete(Path.GetDirectoryName(RawDepotFile(selected, russian, "Skyrim_Default.ini"))!, true);
                else if (invalid == "missing-file") File.Delete(RawDepotFile(selected, game, "Data\\Skyrim.esm"));
                else File.WriteAllText(RawDepotFile(selected, game, "SkyrimSE.exe"), "fake");
                var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(bridge).DownloadAsync(
                    new(fixture.Cache, "test", [game, russian], selected, LocalFilesOnly: true)));
                Assert.True(error.Message.Contains(invalid == "missing-depot" ? "Не найдены" : "не прошли проверку", StringComparison.Ordinal));
                Assert.Equal(0, bridge.SessionQueries);
                Assert.Equal(0, bridge.Commands.Count);
                Assert.Equal(0, bridge.ReadProcessIds.Count);
                Assert.True(!Directory.Exists(fixture.Cache) || !Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
            }
        });
        suite.Add("Local only: absent or invalid public manifest fails even when destination cache is complete", async () =>
        {
            foreach (var invalid in new[] { "absent", "wrong-identity", "encrypted", "truncated" })
            {
                using var fixture = new Fixture();
                var selected = Path.Combine(fixture.Root, "my-depots");
                var depot = new DepotManifest(489833, "10", false);
                FixtureFile[] files = [new("SkyrimSE.exe", "game")];
                WriteRawDepot(selected, depot, files);
                WriteCache(fixture.Cache, depot, files);
                var manifest = Path.Combine(selected, "depotcache", "489833_10.manifest");
                if (invalid == "absent") File.Delete(manifest);
                else File.WriteAllBytes(manifest, invalid switch
                {
                    "wrong-identity" => MakeManifest(489833, 11, files),
                    "encrypted" => MakeManifest(489833, 10, files, encrypted: true),
                    _ => MakeManifest(489833, 10, files)[..^3]
                });
                var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
                var service = new SteamClientDownloadService(bridge, () => []);
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(
                    fixture.Cache, "test", [depot], selected, LocalFilesOnly: true)));
                Assert.True(error.Message.Contains("489833_10.manifest", StringComparison.Ordinal));
                Assert.Equal(0, bridge.SessionQueries);
                Assert.Equal(0, bridge.Commands.Count);
                Assert.Equal(0, bridge.ReadProcessIds.Count);
            }
        });
        suite.Add("Local only: exported verified cache imports without manifest lookup or client access", async () =>
        {
            using var fixture = new Fixture();
            var selected = Path.Combine(fixture.Root, "exported-cache");
            var depot = new DepotManifest(489833, "10", false);
            WriteCache(selected, depot, [new("SkyrimSE.exe", "game")]);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var service = new SteamClientDownloadService(bridge, () => throw new InvalidOperationException("No Steam discovery needed"));
            var result = await service.DownloadAsync(new(fixture.Cache, "test", [depot], selected, LocalFilesOnly: true));
            Assert.Equal("game", File.ReadAllText(Path.Combine(result.SourceDirectories.Single(), "SkyrimSE.exe")));
            Assert.Equal(4L, result.Statistics!.ImportedLocalBytes);
            Assert.Equal(0, bridge.SessionQueries);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.Equal(0, bridge.ReadProcessIds.Count);
            var inPlace = await service.DownloadAsync(new(selected, "test", [depot], selected, LocalFilesOnly: true));
            Assert.Equal("game", File.ReadAllText(Path.Combine(inPlace.SourceDirectories.Single(), "SkyrimSE.exe")));
        });
        suite.Add("Local only: damaged exported cache is rejected without touching selected files", async () =>
        {
            using var fixture = new Fixture();
            var selected = Path.Combine(fixture.Root, "exported-cache");
            var depot = new DepotManifest(489833, "10", false);
            WriteCache(selected, depot, [new("SkyrimSE.exe", "game")]);
            var sourceFile = Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(selected, depot), "content", "SkyrimSE.exe");
            File.WriteAllText(sourceFile, "fake");
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(bridge).DownloadAsync(
                new(fixture.Cache, "test", [depot], selected, LocalFilesOnly: true)));
            Assert.True(error.Message.Contains("повреждены или неполны", StringComparison.Ordinal));
            Assert.Equal("fake", File.ReadAllText(sourceFile));
            Assert.Equal(0, bridge.SessionQueries);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.Equal(0, bridge.ReadProcessIds.Count);
        });
        suite.Add("Local only: selected source may not overlap the game destination", async () =>
        {
            using var fixture = new Fixture();
            var selected = Path.Combine(fixture.Root, "my-depots");
            var depot = new DepotManifest(489833, "10", false);
            WriteRawDepot(selected, depot, [new("SkyrimSE.exe", "game")]);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var error = await Assert.ThrowsAsync<ArgumentException>(() => Service(bridge).DownloadAsync(
                new(fixture.Cache, "test", [depot], selected, InstalledGameDirectory: selected, LocalFilesOnly: true)));
            Assert.True(error.Message.Contains("независимо", StringComparison.Ordinal));
            Assert.Equal(0, bridge.SessionQueries);
            Assert.Equal(0, bridge.Commands.Count);
        });
        suite.Add("Steam: commands preserve exact unsigned manifest and RU selection", () =>
        {
            var commands = SteamConsoleCommands.ForVersion([new(489833, "18446744073709551615", false), new(489838, "20", true)]);
            Assert.Equal("download_depot 489830 489833 18446744073709551615", commands[0]);
            Assert.Equal("download_depot 489830 489838 20", commands[1]);
            Assert.Throws<ArgumentException>(() => SteamConsoleCommands.ForDepot(new(489833, "1;quit", false)));
            Assert.Throws<ArgumentException>(() => SteamConsoleCommands.ForDepot(new(7, "1", false)));
        });
        suite.Add("Steam: completion requires exact app, depot and manifest", () =>
        {
            var observation = new SteamConsoleObservation(new(489833, "10", false));
            foreach (var line in new[] {
                "Depot download complete : \"C:\\Steam\\steamapps\\content\\app_489830\\depot_489833\" (1 files, manifest 11)",
                "Depot download complete : \"C:\\Steam\\steamapps\\content\\app_1\\depot_489833\" (1 files, manifest 10)",
                "Depot download complete : \"C:\\Steam\\steamapps\\content\\app_489830\\depot_489838\" (1 files, manifest 10)" }) observation.Accept(line);
            Assert.True(observation.Completion is null);
            observation.Accept("Depot download complete : \"C:\\Steam\\steamapps\\content\\app_489830\\depot_489833\" (1 files, manifest 10)");
            Assert.Equal("10", observation.Completion!.ManifestId);
            Assert.Equal(1, observation.Completion.FileCount);
        });
        suite.Add("Steam: failures are scoped to acknowledged or exact requested downloads", () =>
        {
            var observation = new SteamConsoleObservation(new(489833, "10", false));
            observation.Accept("Depot download failed : other request");
            observation.Accept("Downloading depot 489833 (45 MB)");
            Assert.True(observation.Acknowledged);
            Assert.Throws<IOException>(() => observation.Accept("Depot download failed : Access Denied"));
        });
        suite.Add("Steam: terminal failures ignore other requests and manifest prefixes", () =>
        {
            foreach (var unrelated in new[] {
                "Downloading depot 489838 (45 MB)",
                "] download_depot 489830 489833 11",
                "] download_depot 489830 489838 10" })
            {
                var observation = new SteamConsoleObservation(new(489833, "10", false));
                observation.Accept("Downloading depot 489833 (45 MB)");
                observation.Accept(unrelated);
                observation.Accept("Depot download failed : Access Denied");
                Assert.True(observation.Acknowledged && !observation.Failed);
            }
            var exact = new SteamConsoleObservation(new(489833, "10", false));
            exact.Accept("App: 489830, Depot: 489833, Manifest: 100 Failed");
            exact.Accept("Downloading depot 489833 (45 MB)");
            exact.Accept("Depot download failed : Failed updating depot 489838 while starting download (No connection to content servers)");
            Assert.True(!exact.Failed);
            Assert.Throws<IOException>(() => exact.Accept("App: 489830, Depot: 489833, Manifest: 10 Failed"));
            Assert.True(exact.Failed);
        });
        suite.Add("Steam: public connection log exposes only latest network state", () =>
        {
            using var fixture = new Fixture();
            var log = Path.Combine(fixture.Root, "connection.txt");
            File.WriteAllText(log, "[Connection] Logged On\n[Connection] Disconnected\n");
            Assert.Equal(false, SteamClientLocator.ReadOnlineState(log));
            File.AppendAllText(log, "[Connection] Logged On\n");
            Assert.Equal(true, SteamClientLocator.ReadOnlineState(log));
            Assert.Equal<bool?>(null, SteamClientLocator.ReadOnlineState(log + ".missing"));
        });
        suite.Add("Steam: per-depot cache reuses shared manifests across version labels and RU without login", async () =>
        {
            using var fixture = new Fixture();
            DepotManifest[] depots = [new(489833, "10", false), new(489838, "20", true)];
            WriteCache(fixture.Cache, depots[0], [new("SkyrimSE.exe", "game"), new("Skyrim_Default.ini", "english")]);
            WriteCache(fixture.Cache, depots[1], [new("Skyrim_Default.ini", "russian")]);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var russian = await Service(bridge).DownloadAsync(new(fixture.Cache, "first", depots));
            Assert.True(russian.FromCache); Assert.Equal(2, russian.SourceDirectories.Count);
            Assert.Equal(Receipt("Skyrim_Default.ini", "russian").Sha256, russian.Files.Single(f => f.RelativePath == "Skyrim_Default.ini").Sha256);
            var english = await Service(bridge).DownloadAsync(new(fixture.Cache, "second", [depots[0]]));
            Assert.True(english.FromCache); Assert.Equal(1, english.SourceDirectories.Count);
            Assert.Equal(Receipt("Skyrim_Default.ini", "english").Sha256, english.Files.Single(f => f.RelativePath == "Skyrim_Default.ini").Sha256);
            Assert.Equal(0, bridge.PrepareCalls);
            Assert.Equal(0, bridge.Commands.Count);
        });
        suite.Add("Steam: every missing depot is prepared when switching target versions in the same client", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root)
            {
                DuringPreparation = steam => Assert.Equal(steam.PrepareCalls - 1, steam.Commands.Count),
                BeforeFirstConsoleRead = steam => Assert.Equal(1, steam.PrepareCalls)
            };
            var service = Service(bridge);
            DepotManifest[] first = [new(489833, "10", false), new(489838, "20", true)];
            DepotManifest[] second = [new(489833, "11", false), new(489838, "21", true)];
            bridge.Payloads[489833] = [new("SkyrimSE.exe", "first game")];
            bridge.Payloads[489838] = [new("Skyrim_Default.ini", "first russian")];
            var firstResult = await service.DownloadAsync(new(fixture.Cache, "first", first));
            bridge.Payloads[489833] = [new("SkyrimSE.exe", "second game")];
            bridge.Payloads[489838] = [new("Skyrim_Default.ini", "second russian")];
            var secondResult = await service.DownloadAsync(new(fixture.Cache, "second", second));
            Assert.True(!firstResult.FromCache && !secondResult.FromCache);
            Assert.Equal(4, bridge.PrepareCalls);
            Assert.True(bridge.PreparedProcessIds.SequenceEqual([42, 42, 42, 42]));
            Assert.True(bridge.Commands.SequenceEqual(first.Concat(second)));
            Assert.Equal("first game", File.ReadAllText(Path.Combine(firstResult.SourceDirectories[0], "SkyrimSE.exe")));
            Assert.Equal("second game", File.ReadAllText(Path.Combine(secondResult.SourceDirectories[0], "SkyrimSE.exe")));
        });
        suite.Add("Steam: explicit preparation baselines replayed console and log success before dispatch", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            WriteFixtureFiles(Path.Combine(fixture.Root, "steamapps", "content", "app_489830", "depot_489833"),
                [new("SkyrimSE.exe", "old download")]);
            var bridge = new FakeSteam(fixture.Root)
            {
                PublishCompletion = false,
                DuringPreparation = steam =>
                {
                    var oldCompletion = steam.CompletionLine(depot, 1) + "\n";
                    steam.Console += oldCompletion;
                    var logs = Path.Combine(fixture.Root, "logs");
                    Directory.CreateDirectory(logs);
                    foreach (var name in new[] { "console_log.txt", "content_log.txt" })
                        File.AppendAllText(Path.Combine(logs, name), oldCompletion);
                }
            };
            var service = new SteamClientDownloadService(bridge, () => [fixture.Root])
            {
                PollInterval = TimeSpan.FromMilliseconds(5),
                CommandAcknowledgementTimeout = TimeSpan.FromMilliseconds(35),
                DepotTimeout = TimeSpan.FromSeconds(1)
            };
            var error = await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => service.DownloadAsync(new(fixture.Cache, "test", [depot])));
            Assert.True(error.Message.Contains("не подтвердил запуск", StringComparison.Ordinal));
            Assert.Equal(1, bridge.PrepareCalls);
            Assert.Equal(1, bridge.Commands.Count);
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
        });
        suite.Add("Steam: session changes during explicit preparation prevent baseline and dispatch", async () =>
        {
            Action<FakeSteam>[] changes = [steam => steam.ProcessId = 43, steam => steam.ActiveUser = 2];
            foreach (var change in changes)
            {
                using var fixture = new Fixture();
                var bridge = new FakeSteam(fixture.Root) { DuringPreparation = change };
                await Assert.ThrowsAsync<InvalidOperationException>(() => Service(bridge).DownloadAsync(
                    new(fixture.Cache, "test", [new(489833, "10", false)])));
                Assert.Equal(1, bridge.PrepareCalls);
                Assert.True(bridge.PreparedProcessIds.SequenceEqual([42]));
                Assert.Equal(0, bridge.ReadProcessIds.Count);
                Assert.Equal(0, bridge.Commands.Count);
                Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
            }
        });
        suite.Add("Steam: cancellation during explicit preparation stops before baseline and dispatch", async () =>
        {
            using var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            var bridge = new FakeSteam(fixture.Root) { DuringPreparation = _ => cancellation.Cancel() };
            await Assert.ThrowsAsync<OperationCanceledException>(() => Service(bridge).DownloadAsync(
                new(fixture.Cache, "test", [new(489833, "10", false)]), cancellationToken: cancellation.Token));
            Assert.Equal(1, bridge.PrepareCalls);
            Assert.Equal(0, bridge.ReadProcessIds.Count);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
        });
        suite.Add("Steam: observed matching completion creates verified independent receipt", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[489833] = [new("SkyrimSE.exe", "game")];
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [new(489833, "10", false)]));
            Assert.True(!result.FromCache); Assert.Equal(1, bridge.Commands.Count);
            Assert.Equal("game", await File.ReadAllTextAsync(Path.Combine(result.SourceDirectories.Single(), "SkyrimSE.exe")));
            bridge.SessionForbidden = true;
            Assert.True((await Service(bridge).DownloadAsync(new(fixture.Cache, "other-label", [new(489833, "10", false)]))).FromCache);
        });
        suite.Add("Steam: changed PID during console preparation prevents dispatch and receipt", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root) { BeforeFirstConsoleRead = steam => steam.ProcessId = 43 };
            bridge.Payloads[489833] = [new("SkyrimSE.exe", "game")];
            bridge.Payloads[489838] = [new("Skyrim_Default.ini", "russian")];
            DepotManifest[] depots = [new(489833, "10", false), new(489838, "20", true)];
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", depots)));
            Assert.Equal(0, bridge.Commands.Count);
            Assert.True(bridge.ReadProcessIds.SequenceEqual([42]));
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
        });
        suite.Add("Steam: preparation cannot switch user, installation or executable before dispatch", async () =>
        {
            Action<FakeSteam>[] changes = [steam => steam.ActiveUser = 2,
                steam => steam.SteamDirectoryOverride = Path.Combine(Path.GetTempPath(), "other-steam"),
                steam => steam.ExecutablePathOverride = Path.Combine(Path.GetTempPath(), "other.exe")];
            foreach (var change in changes)
            {
                using var fixture = new Fixture();
                var bridge = new FakeSteam(fixture.Root) { BeforeFirstConsoleRead = change };
                await Assert.ThrowsAsync<InvalidOperationException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [new(489833, "10", false)])));
                Assert.Equal(0, bridge.Commands.Count);
                Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
            }
        });
        suite.Add("Steam: process change between missing depots cannot authorize another command", async () =>
        {
            using var fixture = new Fixture();
            var game = new DepotManifest(489833, "10", false);
            var russian = new DepotManifest(489838, "20", true);
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[489833] = [new("SkyrimSE.exe", "game")];
            bridge.Payloads[489838] = [new("Skyrim_Default.ini", "russian")];
            var progress = new Recorder(value => { if (value.Message == "Депо 489833 готово.") bridge.ProcessId = 43; });
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [game, russian]), progress));
            Assert.Equal(1, bridge.Commands.Count);
            Assert.Equal(489833U, bridge.Commands.Single().DepotId);
            Assert.True(File.Exists(Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, game), "complete.json")));
            Assert.True(!File.Exists(Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, russian), "complete.json")));
        });
        suite.Add("Steam: restart after command dispatch cannot authorize completion", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root) { AfterDispatch = steam => steam.ProcessId = 43 };
            bridge.Payloads[489833] = [new("SkyrimSE.exe", "game")];
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [new(489833, "10", false)])));
            Assert.Equal(1, bridge.Commands.Count);
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
        });
        suite.Add("Steam: console preparation startup logs cannot replay historic completion", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var bridge = new FakeSteam(fixture.Root)
            {
                PublishCompletion = false,
                BeforeFirstConsoleRead = steam =>
                {
                    Directory.CreateDirectory(Path.Combine(fixture.Root, "logs"));
                    File.AppendAllText(Path.Combine(fixture.Root, "logs", "console_log.txt"), steam.CompletionLine(depot, 1) + "\n");
                }
            };
            await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot])));
            Assert.Equal(1, bridge.Commands.Count);
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
        });
        suite.Add("Steam: changed PID after console snapshot cannot authorize historical completion", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            WriteFixtureFiles(Path.Combine(fixture.Root, "steamapps", "content", "app_489830", "depot_489833"),
                [new("SkyrimSE.exe", "old download")]);
            var bridge = new FakeSteam(fixture.Root)
            {
                PublishCompletion = false,
                AfterFirstConsoleRead = steam =>
                {
                    steam.ProcessId = 43;
                    steam.Console = steam.CompletionLine(depot, 1) + "\n";
                }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot])));
            Assert.True(bridge.ReadProcessIds.SequenceEqual([42]));
            Assert.Equal(0, bridge.Commands.Count);
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
        });
        suite.Add("Steam: existing raw depot imports from exact public manifest without a signed-in client", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            WriteRawDepot(fixture.Root, depot, [new("SkyrimSE.exe", "game")]);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]));
            Assert.True(result.FromCache);
            Assert.Equal(0, bridge.PrepareCalls);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.Equal("game", File.ReadAllText(Path.Combine(result.SourceDirectories.Single(), "SkyrimSE.exe")));
            Assert.True(File.Exists(Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, depot), "complete.json")));
            Assert.Equal(4L, result.Statistics!.ImportedLocalBytes);
            Assert.Equal(0L, result.Statistics.SteamRequestedBytes);
        });

        suite.Add("Steam: exact raw manifest excludes leftover AE files from offline import and cache", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var archive = Path.Combine("Data", "archive.bsa");
            var leftover = Path.Combine("Data", "ccBGSSSE001-Fish.bsa");
            FixtureFile[] target = [new("SkyrimSE.exe", "game"), new(archive, "target")];
            WriteRawDepot(fixture.Root, depot, target);
            var raw = Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!;
            WriteFixtureFiles(raw, [new(leftover, "AE file"), new(Path.Combine(".DepotDownloader", "state"), "metadata")]);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var progress = new Recorder();
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]), progress);
            var cached = result.SourceDirectories.Single();
            Assert.True(result.FromCache);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.Equal("target", File.ReadAllText(Path.Combine(cached, archive)));
            Assert.True(result.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(target.Select(file => file.Path)));
            Assert.True(Directory.EnumerateFiles(cached, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(cached, path)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(target.Select(file => file.Path)));
            Assert.Equal("AE file", File.ReadAllText(Path.Combine(raw, leftover)));
            Assert.Equal("metadata", File.ReadAllText(Path.Combine(raw, ".DepotDownloader", "state")));
            Assert.Equal(10L, result.Statistics!.ImportedLocalBytes);
            Assert.True(progress.Messages.Any(message => message.Contains("1", StringComparison.Ordinal) &&
                message.Contains("вне целевого манифеста", StringComparison.OrdinalIgnoreCase)));
            var receipt = JsonSerializer.Deserialize<DepotDownloadReceipt>(File.ReadAllText(
                Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, depot), "complete.json")))!;
            Assert.True(receipt.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(target.Select(file => file.Path)));
            Assert.True((await Service(bridge).DownloadAsync(new(fixture.Cache, "again", [depot]))).FromCache);
        });

        suite.Add("Steam: fresh matching manifest completion ignores preserved leftover files in its raw folder", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var archive = Path.Combine("Data", "archive.bsa");
            var leftover = Path.Combine("Data", "_ResourcePack.esl");
            FixtureFile[] target = [new("SkyrimSE.exe", "game"), new(archive, "target")];
            WritePublicManifest(fixture.Root, depot, target);
            var raw = Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!;
            var bridge = new FakeSteam(fixture.Root)
            {
                AfterDispatch = _ => WriteFixtureFiles(raw, [new(leftover, "AE file")])
            };
            bridge.Payloads[depot.DepotId] = target;
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]));
            Assert.True(!result.FromCache);
            Assert.Equal(1, bridge.Commands.Count);
            Assert.Equal(2, result.Files.Count);
            Assert.Equal("target", File.ReadAllText(Path.Combine(result.SourceDirectories.Single(), archive)));
            Assert.True(!File.Exists(Path.Combine(result.SourceDirectories.Single(), leftover)));
            Assert.Equal("AE file", File.ReadAllText(Path.Combine(raw, leftover)));
            Assert.Equal(10L, result.Statistics!.TargetBytes);
            Assert.True(File.Exists(Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, depot), "complete.json")));
        });

        suite.Add("Steam: leftovers cannot mask a missing manifest target or wrong SHA1", async () =>
        {
            foreach (var missing in new[] { true, false })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489833, "10", false);
                var archive = Path.Combine("Data", "archive.bsa");
                var leftover = Path.Combine("Data", "other-version.bsa");
                FixtureFile[] target = [new("SkyrimSE.exe", "game"), new(archive, "target")];
                WriteRawDepot(fixture.Root, depot, target);
                var raw = Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!;
                WriteFixtureFiles(raw, [new(leftover, "target")]);
                void DamageTarget()
                {
                    if (missing) File.Delete(Path.Combine(raw, archive));
                    else File.WriteAllText(Path.Combine(raw, archive), "wrong!", new UTF8Encoding(false));
                }
                DamageTarget();
                var game = Path.Combine(fixture.Root, "installed");
                WriteFixtureFiles(game, [new("SkyrimSE.exe", "installed game")]);
                var bridge = new FakeSteam(fixture.Root) { AfterDispatch = _ => DamageTarget() };
                bridge.Payloads[depot.DepotId] = target;
                var progress = new Recorder();
                await Assert.ThrowsAsync<InvalidDataException>(() => Service(bridge).DownloadAsync(
                    new(fixture.Cache, "test", [depot], InstalledGameDirectory: game, ReuseInstalledFiles: false), progress));
                Assert.Equal(1, bridge.Commands.Count);
                Assert.True(progress.Messages.Any(message => message.Contains("не прошли проверку", StringComparison.Ordinal)));
                Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
                Assert.Equal("target", File.ReadAllText(Path.Combine(raw, leftover)));
                Assert.Equal("installed game", File.ReadAllText(Path.Combine(game, "SkyrimSE.exe")));
            }
        });

        suite.Add("Steam: raw leftovers without a public manifest still fail a completion file-count mismatch", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var leftover = Path.Combine("Data", "old-version.bsa");
            var raw = Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!;
            var bridge = new FakeSteam(fixture.Root)
            {
                AfterDispatch = _ => WriteFixtureFiles(raw, [new(leftover, "leftover")])
            };
            bridge.Payloads[depot.DepotId] = [new("SkyrimSE.exe", "game")];
            await Assert.ThrowsAsync<InvalidDataException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot])));
            Assert.Equal(1, bridge.Commands.Count);
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
            Assert.Equal("leftover", File.ReadAllText(Path.Combine(raw, leftover)));
        });

        suite.Add("Steam: ignoring raw extras never follows an extra directory junction", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            FixtureFile[] target = [new("SkyrimSE.exe", "game")];
            WriteRawDepot(fixture.Root, depot, target);
            var raw = Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!;
            var outside = Path.Combine(fixture.Root, "outside");
            WriteFixtureFiles(outside, [new("untouched.txt", "outside contents")]);
            var linked = Path.Combine(raw, "extra-link");
            CreateDirectoryLink(linked, outside);
            try
            {
                var bridge = new FakeSteam(fixture.Root);
                bridge.Payloads[depot.DepotId] = target;
                await Assert.ThrowsAsync<IOException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot])));
                Assert.Equal(1, bridge.Commands.Count);
                Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
                Assert.Equal("outside contents", File.ReadAllText(Path.Combine(outside, "untouched.txt")));
                Assert.True(Directory.Exists(linked));
            }
            finally { Directory.Delete(linked); }
        });

        suite.Add("Steam: unrecorded app-cache extras remain invalid and are quarantined before raw import", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            FixtureFile[] target = [new("SkyrimSE.exe", "game")];
            WriteCache(fixture.Cache, depot, target);
            var cacheRoot = SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, depot);
            var extra = Path.Combine("Data", "unrecorded.bsa");
            WriteFixtureFiles(Path.Combine(cacheRoot, "content"), [new(extra, "cache extra")]);
            WriteRawDepot(fixture.Root, depot, target);
            var raw = Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!;
            WriteFixtureFiles(raw, [new(extra, "raw extra")]);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var progress = new Recorder();
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]), progress);
            Assert.True(result.FromCache);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.Equal(0L, result.Statistics!.ReusedCacheBytes);
            Assert.Equal(4L, result.Statistics.ImportedLocalBytes);
            Assert.True(!File.Exists(Path.Combine(result.SourceDirectories.Single(), extra)));
            Assert.Equal("raw extra", File.ReadAllText(Path.Combine(raw, extra)));
            var quarantined = Directory.GetDirectories(Path.Combine(fixture.Cache, "corrupt")).Single();
            Assert.Equal("cache extra", File.ReadAllText(Path.Combine(quarantined, "content", extra)));
            Assert.True(progress.Messages.Any(message => message.Contains("Кэш депо", StringComparison.Ordinal) &&
                message.Contains("лишние", StringComparison.Ordinal)));
            var again = await Service(bridge).DownloadAsync(new(fixture.Cache, "again", [depot]));
            Assert.Equal(4L, again.Statistics!.ReusedCacheBytes);
            Assert.Equal(0L, again.Statistics.ImportedLocalBytes);
        });

        suite.Add("Steam: manifest-verified prefix imports without stale trailing bytes or changing raw source", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var archive = Path.Combine("Data", "archive.bsa");
            FixtureFile[] target = [new("SkyrimSE.exe", "game"), new(archive, "target")];
            WriteRawDepot(fixture.Root, depot, target);
            var source = RawDepotFile(fixture.Root, depot, archive);
            File.AppendAllText(source, "stale tail", new UTF8Encoding(false));
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]));
            var cached = Path.Combine(result.SourceDirectories.Single(), archive);
            var recorded = result.Files.Single(file => file.RelativePath == archive);
            Assert.True(result.FromCache);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.Equal("target", File.ReadAllText(cached));
            Assert.Equal(6L, new FileInfo(cached).Length);
            Assert.Equal(6L, recorded.Length);
            Assert.Equal(Receipt(archive, "target").Sha256, recorded.Sha256);
            Assert.Equal("targetstale tail", File.ReadAllText(source));
            Assert.Equal(10L, result.Statistics!.ImportedLocalBytes);
            Assert.Equal(10L, result.Statistics.TargetBytes);
            Assert.True((await Service(bridge).DownloadAsync(new(fixture.Cache, "again", [depot]))).FromCache);
        });

        suite.Add("Steam: fresh completion with a manifest-verified stale tail stores only target bytes", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var archive = Path.Combine("Data", "archive.bsa");
            FixtureFile[] target = [new("SkyrimSE.exe", "game"), new(archive, "target")];
            WritePublicManifest(fixture.Root, depot, target);
            var source = RawDepotFile(fixture.Root, depot, archive);
            var bridge = new FakeSteam(fixture.Root)
            {
                AfterDispatch = _ => File.AppendAllText(source, "stale tail", new UTF8Encoding(false))
            };
            bridge.Payloads[depot.DepotId] = target;
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]));
            Assert.True(!result.FromCache);
            Assert.Equal(1, bridge.Commands.Count);
            Assert.Equal("target", File.ReadAllText(Path.Combine(result.SourceDirectories.Single(), archive)));
            Assert.Equal(6L, result.Files.Single(file => file.RelativePath == archive).Length);
            Assert.Equal("targetstale tail", File.ReadAllText(source));
            Assert.Equal(10L, result.Statistics!.SteamRequestedBytes);
            Assert.True(File.Exists(Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, depot), "complete.json")));
        });

        suite.Add("Steam: wrong oversized prefix and truncated target reject import and completion without a receipt", async () =>
        {
            foreach (var invalid in new[] { "wrong!stale tail", "targe" })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489833, "10", false);
                var archive = Path.Combine("Data", "archive.bsa");
                FixtureFile[] target = [new("SkyrimSE.exe", "game"), new(archive, "target")];
                WriteRawDepot(fixture.Root, depot, target);
                var source = RawDepotFile(fixture.Root, depot, archive);
                File.WriteAllText(source, invalid, new UTF8Encoding(false));
                var game = Path.Combine(fixture.Root, "installed");
                WriteFixtureFiles(game, [new("SkyrimSE.exe", "installed game")]);
                var bridge = new FakeSteam(fixture.Root)
                {
                    AfterDispatch = _ => File.WriteAllText(source, invalid, new UTF8Encoding(false))
                };
                bridge.Payloads[depot.DepotId] = target;
                var progress = new Recorder();
                await Assert.ThrowsAsync<InvalidDataException>(() => Service(bridge).DownloadAsync(
                    new(fixture.Cache, "test", [depot], InstalledGameDirectory: game, ReuseInstalledFiles: false), progress));
                Assert.Equal(1, bridge.Commands.Count);
                Assert.True(progress.Messages.Any(message => message.Contains("Ранее скачанные файлы", StringComparison.Ordinal)));
                Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
                Assert.Equal(invalid, File.ReadAllText(source));
                Assert.Equal("installed game", File.ReadAllText(Path.Combine(game, "SkyrimSE.exe")));
            }
        });

        suite.Add("Steam: missing public manifest preserves full-file verification and copy", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[depot.DepotId] = [new("SkyrimSE.exe", "gamestale tail")];
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]));
            Assert.Equal("gamestale tail", File.ReadAllText(Path.Combine(result.SourceDirectories.Single(), "SkyrimSE.exe")));
            Assert.Equal(14L, result.Files.Single().Length);
            Assert.Equal(Receipt("SkyrimSE.exe", "gamestale tail").Sha256, result.Files.Single().Sha256);
        });

        suite.Add("Steam: source growth after inventory is accepted only with a verified target manifest", async () =>
        {
            foreach (var withManifest in new[] { false, true })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489833, "10", false);
                FixtureFile[] target = [new("SkyrimSE.exe", "game")];
                if (withManifest) WritePublicManifest(fixture.Root, depot, target);
                var source = RawDepotFile(fixture.Root, depot, "SkyrimSE.exe");
                var bridge = new FakeSteam(fixture.Root);
                bridge.Payloads[depot.DepotId] = target;
                var progress = new Recorder(value =>
                {
                    if (value.Message == "Проверка депо: SkyrimSE.exe")
                        File.AppendAllText(source, "added tail", new UTF8Encoding(false));
                });
                var operation = Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]), progress);
                if (withManifest)
                {
                    var result = await operation;
                    Assert.Equal("game", File.ReadAllText(Path.Combine(result.SourceDirectories.Single(), "SkyrimSE.exe")));
                    Assert.Equal(4L, result.Files.Single().Length);
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidDataException>(() => operation);
                    Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
                }
                Assert.Equal("gameadded tail", File.ReadAllText(source));
                Assert.Equal(1, bridge.Commands.Count);
            }
        });

        suite.Add("Steam: source truncated after inventory cannot create a completed cache receipt", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            WriteRawDepot(fixture.Root, depot, [new("SkyrimSE.exe", "game")]);
            var source = RawDepotFile(fixture.Root, depot, "SkyrimSE.exe");
            var progress = new Recorder(value =>
            {
                if (value.Message.Contains("уже скачано и соответствует", StringComparison.Ordinal))
                    File.WriteAllText(source, "g", new UTF8Encoding(false));
            });
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            await Assert.ThrowsAsync<EndOfStreamException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]), progress));
            Assert.Equal(0, bridge.Commands.Count);
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
        });

        suite.Add("Steam: verified installed files are reused without including extra mod files", async () =>
        {
            using var fixture = new Fixture();
            var game = Path.Combine(fixture.Root, "installed");
            var depot = new DepotManifest(489833, "10", false);
            FixtureFile[] target = [new("SkyrimSE.exe", "game"), new(Path.Combine("Data", "archive.bsa"), "data", ChunkSizes: [2, 2])];
            WriteFixtureFiles(game, target.Append(new("mod.dll", "mod")).ToArray());
            WritePublicManifest(fixture.Root, depot, target);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot], InstalledGameDirectory: game));
            Assert.True(result.FromCache);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.Equal(8L, result.Statistics!.ReusedInstalledBytes);
            Assert.Equal(8L, result.Statistics.TargetBytes);
            Assert.Equal(0L, result.Statistics.SteamRequestedBytes);
            Assert.Equal<long?>(0, result.Statistics.SteamDownloadedBytes);
            Assert.True(!File.Exists(Path.Combine(result.SourceDirectories.Single(), "mod.dll")));
            Assert.Equal("mod", File.ReadAllText(Path.Combine(game, "mod.dll")));
            var again = await Service(bridge).DownloadAsync(new(fixture.Cache, "other", [depot], InstalledGameDirectory: game));
            Assert.Equal(8L, again.Statistics!.ReusedCacheBytes);
            Assert.Equal(0L, again.Statistics.ReusedInstalledBytes);
        });

        suite.Add("Steam: changed BSA reconstructs relocated source chunks through public manifests", async () =>
        {
            using var fixture = new Fixture();
            var game = Path.Combine(fixture.Root, "installed");
            var depot = new DepotManifest(489833, "10", false);
            var bsa = Path.Combine("Data", "archive.bsa");
            WriteFixtureFiles(game, [new("SkyrimSE.exe", "game"), new(bsa, "B2A1C3")]);
            WritePublicManifest(fixture.Root, depot, [new("SkyrimSE.exe", "game"), new(bsa, "A1B2", ChunkSizes: [2, 2])]);
            WritePublicManifest(fixture.Root, depot with { ManifestId = "11" }, [new("SkyrimSE.exe", "game"), new(bsa, "B2A1C3", ChunkSizes: [2, 2, 2])]);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot], InstalledGameDirectory: game));
            Assert.Equal("A1B2", File.ReadAllText(Path.Combine(result.SourceDirectories.Single(), bsa)));
            Assert.Equal("B2A1C3", File.ReadAllText(Path.Combine(game, bsa)));
            Assert.Equal(8L, result.Statistics!.ReusedInstalledBytes);
            Assert.Equal(0, bridge.Commands.Count);
        });

        suite.Add("Steam: incomplete or corrupt installed chunks fall back to full depot honestly", async () =>
        {
            using var fixture = new Fixture();
            var game = Path.Combine(fixture.Root, "installed");
            var depot = new DepotManifest(489833, "10", false);
            var bsa = Path.Combine("Data", "archive.bsa");
            WriteFixtureFiles(game, [new("SkyrimSE.exe", "game"), new(bsa, "A1??")]);
            FixtureFile[] target = [new("SkyrimSE.exe", "game"), new(bsa, "A1B2", ChunkSizes: [2, 2])];
            WritePublicManifest(fixture.Root, depot, target);
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[depot.DepotId] = target;
            var recorder = new Recorder();
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot], InstalledGameDirectory: game), recorder);
            Assert.True(!result.FromCache);
            Assert.Equal(1, bridge.Commands.Count);
            Assert.Equal(0L, result.Statistics!.ReusedInstalledBytes);
            Assert.Equal(8L, result.Statistics.SteamRequestedBytes);
            Assert.Equal<long?>(null, result.Statistics.SteamDownloadedBytes);
            Assert.Equal("A1??", File.ReadAllText(Path.Combine(game, bsa)));
            Assert.True(recorder.Messages.Any(m => m.Contains("целое депо", StringComparison.Ordinal)));
        });

        suite.Add("Steam: a foreign source depot manifest cannot authorize relocated chunks", async () =>
        {
            using var fixture = new Fixture();
            var game = Path.Combine(fixture.Root, "installed");
            var depot = new DepotManifest(489833, "10", false);
            var bsa = Path.Combine("Data", "archive.bsa");
            WriteFixtureFiles(game, [new("SkyrimSE.exe", "game"), new(bsa, "B2A1")]);
            FixtureFile[] target = [new("SkyrimSE.exe", "game"), new(bsa, "A1B2", ChunkSizes: [2, 2])];
            WritePublicManifest(fixture.Root, depot, target);
            File.WriteAllBytes(Path.Combine(fixture.Root, "depotcache", "489833_11.manifest"),
                MakeManifest(7, 11, [new(bsa, "B2A1", ChunkSizes: [2, 2])]));
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[depot.DepotId] = target;
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot], InstalledGameDirectory: game));
            Assert.Equal(1, bridge.Commands.Count);
            Assert.Equal(0L, result.Statistics!.ReusedInstalledBytes);
        });

        suite.Add("Steam: reuse mode can be disabled and cannot stage inside the installed game", async () =>
        {
            using var fixture = new Fixture();
            var game = Path.Combine(fixture.Root, "installed");
            var depot = new DepotManifest(489833, "10", false);
            FixtureFile[] target = [new("SkyrimSE.exe", "game")];
            WriteFixtureFiles(game, target);
            WritePublicManifest(fixture.Root, depot, target);
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[depot.DepotId] = target;
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot], InstalledGameDirectory: game, ReuseInstalledFiles: false));
            Assert.Equal(1, bridge.Commands.Count);
            Assert.Equal(0L, result.Statistics!.ReusedInstalledBytes);
            await Assert.ThrowsAsync<ArgumentException>(() => Service(bridge).DownloadAsync(
                new(Path.Combine(game, "cache"), "test", [depot], InstalledGameDirectory: game)));
            Assert.True(!Directory.Exists(Path.Combine(game, "cache")));
        });

        suite.Add("Steam: linked installed source is never followed and Steam fallback leaves it intact", async () =>
        {
            using var fixture = new Fixture();
            var game = Path.Combine(fixture.Root, "installed");
            var linked = Path.Combine(fixture.Root, "linked-game");
            var depot = new DepotManifest(489833, "10", false);
            FixtureFile[] target = [new("SkyrimSE.exe", "game")];
            WriteFixtureFiles(game, target);
            WritePublicManifest(fixture.Root, depot, target);
            CreateDirectoryLink(linked, game);
            try
            {
                var bridge = new FakeSteam(fixture.Root);
                bridge.Payloads[depot.DepotId] = target;
                var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot], InstalledGameDirectory: linked));
                Assert.Equal(1, bridge.Commands.Count);
                Assert.Equal(0L, result.Statistics!.ReusedInstalledBytes);
                Assert.Equal("game", File.ReadAllText(Path.Combine(game, "SkyrimSE.exe")));
            }
            finally { Directory.Delete(linked); }
        });

        suite.Add("Steam: RU overlay statistics distinguish cache and installed reuse", async () =>
        {
            using var fixture = new Fixture();
            var game = Path.Combine(fixture.Root, "installed");
            var baseDepot = new DepotManifest(489833, "10", false);
            var russian = new DepotManifest(489838, "20", true);
            WriteCache(fixture.Cache, baseDepot, [new("SkyrimSE.exe", "game"), new("Skyrim_Default.ini", "english")]);
            WriteFixtureFiles(game, [new("SkyrimSE.exe", "game"), new("Skyrim_Default.ini", "russian")]);
            WritePublicManifest(fixture.Root, russian, [new("Skyrim_Default.ini", "russian")]);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [baseDepot, russian], InstalledGameDirectory: game));
            Assert.Equal(11L, result.Statistics!.ReusedCacheBytes);
            Assert.Equal(7L, result.Statistics.ReusedInstalledBytes);
            Assert.Equal(18L, result.Statistics.TargetBytes);
            Assert.Equal("russian", File.ReadAllText(Path.Combine(result.SourceDirectories[^1], "Skyrim_Default.ini")));
            Assert.Equal(0, bridge.Commands.Count);
        });
        suite.Add("Steam: raw depot import honors RU exclusion and localization overlay", async () =>
        {
            using var fixture = new Fixture();
            var game = new DepotManifest(489833, "10", false);
            var russian = new DepotManifest(489838, "20", true);
            WriteRawDepot(fixture.Root, game, [new("SkyrimSE.exe", "game"), new("Skyrim_Default.ini", "english")]);
            WriteRawDepot(fixture.Root, russian, [new("Skyrim_Default.ini", "russian")]);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var english = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [game]));
            Assert.Equal(1, english.SourceDirectories.Count);
            Assert.Equal(Receipt("Skyrim_Default.ini", "english").Sha256, english.Files.Single(file => file.RelativePath == "Skyrim_Default.ini").Sha256);
            Assert.True(!Directory.Exists(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, russian)));
            var localized = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [game, russian]));
            Assert.Equal(2, localized.SourceDirectories.Count);
            Assert.Equal(Receipt("Skyrim_Default.ini", "russian").Sha256, localized.Files.Single(file => file.RelativePath == "Skyrim_Default.ini").Sha256);
            Assert.Equal(0, bridge.Commands.Count);
        });
        suite.Add("Steam: previously selected content directory imports only verified individual depots", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var files = new FixtureFile[] { new("SkyrimSE.exe", "game") };
            var localContent = Path.Combine(fixture.Root, "previous-files", "steamapps", "content");
            WriteFixtureFiles(Path.Combine(localContent, "app_489830", "depot_489833"), files);
            WritePublicManifest(fixture.Root, depot, files);
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot], localContent));
            Assert.True(result.FromCache);
            Assert.Equal("game", File.ReadAllText(Path.Combine(result.SourceDirectories.Single(), "SkyrimSE.exe")));
            Assert.Equal(0, bridge.Commands.Count);
        });
        suite.Add("Steam: merged installed-game directory is never used as raw depot import", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var files = new FixtureFile[] { new("SkyrimSE.exe", "game") };
            var game = Path.Combine(fixture.Root, "game");
            WriteFixtureFiles(game, files);
            WritePublicManifest(fixture.Root, depot, files);
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[489833] = files;
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot], game));
            Assert.True(!result.FromCache);
            Assert.Equal(1, bridge.Commands.Count);
            Assert.Equal("game", File.ReadAllText(Path.Combine(game, "SkyrimSE.exe")));
        });
        suite.Add("Steam: changed raw content fails SHA1 verification and falls back to Steam", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var files = new FixtureFile[] { new("SkyrimSE.exe", "game") };
            WriteRawDepot(fixture.Root, depot, files);
            File.WriteAllText(Path.Combine(fixture.Root, "steamapps", "content", "app_489830", "depot_489833", "SkyrimSE.exe"), "wrong");
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[489833] = files;
            var progress = new Recorder();
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]), progress);
            Assert.True(!result.FromCache);
            Assert.Equal(1, bridge.Commands.Count);
            Assert.True(progress.Messages.Any(message => message.Contains("не прошли проверку")));
            Assert.Equal("game", File.ReadAllText(Path.Combine(result.SourceDirectories.Single(), "SkyrimSE.exe")));
        });
        suite.Add("Steam: absent, wrong, encrypted and truncated manifests cannot authorize raw import", async () =>
        {
            foreach (var invalid in new[] { "absent", "wrong", "encrypted", "truncated" })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489833, "10", false);
                var files = new FixtureFile[] { new("SkyrimSE.exe", "game") };
                WriteRawDepot(fixture.Root, depot, files);
                var manifest = Path.Combine(fixture.Root, "depotcache", "489833_10.manifest");
                if (invalid == "absent") File.Delete(manifest);
                else File.WriteAllBytes(manifest, invalid switch
                {
                    "wrong" => MakeManifest(489833, 11, files),
                    "encrypted" => MakeManifest(489833, 10, files, encrypted: true),
                    _ => MakeManifest(489833, 10, files)[..^3]
                });
                var bridge = new FakeSteam(fixture.Root);
                bridge.Payloads[489833] = files;
                var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]));
                Assert.True(!result.FromCache);
                Assert.Equal(1, bridge.Commands.Count);
            }
        });
        suite.Add("Steam: raw import preserves a corrupt cache separately and replaces its receipt", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var files = new FixtureFile[] { new("SkyrimSE.exe", "game") };
            WriteRawDepot(fixture.Root, depot, files);
            WriteCache(fixture.Cache, depot, files);
            File.WriteAllText(Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, depot), "complete.json"), "{broken");
            var bridge = new FakeSteam(fixture.Root) { SessionForbidden = true };
            Assert.True((await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]))).FromCache);
            Assert.Equal(1, Directory.GetDirectories(Path.Combine(fixture.Cache, "corrupt")).Length);
            Assert.Equal(0, bridge.Commands.Count);
            Assert.True((await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]))).FromCache);
        });
        suite.Add("Steam: corrupt receipt variants and modified files are preserved then repaired", async () =>
        {
            foreach (var corrupt in new[] { "bad-json", "bad-hash", "duplicate", "null-entry", "escape" })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489833, "10", false);
                WriteCache(fixture.Cache, depot, [new("SkyrimSE.exe", "game")]);
                var depotRoot = SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, depot);
                var receiptPath = Path.Combine(depotRoot, "complete.json");
                if (corrupt == "bad-json") await File.WriteAllTextAsync(receiptPath, "{broken");
                else if (corrupt == "bad-hash") await File.WriteAllTextAsync(Path.Combine(depotRoot, "content", "SkyrimSE.exe"), "changed");
                else
                {
                    DownloadedFile[] files = corrupt switch
                    {
                        "duplicate" => [Receipt("SkyrimSE.exe", "game"), Receipt("SkyrimSE.exe", "game")],
                        "null-entry" => [null!], _ => [Receipt("../outside", "game")]
                    };
                    await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new DepotDownloadReceipt(1, 489830, 489833, "10", DateTimeOffset.UtcNow, files)));
                }
                var bridge = new FakeSteam(fixture.Root);
                bridge.Payloads[489833] = [new("SkyrimSE.exe", "game")];
                var progress = new Recorder();
                Assert.True(!(await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]), progress)).FromCache);
                Assert.Equal(1, bridge.Commands.Count);
                Assert.Equal(1, Directory.GetDirectories(Path.Combine(fixture.Cache, "corrupt")).Length);
                Assert.True(progress.Messages.Any(m => m.Contains("повреждён")));
            }
        });
        suite.Add("Steam: changing RU downloads only the missing depot and accepts new repeated completion", async () =>
        {
            using var fixture = new Fixture();
            var game = new DepotManifest(489833, "10", false);
            var russian = new DepotManifest(489838, "20", true);
            WriteCache(fixture.Cache, game, [new("SkyrimSE.exe", "game")]);
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[489838] = [new("Skyrim_Default.ini", "russian")];
            // An identical completion existed historically, but the NEW appended line is valid evidence.
            bridge.Console = bridge.CompletionLine(russian, 1) + "\n";
            var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [game, russian]));
            Assert.Equal(1, bridge.Commands.Count); Assert.Equal(489838U, bridge.Commands.Single().DepotId);
            Assert.Equal(2, result.SourceDirectories.Count);
        });

        suite.Add("Steam: oversized and excessive-file receipts are quarantined before verification", async () =>
        {
            foreach (var oversized in new[] { true, false })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489833, "10", false);
                WriteCache(fixture.Cache, depot, [new("SkyrimSE.exe", "game")]);
                var receiptPath = Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, depot), "complete.json");
                if (oversized)
                {
                    using var receipt = File.Open(receiptPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    receipt.SetLength(16L * 1024 * 1024 + 1);
                }
                else
                {
                    File.WriteAllText(receiptPath, JsonSerializer.Serialize(new DepotDownloadReceipt(1, 489830,
                        depot.DepotId, depot.ManifestId, DateTimeOffset.UtcNow, new DownloadedFile[100_001])));
                }
                var bridge = new FakeSteam(fixture.Root);
                bridge.Payloads[depot.DepotId] = [new("SkyrimSE.exe", "game")];
                var progress = new Recorder();
                var result = await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot]), progress);
                Assert.True(!result.FromCache);
                Assert.Equal(1, bridge.Commands.Count);
                Assert.Equal(1, Directory.GetDirectories(Path.Combine(fixture.Cache, "corrupt")).Length);
                Assert.True(progress.Messages.Any(message => message.Contains(oversized ? "16 МБ" : "100 000", StringComparison.Ordinal)));
            }
        });
        suite.Add("Steam: absent baseline never replays historic success when console first becomes visible", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root) { PublishCompletion = false, HideFirstConsole = true };
            var depot = new DepotManifest(489833, "10", false);
            bridge.Console = bridge.CompletionLine(depot, 1) + "\n";
            await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot])));
        });
        suite.Add("Steam: corrupt operational manifest yields diagnostic and verified Steam completion fallback", async () =>
        {
            using var fixture = new Fixture();
            Directory.CreateDirectory(Path.Combine(fixture.Root, "depotcache"));
            File.WriteAllBytes(Path.Combine(fixture.Root, "depotcache", "489833_10.manifest"), [1, 2, 3]);
            var bridge = new FakeSteam(fixture.Root);
            bridge.Payloads[489833] = [new("SkyrimSE.exe", "game")];
            var progress = new Recorder();
            Assert.True(!(await Service(bridge).DownloadAsync(new(fixture.Cache, "test", [new(489833, "10", false)]), progress)).FromCache);
            Assert.True(progress.Messages.Any(m => m.Contains("Манифест") && m.Contains("повреждён")));
        });
        suite.Add("Steam: baseline success cannot authorize new download", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root) { PublishCompletion = false };
            var depot = new DepotManifest(489833, "10", false);
            bridge.Console = bridge.CompletionLine(depot, 1) + "\n";
            var error = await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot])));
            Assert.Equal(SteamConsoleCommands.ForDepot(depot), error.Command);
        });
        suite.Add("Steam: partial or stale output cannot create a completion receipt", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root) { ReportedFileCount = 2 };
            bridge.Payloads[489833] = [new("SkyrimSE.exe", "game")];
            var depot = new DepotManifest(489833, "10", false);
            await Assert.ThrowsAsync<InvalidDataException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [depot])));
            Assert.True(!File.Exists(Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(fixture.Cache, depot), "complete.json")));
        });
        suite.Add("Steam: cancellation leaves no completed receipt and does not close client", async () =>
        {
            using var fixture = new Fixture();
            var bridge = new FakeSteam(fixture.Root) { PublishCompletion = false, PublishAcknowledgement = true };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(45));
            await Assert.ThrowsAsync<OperationCanceledException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [new(489833, "10", false)]), cancellationToken: cancellation.Token));
            Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
        });
        suite.Add("Steam: a dispatched download tolerates offline until recovery without repeating the command", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var bridge = new FakeSteam(fixture.Root)
            {
                PublishCompletion = false,
                AfterDispatch = steam => steam.Online = false,
                BeforeSessionQuery = steam =>
                {
                    if (steam.Commands.Count != 0 && steam.SessionQueries >= 12)
                    {
                        steam.Online = true;
                        WriteRawDepot(fixture.Root, depot, [new("SkyrimSE.exe", "game")]);
                        steam.Console += steam.CompletionLine(depot, 1) + "\n";
                    }
                }
            };
            var progress = new Recorder();
            var service = new SteamClientDownloadService(bridge, () => [fixture.Root])
            {
                PollInterval = TimeSpan.FromMilliseconds(15),
                CommandAcknowledgementTimeout = TimeSpan.FromMilliseconds(20),
                DepotTimeout = TimeSpan.FromSeconds(1)
            };
            await service.DownloadAsync(new(fixture.Cache, "test", [depot], ForceFreshDownload: true), progress);
            Assert.Equal(1, bridge.Commands.Count);
            Assert.True(progress.Messages.Any(message => message.Contains("потерял подключение", StringComparison.Ordinal)));
            Assert.True(!File.Exists(PendingCommandPath(fixture.Root, depot)));
        });
        suite.Add("Steam: terminal content-server failures preserve payload and permit a fresh retry", async () =>
        {
            foreach (var source in new[] { "console", "console_log.txt", "content_log.txt" })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489831, "8442952117333549665", false);
                WritePublicManifest(fixture.Root, depot, [new("SkyrimSE.exe", "replacement")]);
                const string output = "Downloading depot 489831 (19 files, 4663 MB) ...\n" +
                    "Depot download failed : Failed updating depot 489831 while starting download (No connection to content servers)\n";
                var log = Path.Combine(fixture.Root, "logs", source);
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.WriteAllText(log, "history\n");
                var first = new FakeSteam(fixture.Root)
                {
                    PublishCompletion = false,
                    AfterDispatch = steam =>
                    {
                        WriteFixtureFiles(Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!, [new("SkyrimSE.exe", "partial")]);
                        if (source == "console") steam.Console += output;
                        else File.AppendAllText(log, output);
                    }
                };
                var request = new DownloadRequest(fixture.Cache, "test", [depot], ForceFreshDownload: true);
                var error = await Assert.ThrowsAsync<IOException>(() => Service(first).DownloadAsync(request));
                Assert.True(error.Message.Contains("No connection to content servers", StringComparison.Ordinal));
                Assert.True(!File.Exists(PendingCommandPath(fixture.Root, depot)));
                Assert.Equal("partial", File.ReadAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")));
                var retry = new FakeSteam(fixture.Root);
                retry.Payloads[depot.DepotId] = [new("SkyrimSE.exe", "replacement")];
                await Service(retry).DownloadAsync(request);
                Assert.Equal(1, retry.Commands.Count);
                Assert.Equal("replacement", File.ReadAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")));
            }
        });
        suite.Add("Steam: pending retries recover only from a terminal failure in the same fresh log context", async () =>
        {
            foreach (var context in new[] { "console_log.txt", "content_log.txt", "command", "none", "historic", "other", "cross-log" })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489831, "8442952117333549665", false);
                WritePublicManifest(fixture.Root, depot, [new("SkyrimSE.exe", "replacement")]);
                const string acknowledgement = "[2026-10-05 21:31:05] Downloading depot 489831 (19 files, 4663 MB) ...\n";
                const string failure = "[2026-10-05 21:31:05] Depot download failed : Failed updating depot 489831 while starting download (No connection to content servers)\n";
                var consoleLog = Path.Combine(fixture.Root, "logs", "console_log.txt");
                var contentLog = Path.Combine(fixture.Root, "logs", "content_log.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(consoleLog)!);
                File.WriteAllText(consoleLog, context == "historic" ? acknowledgement : "history\n");
                File.WriteAllText(contentLog, "history\n");
                using var cancellation = new CancellationTokenSource();
                var first = new FakeSteam(fixture.Root)
                {
                    PublishCompletion = false,
                    AfterDispatch = _ =>
                    {
                        WriteFixtureFiles(Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!, [new("SkyrimSE.exe", "partial")]);
                        cancellation.Cancel();
                    }
                };
                var request = new DownloadRequest(fixture.Cache, "test", [depot], ForceFreshDownload: true);
                await Assert.ThrowsAsync<OperationCanceledException>(() => Service(first).DownloadAsync(request, cancellationToken: cancellation.Token));
                var log = context == "content_log.txt" ? contentLog : consoleLog;
                if (context is "console_log.txt" or "content_log.txt" or "cross-log") File.AppendAllText(log, acknowledgement);
                if (context == "command") File.AppendAllText(log, "] " + SteamConsoleCommands.ForDepot(depot) + "\n");
                if (context == "other") File.AppendAllText(log, acknowledgement.Replace("489831", "489832"));
                File.AppendAllText(context == "cross-log" ? contentLog : log, failure);
                var retry = new FakeSteam(fixture.Root);
                retry.Payloads[depot.DepotId] = [new("SkyrimSE.exe", "replacement")];
                var terminal = context is "console_log.txt" or "content_log.txt" or "command";
                if (terminal) await Service(retry).DownloadAsync(request);
                else await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => Service(retry).DownloadAsync(request));
                Assert.Equal(terminal ? 1 : 0, retry.Commands.Count);
                Assert.Equal(!terminal, File.Exists(PendingCommandPath(fixture.Root, depot)));
                Assert.Equal(terminal ? "replacement" : "partial", File.ReadAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")));
            }
        });
        suite.Add("Steam: cancellation and timeout preserve an active writer across new bridge and cache", async () =>
        {
            foreach (var cancel in new[] { true, false })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489833, "10", false);
                var logs = Path.Combine(fixture.Root, "logs");
                Directory.CreateDirectory(logs);
                var log = Path.Combine(logs, "console_log.txt");
                var first = new FakeSteam(fixture.Root) { PublishCompletion = false, PublishAcknowledgement = true };
                File.WriteAllText(log, first.CompletionLine(depot, 1) + "\n"); // Historic success cannot release the pending command.
                using var cancellation = new CancellationTokenSource();
                first.AfterDispatch = steam =>
                {
                    WriteRawDepot(fixture.Root, depot, [new("SkyrimSE.exe", "ongoing")]);
                    if (cancel) cancellation.Cancel();
                };
                var service = new SteamClientDownloadService(first, () => [fixture.Root])
                {
                    PollInterval = TimeSpan.FromMilliseconds(5),
                    CommandAcknowledgementTimeout = TimeSpan.FromMilliseconds(20),
                    DepotTimeout = TimeSpan.FromMilliseconds(30)
                };
                var request = new DownloadRequest(fixture.Cache, "test", [depot], ForceFreshDownload: true);
                if (cancel)
                    await Assert.ThrowsAsync<OperationCanceledException>(() => service.DownloadAsync(request, cancellationToken: cancellation.Token));
                else
                    await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadAsync(request));
                Assert.True(File.Exists(PendingCommandPath(fixture.Root, depot)));
                var writingPath = RawDepotFile(fixture.Root, depot, "SkyrimSE.exe");
                await using var writer = new FileStream(writingPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer.Position = writer.Length;
                var retry = new FakeSteam(fixture.Root);
                retry.Payloads[depot.DepotId] = [new("SkyrimSE.exe", "replacement")];
                await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => Service(retry).DownloadAsync(
                    request with { CacheDirectory = Path.Combine(fixture.Root, "other-cache") }));
                await writer.WriteAsync(Encoding.UTF8.GetBytes(" still downloading"));
                await writer.FlushAsync();
                Assert.Equal(0, retry.Commands.Count);
                Assert.Equal(0, retry.PrepareCalls);
                using var reader = new StreamReader(new FileStream(writingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                Assert.Equal("ongoing still downloading", await reader.ReadToEndAsync());
                Assert.True(File.Exists(PendingCommandPath(fixture.Root, depot)));
            }
        });
        suite.Add("Steam: only appended exact completion releases a pending command before fresh retry", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            WritePublicManifest(fixture.Root, depot, [new("SkyrimSE.exe", "replacement")]);
            var log = Path.Combine(fixture.Root, "logs", "console_log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, "history\n");
            using var cancellation = new CancellationTokenSource();
            var first = new FakeSteam(fixture.Root)
            {
                PublishCompletion = false,
                AfterDispatch = _ =>
                {
                    WriteFixtureFiles(Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!, [new("SkyrimSE.exe", "previous")]);
                    cancellation.Cancel();
                }
            };
            var request = new DownloadRequest(fixture.Cache, "test", [depot], ForceFreshDownload: true);
            await Assert.ThrowsAsync<OperationCanceledException>(() => Service(first).DownloadAsync(request, cancellationToken: cancellation.Token));
            File.AppendAllText(log, first.CompletionLine(depot with { ManifestId = "11" }, 1) + "\n");
            var retry = new FakeSteam(fixture.Root);
            retry.Payloads[depot.DepotId] = [new("SkyrimSE.exe", "replacement")];
            await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => Service(retry).DownloadAsync(request));
            Assert.Equal("previous", File.ReadAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")));
            File.WriteAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"), "replacement");
            File.AppendAllText(log, first.CompletionLine(depot, 1) + "\n");
            await Service(retry).DownloadAsync(request);
            Assert.Equal(1, retry.Commands.Count);
            Assert.Equal("replacement", File.ReadAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")));
            Assert.True(!File.Exists(PendingCommandPath(fixture.Root, depot)));
        });
        suite.Add("Steam: replacement or truncated log history cannot release a pending writer", async () =>
        {
            using var fixture = new Fixture();
            var depot = new DepotManifest(489833, "10", false);
            var log = Path.Combine(fixture.Root, "logs", "console_log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            var first = new FakeSteam(fixture.Root) { PublishCompletion = false };
            var historic = first.CompletionLine(depot, 1) + "\n";
            File.WriteAllText(log, new string('a', historic.Length + 1) + "\n");
            using var cancellation = new CancellationTokenSource();
            first.AfterDispatch = _ =>
            {
                WriteRawDepot(fixture.Root, depot, [new("SkyrimSE.exe", "ongoing")]);
                cancellation.Cancel();
            };
            var request = new DownloadRequest(fixture.Cache, "test", [depot], ForceFreshDownload: true);
            await Assert.ThrowsAsync<OperationCanceledException>(() => Service(first).DownloadAsync(request, cancellationToken: cancellation.Token));
            var retry = new FakeSteam(fixture.Root);
            retry.Payloads[depot.DepotId] = [new("SkyrimSE.exe", "replacement")];
            File.WriteAllText(log, historic);
            await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => Service(retry).DownloadAsync(request));
            File.WriteAllText(log, new string('b', historic.Length + 1) + "\n" + historic);
            await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => Service(retry).DownloadAsync(request));
            Assert.Equal(0, retry.Commands.Count);
            Assert.Equal("ongoing", File.ReadAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")));
        });
        suite.Add("Steam: a proven reused PID releases the writer while unknown creation time does not", async () =>
        {
            foreach (var knownStart in new[] { true, false })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489833, "10", false);
                WritePublicManifest(fixture.Root, depot, [new("SkyrimSE.exe", "replacement")]);
                using var cancellation = new CancellationTokenSource();
                var first = new FakeSteam(fixture.Root)
                {
                    PublishCompletion = false,
                    ProcessStartedUtc = knownStart ? new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero) : null,
                    AfterDispatch = _ =>
                    {
                        WriteFixtureFiles(Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!, [new("SkyrimSE.exe", "ongoing")]);
                        cancellation.Cancel();
                    }
                };
                var request = new DownloadRequest(fixture.Cache, "test", [depot], ForceFreshDownload: true);
                await Assert.ThrowsAsync<OperationCanceledException>(() => Service(first).DownloadAsync(request, cancellationToken: cancellation.Token));
                var retry = new FakeSteam(fixture.Root) { ProcessStartedUtc = first.ProcessStartedUtc?.AddMinutes(1) };
                retry.Payloads[depot.DepotId] = [new("SkyrimSE.exe", "replacement")];
                if (knownStart) await Service(retry).DownloadAsync(request);
                else await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => Service(retry).DownloadAsync(request));
                Assert.Equal(knownStart ? 1 : 0, retry.Commands.Count);
                Assert.Equal(knownStart ? "replacement" : "ongoing", File.ReadAllText(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe")));
            }
        });
        suite.Add("Steam: exact terminal failure permits retry while generic or other-manifest failures do not", async () =>
        {
            foreach (var failure in new[] { "Depot download failed : Access Denied", "App: 489830, Depot: 489833, Manifest: 100 Failed", "App: 489830, Depot: 489833, Manifest: 10 Failed" })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489833, "10", false);
                WritePublicManifest(fixture.Root, depot, [new("SkyrimSE.exe", "replacement")]);
                var log = Path.Combine(fixture.Root, "logs", "console_log.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.WriteAllText(log, "history\n");
                using var cancellation = new CancellationTokenSource();
                var first = new FakeSteam(fixture.Root)
                {
                    PublishCompletion = false,
                    AfterDispatch = _ =>
                    {
                        WriteFixtureFiles(Path.GetDirectoryName(RawDepotFile(fixture.Root, depot, "SkyrimSE.exe"))!, [new("SkyrimSE.exe", "ongoing")]);
                        cancellation.Cancel();
                    }
                };
                var request = new DownloadRequest(fixture.Cache, "test", [depot], ForceFreshDownload: true);
                await Assert.ThrowsAsync<OperationCanceledException>(() => Service(first).DownloadAsync(request, cancellationToken: cancellation.Token));
                File.AppendAllText(log, failure + "\n");
                var retry = new FakeSteam(fixture.Root);
                retry.Payloads[depot.DepotId] = [new("SkyrimSE.exe", "replacement")];
                var exact = failure == "App: 489830, Depot: 489833, Manifest: 10 Failed";
                if (exact) await Service(retry).DownloadAsync(request);
                else await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() => Service(retry).DownloadAsync(request));
                Assert.Equal(exact ? 1 : 0, retry.Commands.Count);
            }
        });
        suite.Add("Steam: live rotated logs and appended pre-command partial lines cannot complete the command", async () =>
        {
            foreach (var partial in new[] { true, false })
            {
                using var fixture = new Fixture();
                var depot = new DepotManifest(489833, "10", false);
                var log = Path.Combine(fixture.Root, "logs", "console_log.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                var bridge = new FakeSteam(fixture.Root) { PublishCompletion = false, PublishAcknowledgement = true };
                var completion = bridge.CompletionLine(depot, 1) + "\n";
                File.WriteAllText(log, partial ? "old unfinished line " : new string('a', completion.Length * 2) + "\n");
                bridge.AfterDispatch = _ =>
                {
                    WriteRawDepot(fixture.Root, depot, [new("SkyrimSE.exe", "ongoing")]);
                    if (partial) File.AppendAllText(log, completion);
                    else File.WriteAllText(log, completion);
                };
                var service = new SteamClientDownloadService(bridge, () => [fixture.Root])
                {
                    PollInterval = TimeSpan.FromMilliseconds(5),
                    CommandAcknowledgementTimeout = TimeSpan.FromMilliseconds(20),
                    DepotTimeout = TimeSpan.FromMilliseconds(35)
                };
                await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadAsync(new(fixture.Cache, "test", [depot], ForceFreshDownload: true)));
                Assert.True(File.Exists(PendingCommandPath(fixture.Root, depot)));
                Assert.True(!Directory.EnumerateFiles(fixture.Cache, "complete.json", SearchOption.AllDirectories).Any());
            }
        });
        suite.Add("Steam: signed-out and offline sessions fail before dispatch", async () =>
        {
            foreach (var user in new uint[] { 0, 1 })
            {
                using var fixture = new Fixture();
                var bridge = new FakeSteam(fixture.Root) { ActiveUser = user, Online = user == 1 ? false : null };
                await Assert.ThrowsAsync<InvalidOperationException>(() => Service(bridge).DownloadAsync(new(fixture.Cache, "test", [new(489833, "10", false)])));
                Assert.Equal(0, bridge.Commands.Count);
            }
        });
        suite.Add("Local depots: resolves content parent, Steam root, app and direct depot in selected order", () =>
        {
            using var fixture = new Fixture();
            var content = Path.Combine(fixture.Root, "steamapps", "content");
            var app = Path.Combine(content, "app_489830");
            DepotManifest[] depots = [new(489833, "10", false), new(489838, "20", true)];
            foreach (var depot in depots) Directory.CreateDirectory(Path.Combine(app, "depot_" + depot.DepotId));
            foreach (var selected in new[] { fixture.Root, Path.Combine(fixture.Root, "steamapps"), content, app, Path.Combine(app, "depot_489833") })
            {
                var sources = LocalDepotResolver.ResolveSources(selected, depots);
                Assert.Equal(Path.Combine(app, "depot_489833"), sources[0]); Assert.Equal(Path.Combine(app, "depot_489838"), sources[1]);
            }
            Directory.Delete(Path.Combine(app, "depot_489838"));
            Assert.Throws<InvalidDataException>(() => LocalDepotResolver.ResolveSources(content, depots));
            Assert.Equal(1, LocalDepotResolver.ResolveSources(content, [depots[0]]).Count);
        });
        suite.Add("Steam libraries: modern and legacy VDF are found", () =>
        {
            using var fixture = new Fixture();
            var modern = Path.Combine(fixture.Root, "modern library"); var legacy = Path.Combine(fixture.Root, "legacy library");
            Directory.CreateDirectory(modern); Directory.CreateDirectory(legacy); Directory.CreateDirectory(Path.Combine(fixture.Root, "steamapps"));
            static string Escape(string path) => path.Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(fixture.Root, "steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{Escape(modern)}\" }} \"2\" \"{Escape(legacy)}\" }}");
            var paths = SteamClientLocator.GetLibraryRoots(fixture.Root);
            Assert.True(paths.Contains(modern) && paths.Contains(legacy));
        });
        suite.Add("Local depots: resolves independent cache and rejects merged files when excluding RU", () =>
        {
            using var fixture = new Fixture();
            DepotManifest[] depots = [new(489833, "10", false), new(489838, "20", true)];
            WriteCache(fixture.Cache, depots[0], [new("SkyrimSE.exe", "game")]);
            WriteCache(fixture.Cache, depots[1], [new("Skyrim_Default.ini", "russian")]);
            foreach (var selected in new[] { fixture.Cache, Path.Combine(fixture.Cache, "depots"), Path.Combine(fixture.Cache, "depots", "489830") })
                Assert.Equal(2, LocalDepotResolver.ResolveSources(selected, depots).Count);
            var merged = Path.Combine(fixture.Root, "merged"); Directory.CreateDirectory(merged);
            File.WriteAllText(Path.Combine(merged, "SkyrimSE.exe"), "game");
            Assert.Throws<InvalidDataException>(() => LocalDepotResolver.ResolveSources(merged, [depots[0]]));
            Assert.Equal(1, LocalDepotResolver.ResolveSources(merged, depots).Count);
        });
        suite.Add("Manifest: exact ulong identity and SHA1 inventory", () =>
        {
            using var stream = new MemoryStream(MakeManifest(489833, ulong.MaxValue, [new("SkyrimSE.exe", "game")]));
            var file = SteamManifestReader.Read(stream, 489833, ulong.MaxValue).Single();
            Assert.Equal("SkyrimSE.exe", file.RelativePath); Assert.Equal(4L, file.Length);
            Assert.Equal(Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("game"))), file.Sha1);
        });
        suite.Add("Manifest: rejects wrong identity, encrypted content, traversal and truncation", () =>
        {
            Assert.Throws<InvalidDataException>(() => SteamManifestReader.Read(new MemoryStream(MakeManifest(489833, 1, [])), 489833, 2));
            Assert.Throws<InvalidDataException>(() => SteamManifestReader.Read(new MemoryStream(MakeManifest(489833, 1, [], true)), 489833, 1));
            foreach (var path in new[] { "../outside", "D:\\outside", ".DepotDownloader\\secret", "file:stream", "Data\\..\\outside" })
                Assert.Throws<InvalidDataException>(() => SteamManifestReader.Read(new MemoryStream(MakeManifest(489833, 1, [new(path, "a")])), 489833, 1));
            Assert.Throws<EndOfStreamException>(() => SteamManifestReader.Read(new MemoryStream(MakeManifest(489833, 1, [new("a", "b")])[..^3]), 489833, 1));
        });
    }
    private static SteamClientDownloadService Service(FakeSteam bridge) => new(bridge, () => [bridge.SteamRoot])
    { PollInterval = TimeSpan.FromMilliseconds(5), CommandAcknowledgementTimeout = TimeSpan.FromMilliseconds(25), DepotTimeout = TimeSpan.FromSeconds(1) };
    private sealed class Recorder(Action<DownloadProgress>? observer = null) : IProgress<DownloadProgress>
    {
        public List<string> Messages { get; } = [];
        public void Report(DownloadProgress value) { Messages.Add(value.Message); observer?.Invoke(value); }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "svp-steam-test-" + Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Root, "cache");
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
    private sealed class FakeSteam(string root) : ISteamClientBridge
    {
        public string SteamRoot => root;
        public Dictionary<uint, FixtureFile[]> Payloads { get; } = [];
        public List<DepotManifest> Commands { get; } = [];
        public List<int> PreparedProcessIds { get; } = [];
        public List<int> DispatchedProcessIds { get; } = [];
        public List<int> ReadProcessIds { get; } = [];
        public bool SessionForbidden { get; set; }
        public bool PublishCompletion { get; init; } = true;
        public string? ReportedCompletionDirectory { get; init; }
        public bool PublishAcknowledgement { get; init; }
        public bool HideFirstConsole { get; init; }
        private int reads;
        public uint ActiveUser { get; set; } = 1;
        public int ProcessId { get; set; } = 42;
        public string? SteamDirectoryOverride { get; set; }
        public string? ExecutablePathOverride { get; set; }
        public Action<FakeSteam>? BeforeFirstConsoleRead { get; init; }
        public Action<FakeSteam>? DuringPreparation { get; init; }
        public Action<FakeSteam>? AfterFirstConsoleRead { get; init; }
        public Action<FakeSteam>? AfterDispatch { get; set; }
        public Action<FakeSteam, DepotManifest>? BeforeDispatch { get; init; }
        public Action<FakeSteam>? BeforeSessionQuery { get; init; }
        public bool? Online { get; set; } = true;
        public DateTimeOffset? ProcessStartedUtc { get; init; }
        public int? ReportedFileCount { get; init; }
        public string Console { get; set; } = "";
        public int SessionQueries { get; private set; }
        public int PrepareCalls { get; private set; }
        public SteamClientSession GetSession()
        {
            SessionQueries++;
            BeforeSessionQuery?.Invoke(this);
            return SessionForbidden ? throw new InvalidOperationException("Session must not be queried for cache")
                : new(SteamDirectoryOverride ?? root, ExecutablePathOverride ?? Path.Combine(root, "steam.exe"), ProcessId, ActiveUser, Online, ProcessStartedUtc);
        }
        public Task PrepareConsoleAsync(SteamClientSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls++;
            PreparedProcessIds.Add(session.ProcessId);
            DuringPreparation?.Invoke(this);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task<string?> ReadConsoleAsync(SteamClientSession session, CancellationToken cancellationToken)
        {
            ReadProcessIds.Add(session.ProcessId);
            var first = reads++ == 0;
            if (first) BeforeFirstConsoleRead?.Invoke(this);
            var snapshot = HideFirstConsole && first ? null : Console;
            if (first) AfterFirstConsoleRead?.Invoke(this);
            return Task.FromResult<string?>(snapshot);
        }
        public Task DispatchDepotAsync(SteamClientSession session, DepotManifest depot, CancellationToken cancellationToken)
        {
            BeforeDispatch?.Invoke(this, depot);
            Commands.Add(depot);
            DispatchedProcessIds.Add(session.ProcessId);
            if (PublishAcknowledgement) Console += $"Downloading depot {depot.DepotId} (1 MB)\n";
            if (!PublishCompletion) { AfterDispatch?.Invoke(this); return Task.CompletedTask; }
            var files = Payloads[depot.DepotId];
            var folder = Path.Combine(root, "steamapps", "content", "app_489830", "depot_" + depot.DepotId);
            foreach (var file in files)
            {
                var path = Path.Combine(folder, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content, new UTF8Encoding(false));
            }
            Console += CompletionLine(depot, ReportedFileCount ?? files.Length, ReportedCompletionDirectory) + "\n";
            AfterDispatch?.Invoke(this);
            return Task.CompletedTask;
        }
        public string CompletionLine(DepotManifest depot, int count, string? destinationDirectory = null) =>
            $"Depot download complete : \"{destinationDirectory ?? Path.Combine(root, "steamapps", "content", "app_489830", "depot_" + depot.DepotId)}\" ({count} files, manifest {depot.ManifestId})";
    }
    private static string PendingCommandPath(string steam, DepotManifest depot) =>
        Path.Combine(steam, "steamapps", "content", "app_489830", $".svp-pending-{depot.DepotId}.json");
    private sealed record FixtureFile(string Path, string Content, uint Flags = 0, IReadOnlyList<int>? ChunkSizes = null);
    private static DownloadedFile Receipt(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content); return new(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    private static void WriteCache(string cache, DepotManifest depot, FixtureFile[] files)
    {
        var folder = SteamClientDownloadService.GetDepotCacheDirectory(cache, depot);
        foreach (var file in files)
        {
            var path = Path.Combine(folder, "content", file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content, new UTF8Encoding(false));
        }
        File.WriteAllText(Path.Combine(folder, "complete.json"), JsonSerializer.Serialize(new DepotDownloadReceipt(1, 489830,
            depot.DepotId, depot.ManifestId, DateTimeOffset.UtcNow, files.Select(f => Receipt(f.Path, f.Content)).ToArray())));
    }
    private static void WriteRawDepot(string steamRoot, DepotManifest depot, FixtureFile[] files)
    {
        WriteFixtureFiles(Path.Combine(steamRoot, "steamapps", "content", "app_489830", "depot_" + depot.DepotId), files);
        WritePublicManifest(steamRoot, depot, files);
    }
    private static string RawDepotFile(string steamRoot, DepotManifest depot, string relative) =>
        Path.Combine(steamRoot, "steamapps", "content", "app_489830", "depot_" + depot.DepotId, relative);
    private static void WriteFixtureFiles(string folder, FixtureFile[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content, new UTF8Encoding(false));
        }
    }
    private static void WritePublicManifest(string steamRoot, DepotManifest depot, FixtureFile[] files)
    {
        var manifestPath = Path.Combine(steamRoot, "depotcache", $"{depot.DepotId}_{depot.ManifestId}.manifest");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllBytes(manifestPath, MakeManifest(depot.DepotId, ulong.Parse(depot.ManifestId), files));
    }
    private static byte[] MakeManifest(uint depot, ulong manifest, FixtureFile[] files, bool encrypted = false)
    {
        using var payload = new MemoryStream();
        foreach (var file in files)
        {
            using var mapping = new MemoryStream(); var bytes = Encoding.UTF8.GetBytes(file.Content);
            Bytes(mapping, 1, Encoding.UTF8.GetBytes(file.Path)); Number(mapping, 2, (ulong)bytes.Length);
            Number(mapping, 3, file.Flags); Bytes(mapping, 5, SHA1.HashData(bytes));
            if (file.ChunkSizes is not null)
            {
                var offset = 0;
                foreach (var length in file.ChunkSizes)
                {
                    using var chunk = new MemoryStream();
                    Bytes(chunk, 1, SHA1.HashData(bytes.AsSpan(offset, length)));
                    Number(chunk, 3, (ulong)offset); Number(chunk, 4, (ulong)length); Number(chunk, 5, (ulong)length);
                    Bytes(mapping, 6, chunk.ToArray());
                    offset += length;
                }
                Assert.Equal(bytes.Length, offset);
            }
            Bytes(payload, 1, mapping.ToArray());
        }
        using var metadata = new MemoryStream(); Number(metadata, 1, depot); Number(metadata, 2, manifest); Number(metadata, 4, encrypted ? 1UL : 0UL);
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(0x71F617D0U); writer.Write((uint)payload.Length); writer.Write(payload.ToArray());
        writer.Write(0x1F4812BEU); writer.Write((uint)metadata.Length); writer.Write(metadata.ToArray());
        writer.Write(0x1B81B817U); writer.Write(0U); writer.Write(0x32C415ABU); return output.ToArray();
    }
    private static void Bytes(Stream stream, uint field, byte[] bytes) { Varint(stream, (field << 3) | 2); Varint(stream, (ulong)bytes.Length); stream.Write(bytes); }
    private static void Number(Stream stream, uint field, ulong value) { Varint(stream, field << 3); Varint(stream, value); }
    private static void Varint(Stream stream, ulong value)
    {
        while (value >= 128) { stream.WriteByte((byte)((value & 127) | 128)); value >>= 7; } stream.WriteByte((byte)value);
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(process.StandardError.ReadToEnd());
    }
}
