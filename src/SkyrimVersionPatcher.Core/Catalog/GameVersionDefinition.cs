namespace SkyrimVersionPatcher.Core.Catalog;

/// <summary>Id is the expected SkyrimSE.exe version, for example 1.6.1170.</summary>
public sealed record GameVersionDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<DepotManifest> Depots,
    string? Notes = null,
    string? SourceUrl = null)
{
    public override string ToString() => DisplayName;
}
