using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Patching;

namespace SkyrimVersionPatcher.Core.Workflow;

public enum InstallationMode { Automatic, SteamDepots, RuntimeOnly, LocalDepots }

public sealed record AutomaticPatchRequest(string GameDirectory, string StorageDirectory,
    GameVersionDefinition Version, bool IncludeRussianLocalization = true,
    InstallationMode Mode = InstallationMode.Automatic, string? TransitionBundlePath = null,
    string? TransitionBundleSha256 = null, bool RenameContentCatalog = true, string? GameLanguage = null)
{
    public string SelectedGameLanguage => GameLocalizationCatalog.Get(GameLanguage ??
        (IncludeRussianLocalization ? GameLocalizationCatalog.Russian : GameLocalizationCatalog.English)).Id;
}

/// <summary>Verified payload categories. SteamRequestedBytes is requested depot payload, not measured network traffic;
/// WrittenBytes counts game-file writes and SkippedBytes unchanged installation targets.</summary>
public sealed record TransitionStatistics(long TargetBytes = 0, long ReusedInstalledBytes = 0,
    long ReusedCacheBytes = 0, long ImportedLocalBytes = 0, long SteamRequestedBytes = 0,
    long? SteamDownloadedBytes = null, long BinaryPayloadBytes = 0, long WrittenBytes = 0,
    long SkippedBytes = 0, InstallationMode Mode = InstallationMode.Automatic);

public sealed record AutomaticPatchProgress(string Stage, string Message, double? Percentage = null,
    string? RelativePath = null, TransitionStatistics? Statistics = null);

public sealed record AutomaticPatchResult(PatchResult Installation, PatchPlan Plan, bool FromCache,
    string? SourceVersion, string TargetVersion, string? ContentCatalogRenamedPath = null,
    TransitionStatistics? Statistics = null);
