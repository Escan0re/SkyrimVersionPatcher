namespace SkyrimVersionPatcher.Core.Catalog;

/// <summary>A Steam depot pinned to an exact manifest. Keep manifest IDs as strings in JSON.</summary>
public sealed record DepotManifest(uint DepotId, string ManifestId, bool IsRussian);
