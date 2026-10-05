using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.Core.Transitions;

/// <summary>A data-only ZIP delta. All input files are checked before writing a new staging directory;
/// each copied/literal range and completed target are checked again. Installation remains PatchEngine's job.</summary>
public static class BinaryTransitionBundle
{
    internal const int MaximumMetadataLength = 16 * 1024 * 1024;
    internal const long MaximumTargetBytes = 4L * 1024 * 1024 * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, MaxDepth = 32 };

    public static Task<BinaryTransitionMetadata> InspectAsync(string bundlePath, string expectedBundleSha256,
        CancellationToken cancellationToken = default) => ReadMetadataAsync(bundlePath, expectedBundleSha256, cancellationToken);

    public static async Task<BinaryTransitionMetadata> ReadMetadataAsync(string bundlePath, string expectedBundleSha256,
        CancellationToken cancellationToken = default)
    {
        await using var input = await OpenVerifiedAsync(bundlePath, expectedBundleSha256, cancellationToken).ConfigureAwait(false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        return await ReadAndValidateAsync(archive, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<BinaryTransitionPreparation> PrepareAsync(string bundlePath,
        IReadOnlyDictionary<string, string> sourceFiles, string stagingDirectory, BinaryTransitionIdentity expectedIdentity,
        string expectedBundleSha256, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(expectedIdentity);
        var sources = NormalizeFiles(sourceFiles);
        var staging = Path.GetFullPath(stagingDirectory);
        DownloadPathSafety.EnsureNoLinks(staging);
        if (File.Exists(staging) || Directory.Exists(staging)) throw new IOException("Рабочая папка бинарного перехода уже существует.");
        await using var input = await OpenVerifiedAsync(bundlePath, expectedBundleSha256, cancellationToken).ConfigureAwait(false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var metadata = await ReadAndValidateAsync(archive, cancellationToken).ConfigureAwait(false);
        if (!SameIdentity(metadata.Identity, expectedIdentity)) throw new InvalidDataException("Пакет бинарного перехода предназначен для другой версии, локализации или режима.");
        var prefix = staging + Path.DirectorySeparatorChar;
        foreach (var file in metadata.Files)
        {
            DownloadPathSafety.ResolvePayloadPath(staging, file.RelativePath);
            if (file.SourceRelativePath is null) continue;
            if (!sources.TryGetValue(file.SourceRelativePath, out var source))
                throw new InvalidDataException("Для бинарного перехода отсутствует исходный файл: " + file.SourceRelativePath);
            if (source.Equals(staging, StringComparison.OrdinalIgnoreCase) || source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Рабочая папка пересекается с исходными файлами.");
            DownloadPathSafety.EnsureNoLinks(source);
            await using var sourceStream = OpenSource(source);
            if (sourceStream.Length != file.SourceLength || !Convert.ToHexString(await SHA256.HashDataAsync(sourceStream, cancellationToken).ConfigureAwait(false))
                .Equals(file.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Исходный файл не подходит для бинарного перехода: " + file.SourceRelativePath);
        }
        var total = metadata.Files.Aggregate(0L, (sum, file) => checked(sum + file.TargetLength));
        SteamChunkReuseService.EnsureFreeSpace(staging, total);
        Directory.CreateDirectory(staging);
        var inventory = new List<DownloadedFile>(metadata.Files.Count);
        long reused = 0, payload = 0;
        var buffer = new byte[128 * 1024];
        foreach (var file in metadata.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = DownloadPathSafety.ResolvePayloadPath(staging, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var targetHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using var source = file.SourceRelativePath is null ? null : OpenSource(sources[file.SourceRelativePath]);
                foreach (var segment in file.Segments)
                {
                    using var segmentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    Stream segmentInput;
                    if (segment.SourceOffset is { } offset)
                    {
                        source!.Position = offset; segmentInput = source;
                        reused = checked(reused + segment.Length);
                    }
                    else
                    {
                        segmentInput = archive.GetEntry(segment.PayloadEntry!)!.Open();
                        payload = checked(payload + segment.Length);
                    }
                    try
                    {
                        var remaining = segment.Length;
                        while (remaining != 0)
                        {
                            var read = await segmentInput.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                            if (read == 0) throw new EndOfStreamException("Бинарный переход содержит неполный диапазон.");
                            segmentHash.AppendData(buffer, 0, read); targetHash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            remaining -= read;
                        }
                        if (segment.SourceOffset is null && segmentInput.ReadByte() != -1)
                            throw new InvalidDataException("Размер данных бинарного перехода превышает описание.");
                        if (!Convert.ToHexString(segmentHash.GetHashAndReset()).Equals(segment.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Диапазон бинарного перехода повреждён: " + file.RelativePath);
                    }
                    finally { if (segment.SourceOffset is null) segmentInput.Dispose(); }
                }
                if (output.Length != file.TargetLength || !Convert.ToHexString(targetHash.GetHashAndReset()).Equals(file.TargetSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Результат бинарного перехода не соответствует целевому SHA256: " + file.RelativePath);
            }
            inventory.Add(new(file.RelativePath, file.TargetLength, file.TargetSha256));
            progress?.Report(new("Подготовлен бинарный переход: " + file.RelativePath, inventory.Count * 100d / metadata.Files.Count));
        }
        return new(metadata.Identity, inventory, reused, payload, total);
    }

    internal static Dictionary<string, string> NormalizeFiles(IReadOnlyDictionary<string, string> files)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, path) in files)
        {
            var normalized = NormalizeRelative(relative);
            var full = Path.GetFullPath(path);
            DownloadPathSafety.EnsureNoLinks(full);
            if (!result.TryAdd(normalized, full)) throw new InvalidDataException("Повторный путь бинарного перехода.");
        }
        return result;
    }

    internal static string NormalizeRelative(string relative)
    {
        DownloadPathSafety.ResolvePayloadPath(Path.GetTempPath(), relative);
        return relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    internal static void ValidateIdentity(BinaryTransitionIdentity identity)
    {
        if (identity is null || identity.SourceVersion is null || identity.TargetVersion is null || identity.SourceVersion.Length > 32 ||
            identity.TargetVersion.Length > 32 || !Version.TryParse(identity.SourceVersion, out _) || !Version.TryParse(identity.TargetVersion, out _) ||
            identity.Mode is not ("Complete" or "RuntimeOnly") || identity.Depots is not { Count: > 0 and <= 4 } ||
            identity.Depots.Any(depot => depot is null))
            throw new InvalidDataException("Некорректная версия или режим бинарного перехода.");
        try { SteamConsoleCommands.ForVersion(identity.Depots); }
        catch (ArgumentException error) { throw new InvalidDataException("Некорректный набор манифестов бинарного перехода.", error); }
        if (identity.Depots.Select(depot => depot.DepotId).Distinct().Count() != identity.Depots.Count ||
            identity.IncludeRussianLocalization != identity.Depots.Any(depot => depot.IsRussian) ||
            identity.Depots.Any(depot => depot.IsRussian != (depot.DepotId == 489838)) ||
            identity.Depots.Any(depot => depot.IsRussian) && !identity.Depots[^1].IsRussian)
            throw new InvalidDataException("Некорректная русская локализация бинарного перехода.");
    }

    private static bool SameIdentity(BinaryTransitionIdentity actual, BinaryTransitionIdentity expected) =>
        actual.SourceVersion == expected.SourceVersion && actual.TargetVersion == expected.TargetVersion &&
        actual.Mode == expected.Mode && actual.IncludeRussianLocalization == expected.IncludeRussianLocalization &&
        actual.Depots.SequenceEqual(expected.Depots);

    private static async Task<FileStream> OpenVerifiedAsync(string path, string expectedHash, CancellationToken token)
    {
        if (!SteamChunkReuseService.IsHash(expectedHash, 64)) throw new InvalidDataException("Нужен проверенный SHA256 пакета бинарного перехода.");
        DownloadPathSafety.EnsureNoLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            if (stream.Length > MaximumTargetBytes || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false))
                .Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Пакет бинарного перехода изменён или повреждён.");
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static FileStream OpenSource(string path)
    {
        DownloadPathSafety.EnsureNoLinks(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
    }

    private static async Task<BinaryTransitionMetadata> ReadAndValidateAsync(ZipArchive archive, CancellationToken token)
    {
        if (archive.Entries.Count > 1_000_001) throw new InvalidDataException("Слишком много записей в пакете перехода.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!entries.TryAdd(entry.FullName, entry) || entry.Length < 0 || entry.Length > MaximumTargetBytes ||
                entry.FullName != "metadata.json" && !IsPayloadName(entry.FullName))
                throw new InvalidDataException("Недопустимая или повторная запись ZIP бинарного перехода.");
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Ссылки в пакете бинарного перехода запрещены.");
        }
        if (!entries.TryGetValue("metadata.json", out var metadataEntry) || metadataEntry.Length > MaximumMetadataLength)
            throw new InvalidDataException("Нет корректного описания бинарного перехода.");
        await using var input = metadataEntry.Open();
        using var metadataBytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (metadataBytes.Length + read > MaximumMetadataLength) throw new InvalidDataException("Описание бинарного перехода слишком велико.");
            metadataBytes.Write(buffer, 0, read);
        }
        var metadata = JsonSerializer.Deserialize<BinaryTransitionMetadata>(metadataBytes.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("Описание бинарного перехода пусто.");
        if (metadata.SchemaVersion != 1 || metadata.Files is not { Count: > 0 and <= 100_000 })
            throw new InvalidDataException("Неизвестный формат бинарного перехода.");
        ValidateIdentity(metadata.Identity);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedPayloads = new HashSet<string>(StringComparer.Ordinal) { "metadata.json" };
        long total = 0;
        var segmentCount = 0;
        foreach (var file in metadata.Files)
        {
            if (file is null || !names.Add(NormalizeRelative(file.RelativePath)) || file.RelativePath != NormalizeRelative(file.RelativePath) ||
                file.TargetLength < 0 || !SteamChunkReuseService.IsHash(file.TargetSha256, 64) || file.Segments is null)
                throw new InvalidDataException("Некорректный файл бинарного перехода.");
            if (file.SourceRelativePath is null)
            {
                if (file.SourceLength is not null || file.SourceSha256 is not null) throw new InvalidDataException("Неполное описание источника.");
            }
            else if (file.SourceRelativePath != NormalizeRelative(file.SourceRelativePath) || file.SourceLength is null or < 0 ||
                !SteamChunkReuseService.IsHash(file.SourceSha256, 64)) throw new InvalidDataException("Некорректный источник бинарного перехода.");
            long length = 0;
            foreach (var segment in file.Segments)
            {
                if (++segmentCount > 1_000_000 || segment is null || segment.Length <= 0 || !SteamChunkReuseService.IsHash(segment.Sha256, 64))
                    throw new InvalidDataException("Некорректный диапазон бинарного перехода.");
                length = checked(length + segment.Length);
                if (length > file.TargetLength) throw new InvalidDataException("Диапазон выходит за пределы результата.");
                if (segment.SourceOffset is { } offset)
                {
                    if (segment.PayloadEntry is not null || file.SourceRelativePath is null || offset < 0 || offset > file.SourceLength ||
                        segment.Length > file.SourceLength - offset) throw new InvalidDataException("Диапазон выходит за пределы источника.");
                }
                else
                {
                    if (segment.PayloadEntry is null || !IsPayloadName(segment.PayloadEntry) ||
                        !entries.TryGetValue(segment.PayloadEntry, out var payload) || payload.Length != segment.Length)
                        throw new InvalidDataException("Отсутствуют данные диапазона бинарного перехода.");
                    usedPayloads.Add(segment.PayloadEntry);
                }
            }
            if (length != file.TargetLength) throw new InvalidDataException("Размер диапазонов не соответствует результату.");
            total = checked(total + file.TargetLength);
            if (total > MaximumTargetBytes) throw new InvalidDataException("Результат бинарного перехода слишком велик.");
        }
        if (!usedPayloads.SetEquals(entries.Keys)) throw new InvalidDataException("В пакете бинарного перехода есть лишние данные.");
        if (metadata.Identity.Mode == "RuntimeOnly")
        {
            var permitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SkyrimSE.exe", "SkyrimSELauncher.exe", NormalizeRelative("Data/Skyrim - Shaders.bsa")
            };
            if (!names.Contains("SkyrimSE.exe") || !names.IsSubsetOf(permitted) || metadata.Identity.IncludeRussianLocalization)
                throw new InvalidDataException("Режим RuntimeOnly изменяет только SkyrimSE.exe, SkyrimSELauncher.exe и Skyrim - Shaders.bsa; русская локализация сохраняется.");
        }
        return metadata;
    }

    private static bool IsPayloadName(string name) => name.Length == 72 && name.StartsWith("payload/", StringComparison.Ordinal) &&
        SteamChunkReuseService.IsHash(name[8..], 64);
}
