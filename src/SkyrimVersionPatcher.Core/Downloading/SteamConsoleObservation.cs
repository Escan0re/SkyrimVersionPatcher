using System.Globalization;
using System.Text.RegularExpressions;
using SkyrimVersionPatcher.Core.Catalog;

namespace SkyrimVersionPatcher.Core.Downloading;

public sealed record SteamDepotCompletion(string Directory, int? FileCount, string ManifestId);

/// <summary>Completion is tied to a specific app, depot and manifest, never to folder size or inactivity.</summary>
public sealed partial class SteamConsoleObservation(DepotManifest depot)
{
    private bool failureAcknowledged;
    public bool Acknowledged { get; private set; }
    public bool Failed { get; private set; }
    public SteamDepotCompletion? Completion { get; private set; }

    public void Accept(string line)
    {
        var completion = CompletionRegex().Match(line);
        if (completion.Success)
        {
            var normalized = completion.Groups["path"].Value.Replace('\\', '/').TrimEnd('/');
            var suffix = $"/app_{SteamConsoleCommands.AppId}/depot_{depot.DepotId}";
            if (!normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                !completion.Groups["manifest"].Value.Equals(depot.ManifestId, StringComparison.Ordinal)) return;
            int? count = null;
            if (completion.Groups["count"].Success)
            {
                if (!int.TryParse(completion.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    throw new InvalidDataException("Steam сообщил некорректное число файлов депо.");
                count = parsed;
            }
            Acknowledged = true;
            Completion = new(completion.Groups["path"].Value, count, depot.ManifestId);
            return;
        }
        var requestedCommand = SteamConsoleCommands.ForDepot(depot);
        var command = line.Trim();
        if (command.StartsWith("] ", StringComparison.Ordinal)) command = command[2..];
        if (command.StartsWith("download_depot ", StringComparison.Ordinal))
            failureAcknowledged = command.Equals(requestedCommand, StringComparison.Ordinal);
        var downloading = DownloadingDepotRegex().Match(line);
        if (downloading.Success)
            failureAcknowledged = downloading.Groups["depot"].Value == depot.DepotId.ToString(CultureInfo.InvariantCulture);
        if (failureAcknowledged) Acknowledged = true;
        var failure = line.Contains("Depot download failed", StringComparison.OrdinalIgnoreCase);
        var failedDepot = FailedDepotRegex().Match(line);
        if (failedDepot.Success && failedDepot.Groups["depot"].Value != depot.DepotId.ToString(CultureInfo.InvariantCulture))
            failure = false;
        var identity = $"App: {SteamConsoleCommands.AppId}, Depot: {depot.DepotId}, Manifest: {depot.ManifestId}";
        var index = line.IndexOf(identity, StringComparison.OrdinalIgnoreCase);
        var exactRequest = index >= 0 && (index + identity.Length == line.Length || !char.IsDigit(line[index + identity.Length]));
        if ((failureAcknowledged && failure) || exactRequest &&
            (line.Contains("Failed", StringComparison.OrdinalIgnoreCase) || line.Contains("Access Denied", StringComparison.OrdinalIgnoreCase)))
        {
            Failed = true;
            throw new IOException($"Steam не загрузил депо {depot.DepotId}, манифест {depot.ManifestId}. " +
                "Проверьте доступ к игре, сеть и место на диске. Сообщение Steam: " + line.Trim());
        }
    }

    [GeneratedRegex(@"\bDownloading depot\s+(?<depot>[0-9]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadingDepotRegex();

    [GeneratedRegex(@"\bFailed updating depot\s+(?<depot>[0-9]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FailedDepotRegex();

    [GeneratedRegex("Depot download complete\\s*[:|]\\s*\\\"(?<path>[^\\\"]+)\\\"\\s*\\((?:(?<count>[0-9]+)\\s+files?,?\\s*)?manifest\\s+(?<manifest>[0-9]+)\\)", RegexOptions.IgnoreCase)]
    private static partial Regex CompletionRegex();
}
