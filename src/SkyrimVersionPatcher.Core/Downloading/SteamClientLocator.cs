using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SkyrimVersionPatcher.Core.Downloading;

public static partial class SteamClientLocator
{
    public static IReadOnlyList<string> FindSteamDirectories()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return FindWindowsSteamDirectories();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> FindWindowsSteamDirectories()
    {
        var candidates = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var steam = root.OpenSubKey(@"Software\Valve\Steam");
                foreach (var value in new[] { "SteamPath", "InstallPath" })
                    if (steam?.GetValue(value) is string path) candidates.Add(path);
                if (steam?.GetValue("SteamExe") is string executable && Path.GetDirectoryName(executable) is { } parent)
                    candidates.Add(parent);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Steam");
                if (uninstall?.GetValue("InstallLocation") is string install) candidates.Add(install);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try
                {
                    if (WindowsProcessIdentity.TryGetExecutablePath(process.Id, out var executable, out _))
                        candidates.Add(Path.GetDirectoryName(executable)!);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        foreach (var special in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            candidates.Add(Path.Combine(Environment.GetFolderPath(special), "Steam"));
        return candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Select(TryNormalize)
            .Where(p => p is not null && File.Exists(Path.Combine(p, "steam.exe"))).Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> GetLibraryRoots(string steamDirectory)
    {
        var libraries = new List<string> { Path.GetFullPath(steamDirectory) };
        foreach (var file in new[] { Path.Combine(steamDirectory, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamDirectory, "config", "libraryfolders.vdf") })
        {
            try
            {
                if (!File.Exists(file)) continue;
                var text = File.ReadAllText(file);
                foreach (Match match in LibraryPathRegex().Matches(text))
                    if (TryNormalize(UnescapeVdf(match.Groups[1].Value)) is { } path && Path.IsPathFullyQualified(path))
                        libraries.Add(path);
                // Older Steam clients stored numbered string values instead of nested "path" fields.
                foreach (Match match in LegacyLibraryRegex().Matches(text))
                    if (TryNormalize(UnescapeVdf(match.Groups[1].Value)) is { } path && Directory.Exists(path))
                        libraries.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string? FindGameDirectory(bool includeInterruptedInstallations = false)
    {
        foreach (var steam in FindSteamDirectories())
        foreach (var library in GetLibraryRoots(steam))
        {
            var installName = "Skyrim Special Edition";
            try
            {
                var manifest = Path.Combine(library, "steamapps", "appmanifest_489830.acf");
                if (File.Exists(manifest))
                {
                    var match = InstallDirectoryRegex().Match(File.ReadAllText(manifest));
                    if (match.Success)
                    {
                        var name = UnescapeVdf(match.Groups[1].Value);
                        if (name is not ("." or "..") && name.IndexOfAny(['/', '\\', ':']) < 0 &&
                            name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) installName = name;
                    }
                }
                var game = Path.Combine(library, "steamapps", "common", installName);
                if (File.Exists(Path.Combine(game, "SkyrimSE.exe")) ||
                    includeInterruptedInstallations && File.Exists(manifest) &&
                    Directory.Exists(game) && Directory.Exists(Path.Combine(game, "Data"))) return game;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static SteamClientSession GetSession()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Загрузка через Steam поддерживается в Windows.");
        return GetWindowsSession();
    }

    [SupportedOSPlatform("windows")]
    private static SteamClientSession GetWindowsSession()
    {
        uint activeUser;
        uint pid;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            activeUser = ToUInt32(key?.GetValue("ActiveUser"));
            pid = ToUInt32(key?.GetValue("pid"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw new InvalidOperationException("Не удалось прочитать сведения о текущей сессии Steam в реестре Windows. " +
                "Запустите Steam и патчер от одного пользователя Windows, затем повторите проверку. " + ex.Message, ex);
        }
        if (pid == 0 || pid > int.MaxValue) throw new InvalidOperationException("Запустите Steam и войдите в свой аккаунт в клиенте, затем повторите загрузку.");
        if (!WindowsProcessIdentity.TryGetExecutablePath((int)pid, out var executable, out var processError))
        {
            if (processError is 87 or 1168)
                throw new InvalidOperationException("Steam не запущен. Запустите клиент Steam и войдите в свой аккаунт.");
            var error = new System.ComponentModel.Win32Exception(processError);
            var guidance = processError == 5
                ? "Windows не разрешает проверить процесс Steam. Если Steam запущен от администратора, закройте его и запустите обычным способом. " +
                  "Steam и патчер должны работать от одного пользователя с одинаковыми правами Windows."
                : "Не удалось проверить процесс Steam. Перезапустите клиент Steam и повторите проверку.";
            throw new InvalidOperationException($"{guidance} Код Windows: {processError}. {error.Message}", error);
        }
        if (!Path.GetFileName(executable).Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Активный процесс Steam не найден. Перезапустите клиент Steam.");
        if (activeUser == 0) throw new InvalidOperationException("В Steam нет активного входа. Войдите в аккаунт в клиенте Steam и повторите загрузку.");
        var root = Path.GetDirectoryName(executable)!;
        DateTime? sessionStartedAt = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            sessionStartedAt = process.StartTime;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { /* A log cannot prove the current client's network state without its start time. */ }
        var online = sessionStartedAt is { } startedAt
            ? ReadOnlineState(Path.Combine(root, "logs", "connection_log.txt"), startedAt)
            : null;
        // Dispatch requires online state; observing an already running download must allow reconnects.
        return new(root, executable, (int)pid, activeUser, online,
            sessionStartedAt is { } processStart ? new DateTimeOffset(processStart).ToUniversalTime() : null);
    }

    /// <summary>Best-effort CM state from public logs. Session start and log timestamps use local wall time.</summary>
    public static bool? ReadOnlineState(string connectionLog, DateTime? sessionStartedAt = null)
    {
        try
        {
            if (!File.Exists(connectionLog)) return null;
            using var stream = new FileStream(connectionLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - 128 * 1024);
            var partialLine = false;
            if (start > 0)
            {
                stream.Position = start - 1;
                partialLine = stream.ReadByte() != '\n';
            }
            stream.Position = start;
            using var reader = new StreamReader(stream);
            if (partialLine) _ = reader.ReadLine();
            var minimumTime = sessionStartedAt is { } sessionStart
                ? new DateTime(sessionStart.Ticks - sessionStart.Ticks % TimeSpan.TicksPerSecond)
                : (DateTime?)null;
            bool? connected = null;
            while (reader.ReadLine() is { } line)
            {
                var timestamp = ConnectionTimestampRegex().Match(line);
                if (minimumTime is { } minimum && (!timestamp.Success ||
                    !DateTime.TryParseExact(timestamp.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var loggedAt) || loggedAt < minimum)) continue;
                if (timestamp.Success) line = line[timestamp.Length..];
                var entry = ConnectionStateRegex().Match(line);
                if (!entry.Success) continue;
                var state = entry.Groups["state"].Value;
                var message = entry.Groups["message"].Value;
                if (DisconnectEventRegex().IsMatch(message)) connected = false;
                else if (LogonResponseRegex().IsMatch(message) && SuccessfulLogonRegex().IsMatch(message)) connected = true;
                else if (state.Equals("Logged On", StringComparison.OrdinalIgnoreCase)) connected = true;
                else if (state.Equals("Logged Off", StringComparison.OrdinalIgnoreCase) ||
                         state.Equals("Logging Off", StringComparison.OrdinalIgnoreCase) ||
                         state.Equals("Offline", StringComparison.OrdinalIgnoreCase)) connected = false;
                else if (state.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    // Exact legacy events are supported; unrelated adapter diagnostics never set CM state.
                    if (message.Equals("Logged On", StringComparison.OrdinalIgnoreCase)) connected = true;
                    else if (message.Equals("Disconnected", StringComparison.OrdinalIgnoreCase) ||
                             message.Equals("Offline mode", StringComparison.OrdinalIgnoreCase) ||
                             message.Equals("Offline mode enabled", StringComparison.OrdinalIgnoreCase)) connected = false;
                }
                else connected = null; // A reconnect in progress is not evidence of offline mode.
            }
            return connected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static uint ToUInt32(object? value) => value switch
    {
        int signed => unchecked((uint)signed), uint unsigned => unsigned,
        long wide when wide is >= 0 and <= uint.MaxValue => (uint)wide, _ => 0
    };
    private static string UnescapeVdf(string value) => value.Replace(@"\\", @"\").Replace("\\\"", "\"");
    private static string? TryNormalize(string path)
    {
        try { return Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }
    [GeneratedRegex("\\\"path\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
    [GeneratedRegex("\\\"[0-9]+\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyLibraryRegex();
    [GeneratedRegex("\\\"installdir\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirectoryRegex();
    [GeneratedRegex("\\[\\s*OK\\s*\\]|'OK'", RegexOptions.IgnoreCase)]
    private static partial Regex SuccessfulLogonRegex();
    [GeneratedRegex(@"^\s*\[(?<time>[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2})\]\s*")]
    private static partial Regex ConnectionTimestampRegex();
    [GeneratedRegex(@"^\s*\[(?<state>Logged On|Logged Off|Logging Off|Logging On|Connected|Connecting|Disconnected|Offline|Connection)(?:,\s*[0-9]+,\s*[0-9]+)?\]\s*(?<message>.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStateRegex();
    [GeneratedRegex(@"^(?:\[U:[^\]\r\n]*\]\s*)?(?:ConnectionDisconnected|LogOff)\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DisconnectEventRegex();
    [GeneratedRegex(@"^(?:\[U:[^\]\r\n]*\]\s*)?(?:RecvMsgClient)?LogOnResponse\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogonResponseRegex();
}
