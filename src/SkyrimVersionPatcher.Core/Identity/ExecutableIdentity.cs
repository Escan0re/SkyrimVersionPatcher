namespace SkyrimVersionPatcher.Core.Identity;

public enum ExecutableIdentityStatus
{
    KnownOfficial,
    KnownVersionHashMismatch,
    Unknown
}

/// <summary>A reference established from a Steam depot manifest, rather than the installed file's version resource.</summary>
public sealed record VerifiedExecutableHash(string Version, string Sha256, long Length,
    uint DepotId, string ManifestId, string Sha1, string SourceUrl);

public sealed record ExecutableIdentity(string? Version, string? MetadataVersion, string Sha256,
    long Length, ExecutableIdentityStatus Status, VerifiedExecutableHash? Reference = null)
{
    public bool MetadataMatchesHash => Reference is not null && MetadataVersion == Reference.Version;
}
