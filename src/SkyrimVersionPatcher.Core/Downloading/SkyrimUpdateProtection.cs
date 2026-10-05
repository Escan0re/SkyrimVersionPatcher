using System.Text.RegularExpressions;

namespace SkyrimVersionPatcher.Core.Downloading;

public static partial class SkyrimUpdateProtection
{
    private const string ManifestFileName = "appmanifest_489830.acf";

    /// <summary>Finds the selected installation's manifest, or the unique Steam installation for a separate game copy.</summary>
    public static string? FindManifest(string gameDirectory, IEnumerable<string>? steamDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        DownloadPathSafety.EnsureNoLinks(game);

        var common = Path.GetDirectoryName(game);
        var steamApps = common is null ? null : Path.GetDirectoryName(common);
        if (common is not null && steamApps is not null &&
            Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(steamApps).Equals("steamapps", StringComparison.OrdinalIgnoreCase))
        {
            var selectedManifest = Path.Combine(steamApps, ManifestFileName);
            if (ReadInstallationDirectory(selectedManifest) is { } installed &&
                installed.Equals(game, StringComparison.OrdinalIgnoreCase)) return selectedManifest;
        }

        var manifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steam in steamDirectories ?? SteamClientLocator.FindSteamDirectories())
        {
            if (string.IsNullOrWhiteSpace(steam)) continue;
            var steamRoot = Path.GetFullPath(steam);
            DownloadPathSafety.EnsureNoLinks(steamRoot);
            foreach (var library in SteamClientLocator.GetLibraryRoots(steamRoot))
            {
                var manifest = Path.Combine(library, "steamapps", ManifestFileName);
                if (ReadInstallationDirectory(manifest) is { } installed && Directory.Exists(installed))
                    manifests.Add(manifest);
            }
        }

        return manifests.Count switch
        {
            0 => null,
            1 => manifests.Single(),
            _ => throw new InvalidOperationException(
                "Найдено несколько установок Skyrim в Steam. Нельзя однозначно выбрать appmanifest_489830.acf для защиты от обновлений.")
        };
    }

    /// <summary>Adds read-only without clearing existing attributes and verifies that Windows accepted it.</summary>
    public static void SetReadOnly(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var path = Path.GetFullPath(manifestPath);
        DownloadPathSafety.EnsureNoLinks(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
            throw new IOException("Вместо appmanifest_489830.acf указан каталог.");
        if ((attributes & FileAttributes.ReadOnly) == 0)
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0)
            throw new IOException("Не удалось установить режим «только для чтения» для appmanifest_489830.acf.");
    }

    private static string? ReadInstallationDirectory(string manifestPath)
    {
        DownloadPathSafety.EnsureNoLinks(manifestPath);
        if (!File.Exists(manifestPath)) return null;
        var text = File.ReadAllText(manifestPath);
        var appIds = AppIdRegex().Matches(text);
        var installDirectories = InstallDirectoryRegex().Matches(text);
        if (appIds.Count != 1 || appIds[0].Groups[1].Value != "489830" || installDirectories.Count != 1)
            return null;
        var name = installDirectories[0].Groups[1].Value.Replace(@"\\", @"\").Replace("\\\"", "\"");
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(['/', '\\', ':']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        var game = Path.Combine(Path.GetDirectoryName(manifestPath)!, "common", name);
        DownloadPathSafety.EnsureNoLinks(game);
        return game;
    }

    [GeneratedRegex("\\\"appid\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex("\\\"installdir\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirectoryRegex();
}
