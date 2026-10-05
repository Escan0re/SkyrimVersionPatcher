using System.IO;
using System.Text.RegularExpressions;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.App;

internal sealed record SteamConsoleCssClasses(string Console, string ConsoleInput, string InputBox);

/// <summary>Identifies the console using the installed public UI bundle, without executing JavaScript.</summary>
internal static partial class SteamConsoleIdentity
{
    internal static IReadOnlyList<SteamConsoleCssClasses> ReadClasses(string steamDirectory)
    {
        var directory = Path.Combine(steamDirectory, "steamui");
        if (!Directory.Exists(directory)) return [];
        var found = new HashSet<SteamConsoleCssClasses>();
        long readBytes = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.js", SearchOption.TopDirectoryOnly)
                     .OrderBy(file => Path.GetFileName(file).StartsWith("chunk", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(Path.GetFileName, StringComparer.Ordinal).Take(64))
        {
            var info = new FileInfo(file);
            if (info.Length > 32 * 1024 * 1024 || readBytes + info.Length > 128 * 1024 * 1024) continue;
            readBytes += info.Length;
            foreach (var classes in ExtractClasses(File.ReadAllText(file))) found.Add(classes);
        }
        return found.ToArray();
    }

    internal static IReadOnlyList<SteamConsoleCssClasses> ExtractClasses(string source) =>
        ConsoleClassesRegex().Matches(source).Select(match => new SteamConsoleCssClasses(
            match.Groups["console"].Value, match.Groups["form"].Value, match.Groups["input"].Value)).Distinct().ToArray();

    internal static bool HasClass(string classNames, string expected) =>
        classNames.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(expected, StringComparer.Ordinal);

    internal static bool IsConsoleRoute(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("steamloopback.host", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Equals("/console", StringComparison.Ordinal) || path.Equals("/routes/console", StringComparison.Ordinal);
    }

    internal static bool IsSteamClientDocument(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("steamloopback.host", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath.Equals("/index.html", StringComparison.Ordinal);

    internal static void RequireSameAccountAndExecutable(SteamClientSession expected, SteamClientSession current)
    {
        if (current.ActiveUser == 0 || current.ActiveUser != expected.ActiveUser ||
            !Path.GetFullPath(current.ExecutablePath).Equals(Path.GetFullPath(expected.ExecutablePath), StringComparison.OrdinalIgnoreCase) ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.SteamDirectory)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected.SteamDirectory)), StringComparison.OrdinalIgnoreCase))
            throw new SteamConsoleUnavailableException("Аккаунт или установленный клиент Steam изменился. Загрузка остановлена до отправки команд. Повторите установку с нужным аккаунтом Steam.");
    }

    // All four properties must belong to one CSS-module object. A similarly named input elsewhere is insufficient.
    [GeneratedRegex(@"\bConsole\s*:\s*""(?<console>[_a-zA-Z0-9-]{4,100})""[^{}]{0,4000}\bSpewLine\s*:\s*""[_a-zA-Z0-9-]{4,100}""[^{}]{0,4000}\bConsoleInput\s*:\s*""(?<form>[_a-zA-Z0-9-]{4,100})""[^{}]{0,1000}\bInputBox\s*:\s*""(?<input>[_a-zA-Z0-9-]{4,100})""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ConsoleClassesRegex();
}
