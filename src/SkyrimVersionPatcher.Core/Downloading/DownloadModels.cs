using SkyrimVersionPatcher.Core.Catalog;

namespace SkyrimVersionPatcher.Core.Downloading;

public sealed record DownloadRequest(string CacheDirectory, string VersionId, IReadOnlyList<DepotManifest> Depots,
    string? ExistingFilesDirectory = null, string? InstalledGameDirectory = null, bool ReuseInstalledFiles = true,
    bool LocalFilesOnly = false, bool ForceFreshDownload = false);

/// <summary>Byte categories count verified target depot payload; SteamRequestedBytes is not measured network traffic.</summary>
public sealed record DownloadStatistics(long TargetBytes = 0, long ReusedInstalledBytes = 0,
    long ReusedCacheBytes = 0, long ImportedLocalBytes = 0, long SteamRequestedBytes = 0,
    long? SteamDownloadedBytes = null);

public sealed record DownloadProgress(string Message, double? Percentage = null, DownloadStatistics? Statistics = null);

public sealed record DownloadedFile(string RelativePath, long Length, string Sha256);

public sealed record DownloadResult(IReadOnlyList<string> SourceDirectories,
    IReadOnlyList<DepotManifest> Depots, IReadOnlyList<DownloadedFile> Files, bool FromCache = false,
    DownloadStatistics? Statistics = null, string? FreshSteamDirectory = null);

public sealed record DepotDownloadReceipt(int SchemaVersion, uint AppId, uint DepotId, string ManifestId,
    DateTimeOffset CompletedUtc, IReadOnlyList<DownloadedFile> Files);

/// <summary>Only public session state is inspected. No stored login material is read.</summary>
public sealed record SteamClientSession(string SteamDirectory, string ExecutablePath, int ProcessId,
    uint ActiveUser, bool? Online = null, DateTimeOffset? ProcessStartedUtc = null);

/// <summary>The desktop implementation targets the observed Steam console through Windows accessibility.</summary>
public interface ISteamClientBridge
{
    SteamClientSession GetSession();
    /// <summary>Restores console readiness before the caller captures fresh command/output baselines.</summary>
    Task PrepareConsoleAsync(SteamClientSession session, CancellationToken cancellationToken) => Task.CompletedTask;
    Task DispatchDepotAsync(SteamClientSession session, DepotManifest depot, CancellationToken cancellationToken);
    Task<string?> ReadConsoleAsync(SteamClientSession session, CancellationToken cancellationToken);
}

public sealed class SteamConsoleUnavailableException(string message, string? command = null) : IOException(message)
{
    public string? Command { get; } = command;
}
