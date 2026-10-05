using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using SkyrimVersionPatcher.Core.Downloading;
using SkyrimVersionPatcher.Core.Identity;

namespace SkyrimVersionPatcher.App;

internal sealed record UserSettings(string GameDirectory = "", string LocalDirectory = "", string VersionId = "1.6.1170",
    bool UseSteamDirectory = true, string InstallationMode = "SteamDepots", bool RenameContentCatalog = true,
    string? GameLanguage = null, string? InterfaceLanguage = null, bool StopSkyrimUpdates = true);

internal static class DesktopServices
{
    public static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimVersionPatcher");

    public static void MigrateLegacyData(string legacyDirectory, string appDataDirectory)
    {
        var legacy = Path.GetFullPath(legacyDirectory);
        var destination = Path.GetFullPath(appDataDirectory);
        if (Path.TrimEndingDirectorySeparator(legacy).Equals(Path.TrimEndingDirectorySeparator(destination), StringComparison.OrdinalIgnoreCase)) return;
        var settings = Path.Combine(legacy, "settings.json");
        var migratedSettings = Path.Combine(destination, "settings.json");
        if (File.Exists(settings) && !File.Exists(migratedSettings))
        {
            RejectLinkedPath(settings);
            RejectLinkedPath(migratedSettings);
            Directory.CreateDirectory(destination);
            File.Move(settings, migratedSettings);
        }
        var storage = Path.Combine(legacy, "Storage");
        var migratedStorage = Path.Combine(destination, "Storage");
        if (!Directory.Exists(storage)) return;
        if (Overlap(storage, migratedStorage)) throw new IOException("Папки переноса рабочих данных пересекаются.");
        MoveLegacyDirectory(storage, migratedStorage);
    }

    private static void MoveLegacyDirectory(string source, string destination)
    {
        RejectLinkedPath(source);
        RejectLinkedPath(destination);
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination, entry.Name);
            RejectLinkedPath(entry.FullName);
            if (entry is DirectoryInfo) MoveLegacyDirectory(entry.FullName, target);
            else if (!File.Exists(target)) File.Move(entry.FullName, target);
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }

    private static void RejectLinkedPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Путь переноса содержит ссылку или junction: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public static UserSettings LoadSettings()
    {
        var file = Path.Combine(AppDataDirectory, "settings.json");
        try { return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(file)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public static void SaveSettings(UserSettings settings)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var path = Path.Combine(AppDataDirectory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    public static void RestoreCompatibilityOptions(UserSettings settings, CheckBox renameCatalog, CheckBox stopUpdates)
    {
        renameCatalog.IsChecked = settings.RenameContentCatalog;
        stopUpdates.IsChecked = settings.StopSkyrimUpdates;
    }
    public static string? FindGame() => SteamClientLocator.FindGameDirectory(includeInterruptedInstallations: true);
    public static string? GetExecutableVersion(string gameDirectory)
    {
        var file = Path.Combine(gameDirectory, "SkyrimSE.exe");
        return File.Exists(file) ? ExecutableIdentityService.ReadWindowsMetadataVersion(file) : null;
    }
    public static void EnsureGameStopped()
    {
        foreach (var name in new[] { "SkyrimSE", "SkyrimSELauncher", "skse64_loader" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0) throw new InvalidOperationException(AppLocalization.Translate("Закройте Skyrim, лаунчер и SKSE перед изменением файлов игры.", "Close Skyrim, its launcher and SKSE before changing game files."));
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    public static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.00} {AppLocalization.Translate("ГБ", "GB")}" : $"{bytes / (1024.0 * 1024):0.0} {AppLocalization.Translate("МБ", "MB")}";

    public static string CanonicalDirectory(string value, string caption)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"{caption}: {AppLocalization.Translate("укажите полный путь к папке.", "enter a full folder path.")}");
        var path = Path.GetFullPath(value.Trim());
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidOperationException($"{caption}: {AppLocalization.Translate("выберите локальный диск.", "select a local drive.")}");
        if (path.Length >= 2 && path[1] == ':') path = char.ToUpperInvariant(path[0]) + path[1..];
        return Path.TrimEndingDirectorySeparator(path);
    }

    public static bool Overlap(string first, string second)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

}
