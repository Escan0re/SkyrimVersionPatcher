using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.Core.Transitions;

public sealed record TransitionPackageFileHash(string RelativePath, string SourceSha256, string TargetSha256);
public sealed record TransitionPackageIndexEntry(string FileName, string Sha256, BinaryTransitionIdentity Identity,
    IReadOnlyList<TransitionPackageFileHash> Files, string? SourceUrl = null);
public sealed record TransitionPackageIndex(int SchemaVersion, IReadOnlyList<TransitionPackageIndexEntry> Packages);
public sealed record TransitionPackageRecord(string Path, string Sha256, BinaryTransitionIdentity Identity,
    IReadOnlyList<TransitionPackageFileHash> Files, string? SourceUrl = null);

/// <summary>Reads the data-only transition index supplied with the application. It makes no network
/// requests and executes no package code. A caller passes each record's pinned SHA256 to PrepareAsync.</summary>
public sealed class TransitionPackageStore(string? directory = null)
{
    private readonly string root = Path.GetFullPath(directory ?? Path.Combine(AppContext.BaseDirectory, "data", "transitions"));
    private const int MaximumIndexLength = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public TransitionPackageRecord? Find(string sourceVersion, string targetVersion, string mode,
        bool includeRussianLocalization, IReadOnlyList<DepotManifest> depots)
    {
        var expected = new BinaryTransitionIdentity(sourceVersion, targetVersion, includeRussianLocalization, mode, depots);
        BinaryTransitionBundle.ValidateIdentity(expected);
        return Load().SingleOrDefault(record => record.Identity.SourceVersion == expected.SourceVersion &&
            record.Identity.TargetVersion == expected.TargetVersion && record.Identity.Mode == expected.Mode &&
            record.Identity.IncludeRussianLocalization == expected.IncludeRussianLocalization && record.Identity.Depots.SequenceEqual(expected.Depots));
    }

    /// <summary>Pins the whole source release subset even when a literal-only delta does not need
    /// to copy bytes from its executable. VersionInfo alone cannot satisfy this check.</summary>
    public static async Task ValidateSourceAsync(TransitionPackageRecord package, IReadOnlyDictionary<string, string> sourceFiles,
        CancellationToken cancellationToken = default)
    {
        var sources = BinaryTransitionBundle.NormalizeFiles(sourceFiles);
        foreach (var reference in package.Files)
        {
            var relative = BinaryTransitionBundle.NormalizeRelative(reference.RelativePath);
            if (!SteamChunkReuseService.IsHash(reference.SourceSha256, 64) || !sources.TryGetValue(relative, out var path))
                throw new InvalidDataException("Нет проверенного исходного файла бинарного перехода: " + relative);
            DownloadPathSafety.EnsureNoLinks(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))
                .Equals(reference.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Исходный файл изменён или не подходит для готового перехода: " + relative);
        }
    }

    public static void ValidatePrepared(TransitionPackageRecord package, IReadOnlyList<DownloadedFile> files)
    {
        var expected = package.Files.ToDictionary(file => BinaryTransitionBundle.NormalizeRelative(file.RelativePath), StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || !names.Add(BinaryTransitionBundle.NormalizeRelative(file.RelativePath)) || file.Length < 0 ||
                !expected.TryGetValue(BinaryTransitionBundle.NormalizeRelative(file.RelativePath), out var reference) ||
                !string.Equals(file.Sha256, reference.TargetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Результат готового перехода не соответствует встроенным хешам.");
        }
        if (!names.SetEquals(expected.Keys)) throw new InvalidDataException("В готовом переходе отсутствуют целевые файлы.");
    }

    public IReadOnlyList<TransitionPackageRecord> Load()
    {
        DownloadPathSafety.EnsureNoLinks(root);
        var indexPath = Path.Combine(root, "index.json");
        DownloadPathSafety.EnsureNoLinks(indexPath);
        if (!File.Exists(indexPath)) return [];
        using var input = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumIndexLength) throw new InvalidDataException("Индекс бинарных переходов слишком велик.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        try
        {
            RejectDuplicateProperties(bytes);
            var index = JsonSerializer.Deserialize<TransitionPackageIndex>(bytes, JsonOptions);
            if (index is null || index.SchemaVersion != 1 || index.Packages is not { Count: <= 256 })
                throw new InvalidDataException("Неизвестный или неполный индекс бинарных переходов.");
            var result = new List<TransitionPackageRecord>(index.Packages.Count);
            var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var package in index.Packages)
            {
                if (package is null || package.FileName is null || package.FileName.Length > 128 ||
                    package.FileName.Contains('/') || package.FileName.Contains('\\') || !package.FileName.EndsWith(".svpt", StringComparison.OrdinalIgnoreCase) ||
                    package.FileName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')) ||
                    IsDeviceName(package.FileName) || !filenames.Add(package.FileName) || !SteamChunkReuseService.IsHash(package.Sha256, 64) ||
                    package.Files is not { Count: > 0 and <= 100_000 }) throw new InvalidDataException("Некорректная запись бинарного перехода.");
                BinaryTransitionBundle.ValidateIdentity(package.Identity);
                var identityKey = package.Identity.SourceVersion + "\n" + package.Identity.TargetVersion + "\n" + package.Identity.Mode + "\n" +
                    package.Identity.IncludeRussianLocalization + "\n" + string.Join(';', package.Identity.Depots.Select(depot => $"{depot.DepotId}:{depot.ManifestId}:{depot.IsRussian}"));
                if (!identities.Add(identityKey)) throw new InvalidDataException("Повторная пара версий в индексе бинарных переходов.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in package.Files)
                {
                    if (file is null || !names.Add(BinaryTransitionBundle.NormalizeRelative(file.RelativePath)) ||
                        !SteamChunkReuseService.IsHash(file.SourceSha256, 64) || !SteamChunkReuseService.IsHash(file.TargetSha256, 64))
                        throw new InvalidDataException("Некорректные проверенные хеши бинарного перехода.");
                }
                if (package.Identity.Mode == "RuntimeOnly")
                {
                    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "SkyrimSE.exe", "SkyrimSELauncher.exe", BinaryTransitionBundle.NormalizeRelative("Data/Skyrim - Shaders.bsa") };
                    if (package.Identity.IncludeRussianLocalization || !names.Contains("SkyrimSE.exe") || !names.IsSubsetOf(allowed))
                        throw new InvalidDataException("Индекс RuntimeOnly содержит посторонние файлы.");
                }
                if (package.SourceUrl is not null && (!Uri.TryCreate(package.SourceUrl, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)) throw new InvalidDataException("Некорректная ссылка на источник бинарного перехода.");
                var path = DownloadPathSafety.ResolvePayloadPath(root, package.FileName);
                result.Add(new(path, package.Sha256, package.Identity, package.Files, package.SourceUrl));
            }
            return result.AsReadOnly();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or DecoderFallbackException or OverflowException)
        { throw new InvalidDataException("Индекс бинарных переходов повреждён: " + error.Message, error); }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
        var stack = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) stack.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) stack.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !stack.Peek().Add(reader.GetString()!))
                throw new InvalidDataException("Повторное поле в индексе бинарных переходов.");
        }
    }

    private static bool IsDeviceName(string fileName)
    {
        var name = fileName.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        return name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '0' and <= '9';
    }
}
