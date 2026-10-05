using SkyrimVersionPatcher.Core.Downloading;

public static class SkyrimUpdateProtectionTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Update protection finds the selected Steam installation without discovery", () =>
        {
            using var fixture = new Fixture();
            var install = fixture.Install("Steam", "Skyrim Special Edition");
            Assert.Equal(install.Manifest, SkyrimUpdateProtection.FindManifest(install.Game + Path.DirectorySeparatorChar, []));
            Assert.True((File.GetAttributes(install.Manifest) & FileAttributes.ReadOnly) == 0);
        });
        suite.Add("Update protection prefers the selected installation when another library also has Skyrim", () =>
        {
            using var fixture = new Fixture();
            var selected = fixture.Install("Library", "Custom Skyrim Folder");
            var other = fixture.Install("Steam", "Skyrim Special Edition");
            fixture.AddLibrary(other.Root, selected.Root);
            Assert.Equal(selected.Manifest, SkyrimUpdateProtection.FindManifest(selected.Game, [other.Root]));
        });
        suite.Add("Update protection discovers a secondary Steam library for a separate game copy", () =>
        {
            using var fixture = new Fixture();
            var steam = Path.Combine(fixture.Root, "Steam");
            var install = fixture.Install("Secondary Library", "Skyrim Special Edition");
            fixture.AddLibrary(steam, install.Root);
            var copy = fixture.Copy();
            Assert.Equal(install.Manifest, SkyrimUpdateProtection.FindManifest(copy, [steam, steam]));
        });
        suite.Add("Update protection rejects ambiguous Steam installations for a separate game copy", () =>
        {
            using var fixture = new Fixture();
            var first = fixture.Install("Steam", "Skyrim Special Edition");
            var second = fixture.Install("Secondary Library", "Skyrim Special Edition");
            fixture.AddLibrary(first.Root, second.Root);
            Assert.Throws<InvalidOperationException>(() => SkyrimUpdateProtection.FindManifest(fixture.Copy(), [first.Root]));
            Assert.True((File.GetAttributes(first.Manifest) & FileAttributes.ReadOnly) == 0);
            Assert.True((File.GetAttributes(second.Manifest) & FileAttributes.ReadOnly) == 0);
        });
        suite.Add("Update protection returns no manifest when Steam installation is missing", () =>
        {
            using var fixture = new Fixture();
            Assert.Equal<string?>(null, SkyrimUpdateProtection.FindManifest(fixture.Copy(), [Path.Combine(fixture.Root, "Steam")]));
            var stale = fixture.Install("Stale Steam", "Skyrim Special Edition");
            Directory.Delete(stale.Game, true);
            Assert.Equal<string?>(null, SkyrimUpdateProtection.FindManifest(fixture.Copy(), [stale.Root]));
        });
        suite.Add("Update protection validates app identity and selected installdir", () =>
        {
            using var fixture = new Fixture();
            var install = fixture.Install("Steam", "Skyrim Special Edition");
            File.WriteAllText(install.Manifest, "\"AppState\" { \"appid\" \"1\" \"installdir\" \"Skyrim Special Edition\" }");
            Assert.Equal<string?>(null, SkyrimUpdateProtection.FindManifest(install.Game, [install.Root]));
            File.WriteAllText(install.Manifest, "\"AppState\" { \"appid\" \"489830\" \"installdir\" \"Other Skyrim\" }");
            Assert.Equal<string?>(null, SkyrimUpdateProtection.FindManifest(install.Game, []));
        });
        suite.Add("Update protection rejects unsafe installdir and duplicate identity fields", () =>
        {
            using var fixture = new Fixture();
            var install = fixture.Install("Steam", "Skyrim Special Edition");
            foreach (var name in new[] { "..", ".", "../Skyrim Special Edition", "C:\\Skyrim", "Skyrim Special Edition.", "Skyrim Special Edition " })
            {
                File.WriteAllText(install.Manifest, $"\"AppState\" {{ \"appid\" \"489830\" \"installdir\" \"{name}\" }}");
                Assert.Equal<string?>(null, SkyrimUpdateProtection.FindManifest(install.Game, [install.Root]));
            }
            File.WriteAllText(install.Manifest, "\"appid\" \"1\" \"appid\" \"489830\" \"installdir\" \"Skyrim Special Edition\"");
            Assert.Equal<string?>(null, SkyrimUpdateProtection.FindManifest(install.Game, [install.Root]));
        });
        suite.Add("Update protection adds read-only idempotently while preserving attributes and content", () =>
        {
            using var fixture = new Fixture();
            var install = fixture.Install("Steam", "Skyrim Special Edition");
            if (OperatingSystem.IsWindows())
                File.SetAttributes(install.Manifest, FileAttributes.Archive | FileAttributes.Hidden);
            var originalAttributes = File.GetAttributes(install.Manifest);
            var originalText = File.ReadAllText(install.Manifest);
            SkyrimUpdateProtection.SetReadOnly(install.Manifest);
            var protectedAttributes = File.GetAttributes(install.Manifest);
            Assert.True((protectedAttributes & FileAttributes.ReadOnly) != 0);
            Assert.Equal(originalAttributes & ~FileAttributes.Normal, protectedAttributes & ~FileAttributes.ReadOnly);
            SkyrimUpdateProtection.SetReadOnly(install.Manifest);
            Assert.Equal(protectedAttributes, File.GetAttributes(install.Manifest));
            Assert.Equal(originalText, File.ReadAllText(install.Manifest));
        });
        suite.Add("Update protection reports missing files and rejects directory targets", () =>
        {
            using var fixture = new Fixture();
            Assert.Throws<FileNotFoundException>(() => SkyrimUpdateProtection.SetReadOnly(Path.Combine(fixture.Root, "appmanifest_489830.acf")));
            Assert.Throws<IOException>(() => SkyrimUpdateProtection.SetReadOnly(fixture.Root));
            Assert.Throws<ArgumentException>(() => SkyrimUpdateProtection.FindManifest("", []));
            Assert.Throws<ArgumentException>(() => SkyrimUpdateProtection.SetReadOnly(""));
        });
        suite.Add("Update protection rejects manifest symlinks when supported", () =>
        {
            using var fixture = new Fixture();
            var install = fixture.Install("Steam", "Skyrim Special Edition");
            var target = Path.Combine(fixture.Root, "real-manifest.acf");
            File.Move(install.Manifest, target);
            fixture.TrackFile(target);
            if (!fixture.TryLink(install.Manifest, target, directory: false)) return;
            Assert.Throws<IOException>(() => SkyrimUpdateProtection.FindManifest(install.Game, []));
            Assert.Throws<IOException>(() => SkyrimUpdateProtection.SetReadOnly(install.Manifest));
            Assert.True((File.GetAttributes(target) & FileAttributes.ReadOnly) == 0);
        });
        suite.Add("Update protection rejects linked installation directories when supported", () =>
        {
            using var fixture = new Fixture();
            var install = fixture.Install("Steam", "Skyrim Special Edition");
            var copy = fixture.Copy();
            Directory.Delete(install.Game, true);
            if (!fixture.TryLink(install.Game, copy, directory: true)) return;
            Assert.Throws<IOException>(() => SkyrimUpdateProtection.FindManifest(install.Game, []));
            Assert.Throws<IOException>(() => SkyrimUpdateProtection.FindManifest(copy, [install.Root]));
            Assert.True((File.GetAttributes(install.Manifest) & FileAttributes.ReadOnly) == 0);
        });
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "skyrim-update-protection-test-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> files = [];
        private readonly List<(string Path, bool Directory)> links = [];

        public Fixture() => Directory.CreateDirectory(Root);

        public (string Root, string Game, string Manifest) Install(string libraryName, string installName)
        {
            var library = Path.Combine(Root, libraryName);
            var game = Path.Combine(library, "steamapps", "common", installName);
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "SkyrimSE.exe"), "fixture executable");
            var manifest = Path.Combine(library, "steamapps", "appmanifest_489830.acf");
            File.WriteAllText(manifest, $"\"AppState\"\n{{\n\"appid\" \"489830\"\n\"installdir\" \"{installName}\"\n}}\n");
            TrackFile(manifest);
            return (library, game, manifest);
        }

        public string Copy()
        {
            var copy = Path.Combine(Root, "Separate Skyrim Copy");
            Directory.CreateDirectory(copy);
            File.WriteAllText(Path.Combine(copy, "SkyrimSE.exe"), "fixture executable");
            return copy;
        }

        public void AddLibrary(string steam, string library)
        {
            var steamApps = Path.Combine(steam, "steamapps");
            Directory.CreateDirectory(steamApps);
            File.WriteAllText(Path.Combine(steamApps, "libraryfolders.vdf"),
                $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{library.Replace("\\", "\\\\")}\" }} }}");
        }

        public void TrackFile(string path) => files.Add(path);

        public bool TryLink(string path, string target, bool directory)
        {
            try
            {
                if (directory) Directory.CreateSymbolicLink(path, target);
                else File.CreateSymbolicLink(path, target);
                links.Add((path, directory));
                return true;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            foreach (var (path, directory) in links)
            {
                if (directory) Directory.Delete(path);
                else File.Delete(path);
            }
            foreach (var file in files)
                if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
