using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.Core.Transitions;

/// <summary>Complete prepares the listed target files. RuntimeOnly prepares a defined runtime subset;
/// it must never be described as making all game data match the target release.</summary>
public sealed record BinaryTransitionIdentity(string SourceVersion, string TargetVersion,
    bool IncludeRussianLocalization, string Mode, IReadOnlyList<DepotManifest> Depots);
public sealed record BinaryTransitionSegment(long Length, string Sha256, long? SourceOffset = null, string? PayloadEntry = null);
public sealed record BinaryTransitionFile(string RelativePath, long TargetLength, string TargetSha256,
    string? SourceRelativePath, long? SourceLength, string? SourceSha256, IReadOnlyList<BinaryTransitionSegment> Segments);
public sealed record BinaryTransitionMetadata(int SchemaVersion, BinaryTransitionIdentity Identity, IReadOnlyList<BinaryTransitionFile> Files);
public sealed record BinaryTransitionBuildResult(string Path, string Sha256, long ReusedBytes, long PayloadBytes, long TargetBytes);
public sealed record BinaryTransitionPreparation(BinaryTransitionIdentity Identity, IReadOnlyList<DownloadedFile> Files,
    long ReusedBytes, long PayloadBytes, long WrittenBytes);
