using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.Core.Identity;

/// <summary>Recognizes exact EXE bytes. VersionInfo alone never establishes an official executable.</summary>
public sealed class ExecutableIdentityService
{
    public const long MaximumExecutableLength = 1024L * 1024 * 1024;
    private const int MaximumDatabaseCharacters = 2_000_000;
    private const long MaximumReceiptLength = 16L * 1024 * 1024;
    private readonly List<VerifiedExecutableHash> references;
    private readonly Func<string, string?> readMetadataVersion;
    private readonly object referenceLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public ExecutableIdentityService(IEnumerable<VerifiedExecutableHash>? verifiedHashes = null,
        Func<string, string?>? readMetadataVersion = null)
    {
        references = ValidateReferences(verifiedHashes ?? LoadBuiltIn()).ToList();
        this.readMetadataVersion = readMetadataVersion ?? ReadWindowsMetadataVersion;
    }

    public static IReadOnlyList<VerifiedExecutableHash> LoadBuiltIn()
    {
        using var stream = typeof(ExecutableIdentityService).Assembly
            .GetManifestResourceStream("SkyrimVersionPatcher.Core.executable-hashes.json")
            ?? throw new InvalidOperationException("Встроенные хеши SkyrimSE.exe не найдены.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return ParseVerifiedDatabase(reader.ReadToEnd());
    }

    /// <summary>Validates a reference database. Only call with a separately trusted embedded or authenticated source.</summary>
    public static IReadOnlyList<VerifiedExecutableHash> ParseVerifiedDatabase(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumDatabaseCharacters)
            throw new InvalidDataException("База хешей слишком большая.");
        try
        {
            using var parsed = JsonDocument.Parse(json, new() { MaxDepth = 16 });
            RejectDuplicateMembers(parsed.RootElement);
            var document = JsonSerializer.Deserialize<HashDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("Пустая база хешей.");
            if (document.SchemaVersion != 1 || document.AppId != SteamConsoleCommands.AppId || document.Executables is null)
                throw new InvalidDataException("База хешей не соответствует Skyrim Steam или поддерживаемой схеме.");
            return ValidateReferences(document.Executables);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Повреждённая база хешей SkyrimSE.exe.", error);
        }
    }

    public async Task<ExecutableIdentity> IdentifyAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        cancellationToken.ThrowIfCancellationRequested();
        DownloadPathSafety.EnsureNoLinks(executablePath);
        await using var stream = OpenExecutable(executablePath);
        var length = stream.Length;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = NormalizeVersion(readMetadataVersion(executablePath));
        lock (referenceLock)
        {
            var match = references.FirstOrDefault(reference => reference.Length == length &&
                reference.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return new(match.Version, metadata, hash, length, ExecutableIdentityStatus.KnownOfficial, match);
            var hasKnownReference = metadata is not null && references.Any(reference => reference.Version == metadata);
            return new(metadata, metadata, hash, length, hasKnownReference
                ? ExecutableIdentityStatus.KnownVersionHashMismatch : ExecutableIdentityStatus.Unknown);
        }
    }

    /// <summary>
    /// Learns a reference only from the requested depot cache with a matching receipt, a public
    /// unencrypted Steam manifest, its EXE SHA1 and matching VersionInfo. An installed game cannot
    /// become trusted merely by carrying the expected version resource. References stay in memory;
    /// callers can repeat this proof against the depot cache on the next run.
    /// </summary>
    public async Task<VerifiedExecutableHash?> LearnFromSteamCacheAsync(string cacheDirectory, string version,
        DepotManifest executableDepot, IEnumerable<string> steamDirectories, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(executableDepot);
        ArgumentNullException.ThrowIfNull(steamDirectories);
        cancellationToken.ThrowIfCancellationRequested();
        if (NormalizeVersion(version) != version || executableDepot.DepotId != 489833 || executableDepot.IsRussian)
            throw new InvalidDataException("Для проверки SkyrimSE.exe нужны версия и исполняемое депо 489833.");
        SteamConsoleCommands.ValidateDepot(executableDepot);
        var root = SteamClientDownloadService.GetDepotCacheDirectory(cacheDirectory, executableDepot);
        var receiptPath = Path.Combine(root, "complete.json");
        var executablePath = Path.Combine(root, "content", "SkyrimSE.exe");
        DownloadPathSafety.EnsureNoLinks(receiptPath);
        DownloadPathSafety.EnsureNoLinks(executablePath);
        if (!File.Exists(receiptPath) || !File.Exists(executablePath)) return null;
        if (new FileInfo(receiptPath).Length > MaximumReceiptLength)
            throw new InvalidDataException("Квитанция исполняемого депо слишком большая.");
        DepotDownloadReceipt receipt;
        try
        {
            await using var receiptStream = File.OpenRead(receiptPath);
            receipt = await JsonSerializer.DeserializeAsync<DepotDownloadReceipt>(receiptStream,
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Пустая квитанция исполняемого депо.");
        }
        catch (JsonException error) { throw new InvalidDataException("Повреждённая квитанция исполняемого депо.", error); }
        if (receipt.SchemaVersion != 1 || receipt.AppId != SteamConsoleCommands.AppId ||
            receipt.DepotId != executableDepot.DepotId || receipt.ManifestId != executableDepot.ManifestId || receipt.Files is null)
            throw new InvalidDataException("Квитанция не соответствует запрошенному исполняемому депо.");
        var recorded = receipt.Files.Where(file => file is not null &&
            string.Equals(file.RelativePath, "SkyrimSE.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (recorded.Length != 1 || !ValidHash(recorded[0].Sha256, 64))
            throw new InvalidDataException("В квитанции нет однозначного SHA256 SkyrimSE.exe.");

        SteamManifestFile? expected = null;
        foreach (var steamDirectory in steamDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(steamDirectory, "depotcache",
                $"{executableDepot.DepotId}_{executableDepot.ManifestId}.manifest");
            DownloadPathSafety.EnsureNoLinks(manifestPath);
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using var manifest = File.OpenRead(manifestPath);
                expected = SteamManifestReader.Read(manifest, executableDepot.DepotId,
                    ulong.Parse(executableDepot.ManifestId, CultureInfo.InvariantCulture))
                    .SingleOrDefault(file => file.RelativePath.Equals("SkyrimSE.exe", StringComparison.OrdinalIgnoreCase));
                if (expected is not null) break;
            }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException)
            {
                // Encrypted or malformed local manifests are not evidence of an official hash.
            }
        }
        if (expected is null) return null;
        await using var executable = OpenExecutable(executablePath);
        if (expected.Length != executable.Length || recorded[0].Length != executable.Length)
            throw new InvalidDataException("Размер SkyrimSE.exe не соответствует квитанции и манифесту Steam.");
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = await executable.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            sha1.AppendData(buffer, 0, count);
            sha256.AppendData(buffer, 0, count);
        }
        var hash1 = Convert.ToHexString(sha1.GetHashAndReset());
        var hash256 = Convert.ToHexString(sha256.GetHashAndReset());
        if (!expected.Sha1.Equals(hash1, StringComparison.OrdinalIgnoreCase) ||
            !recorded[0].Sha256.Equals(hash256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Хеш SkyrimSE.exe не соответствует квитанции и манифесту Steam.");
        if (NormalizeVersion(readMetadataVersion(executablePath)) != version)
            throw new InvalidDataException("Ресурс версии SkyrimSE.exe не соответствует проверяемой сборке.");
        var reference = new VerifiedExecutableHash(version, hash256, executable.Length, executableDepot.DepotId,
            executableDepot.ManifestId, hash1, $"https://steamdb.info/depot/{executableDepot.DepotId}/?manifest={executableDepot.ManifestId}");
        lock (referenceLock)
        {
            if (references.Any(existing => existing.Sha256.Equals(hash256, StringComparison.OrdinalIgnoreCase) && existing.Version != version))
                throw new InvalidDataException("Хеш SkyrimSE.exe уже относится к другой проверенной версии.");
            if (!references.Contains(reference)) references.Add(reference);
        }
        return reference;
    }

    private static FileStream OpenExecutable(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumExecutableLength)
        {
            stream.Dispose();
            throw new InvalidDataException("Недопустимый размер SkyrimSE.exe.");
        }
        return stream;
    }

    public static string? ReadWindowsMetadataVersion(string executablePath)
    {
        var info = FileVersionInfo.GetVersionInfo(executablePath);
        return ResolveMetadataVersion(info.FileVersion, info.ProductVersion,
            $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}");
    }

    /// <summary>Reads numeric version strings before fixed fields, which Skyrim can leave at 1.0.0.0.</summary>
    public static string? ResolveMetadataVersion(string? fileVersion, string? productVersion, string? fixedVersion) =>
        ParseMetadataVersion(fileVersion) ?? ParseMetadataVersion(productVersion) ?? ParseMetadataVersion(fixedVersion);

    private static string? ParseMetadataVersion(string? value)
    {
        if (value is null || value.Length > 64) return null;
        var canonical = NormalizeVersion(value.Trim());
        if (canonical is null) return null;
        var version = Version.Parse(canonical);
        return version.Revision <= 0 ? version.ToString(3) : version.ToString();
    }

    private static string? NormalizeVersion(string? version) => version is not null && version.Length <= 64 &&
        Version.TryParse(version, out var parsed) && parsed.Major > 0 && parsed.Build >= 0 && parsed.ToString() == version &&
        version.All(character => character is >= '0' and <= '9' or '.') ? version : null;

    private static bool ValidHash(string? hash, int length) => hash is not null && hash.Length == length &&
        hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static IReadOnlyList<VerifiedExecutableHash> ValidateReferences(IEnumerable<VerifiedExecutableHash> entries)
    {
        var result = new List<VerifiedExecutableHash>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (result.Count >= 2000 || entry is null || entry.Version is null || NormalizeVersion(entry.Version) != entry.Version ||
                !ValidHash(entry.Sha256, 64) || !ValidHash(entry.Sha1, 40) || entry.Length is <= 0 or > MaximumExecutableLength ||
                entry.DepotId != 489833 || !ulong.TryParse(entry.ManifestId, NumberStyles.None, CultureInfo.InvariantCulture, out var manifest) ||
                manifest == 0 || manifest.ToString(CultureInfo.InvariantCulture) != entry.ManifestId ||
                entry.SourceUrl is null || entry.SourceUrl.Length > 2048 ||
                !Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps ||
                source.UserInfo.Length != 0 || source.Fragment.Length != 0 || !keys.Add(entry.Sha256))
                throw new InvalidDataException("Некорректная или повторная проверенная запись хеша SkyrimSE.exe.");
            result.Add(entry with { Sha256 = entry.Sha256.ToUpperInvariant(), Sha1 = entry.Sha1.ToUpperInvariant() });
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Повторное поле базы хешей.");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateMembers(child);
    }

    private sealed record HashDocument(int SchemaVersion, uint AppId, IReadOnlyList<VerifiedExecutableHash> Executables);
}
