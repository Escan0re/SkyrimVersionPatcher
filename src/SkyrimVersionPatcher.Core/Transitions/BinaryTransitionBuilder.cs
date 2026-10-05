using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.Core.Transitions;

/// <summary>Builds reusable data-only transition packages from already verified local releases.
/// Virtual path maps allow depot overlays without copying entire game versions to merged directories.</summary>
public static class BinaryTransitionBuilder
{
    private const int BlockLength = 64 * 1024;

    public static async Task<BinaryTransitionBuildResult> BuildAsync(string bundlePath, BinaryTransitionIdentity identity,
        IReadOnlyDictionary<string, string> sourceFiles, IReadOnlyDictionary<string, string> targetFiles,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        BinaryTransitionBundle.ValidateIdentity(identity);
        var sources = BinaryTransitionBundle.NormalizeFiles(sourceFiles);
        var targets = BinaryTransitionBundle.NormalizeFiles(targetFiles);
        if (targets.Count is 0 or > 100_000) throw new InvalidDataException("Нужны проверенные целевые файлы бинарного перехода.");
        var bundle = Path.GetFullPath(bundlePath);
        DownloadPathSafety.EnsureNoLinks(bundle);
        if (File.Exists(bundle) || Directory.Exists(bundle)) throw new IOException("Пакет бинарного перехода уже существует.");
        if (sources.Values.Concat(targets.Values).Any(path => path.Equals(bundle, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Пакет пересекается с исходными файлами.");
        var targetLengths = targets.ToDictionary(pair => pair.Key, pair => new FileInfo(pair.Value).Length, StringComparer.OrdinalIgnoreCase);
        var total = targetLengths.Values.Aggregate(0L, (sum, length) => checked(sum + length));
        if (total > BinaryTransitionBundle.MaximumTargetBytes) throw new InvalidDataException("Результат бинарного перехода слишком велик.");
        // Worst case: none of the data can be reused. Reserve that space before creating a package.
        SteamChunkReuseService.EnsureFreeSpace(bundle, checked(total + BinaryTransitionBundle.MaximumMetadataLength));
        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
        var files = new List<BinaryTransitionFile>(targets.Count);
        var payloadNames = new HashSet<string>(StringComparer.Ordinal);
        long reused = 0, payloadBytes = 0;
        var targetBuffer = new byte[BlockLength];
        await using (var output = new FileStream(bundle, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            128 * 1024, FileOptions.Asynchronous))
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var (relative, targetPath) in targets.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DownloadPathSafety.EnsureNoLinks(targetPath);
                await using var target = Open(targetPath);
                if (target.Length != targetLengths[relative]) throw new InvalidDataException("Целевой файл изменился до создания бинарного перехода: " + relative);
                await using var source = sources.TryGetValue(relative, out var sourcePath) ? Open(sourcePath) : null;
                var sourceIndex = source is null ? null : await IndexSourceAsync(source, cancellationToken).ConfigureAwait(false);
                var segments = new List<BinaryTransitionSegment>();
                using var targetHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var copyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long? copyOffset = null;
                long copyLength = 0;
                long targetOffset = 0;
                void FlushCopy()
                {
                    if (copyOffset is null) return;
                    segments.Add(new(copyLength, Convert.ToHexString(copyHash.GetHashAndReset()), copyOffset));
                    copyOffset = null; copyLength = 0;
                }
                while (true)
                {
                    var count = await ReadBlockAsync(target, targetBuffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    targetHash.AppendData(targetBuffer, 0, count);
                    var hash = Convert.ToHexString(SHA256.HashData(targetBuffer.AsSpan(0, count)));
                    long? reusableOffset = null;
                    if (sourceIndex is not null)
                    {
                        if (sourceIndex.ByOffset.TryGetValue(targetOffset, out var atOffset) && atOffset == (hash, count)) reusableOffset = targetOffset;
                        else if (sourceIndex.ByHash.TryGetValue((hash, count), out var located)) reusableOffset = located;
                    }
                    if (reusableOffset is { } found)
                    {
                        if (copyOffset is not null && found != copyOffset + copyLength) FlushCopy();
                        copyOffset ??= found;
                        copyLength = checked(copyLength + count); copyHash.AppendData(targetBuffer, 0, count);
                        reused = checked(reused + count);
                    }
                    else
                    {
                        FlushCopy();
                        var entryName = "payload/" + hash;
                        if (payloadNames.Add(entryName))
                        {
                            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                            await using var entryOutput = entry.Open();
                            await entryOutput.WriteAsync(targetBuffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        }
                        segments.Add(new(count, hash, PayloadEntry: entryName));
                        payloadBytes = checked(payloadBytes + count);
                    }
                    targetOffset = checked(targetOffset + count);
                }
                FlushCopy();
                var result = new BinaryTransitionFile(relative, targetOffset, Convert.ToHexString(targetHash.GetHashAndReset()),
                    source is not null ? relative : null, source?.Length, sourceIndex?.Sha256, segments);
                files.Add(result);
                progress?.Report(new("Создана бинарная разница: " + relative, files.Count * 100d / targets.Count));
            }
            var metadata = new BinaryTransitionMetadata(1, identity, files);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(metadata, BinaryTransitionBundle.JsonOptions);
            if (serialized.Length > BinaryTransitionBundle.MaximumMetadataLength) throw new InvalidDataException("Описание бинарного перехода слишком велико.");
            var metadataEntry = archive.CreateEntry("metadata.json", CompressionLevel.Optimal);
            await using var metadataOutput = metadataEntry.Open();
            await metadataOutput.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
        }
        await using var built = Open(bundle);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(built, cancellationToken).ConfigureAwait(false));
        // Apply-time validation also checks the package's untrusted metadata. Exercise that validation before publishing a build result.
        await BinaryTransitionBundle.ReadMetadataAsync(bundle, sha256, cancellationToken).ConfigureAwait(false);
        return new(bundle, sha256, reused, payloadBytes, total);
    }

    private static FileStream Open(string path)
    {
        DownloadPathSafety.EnsureNoLinks(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private sealed record SourceIndex(string Sha256, Dictionary<long, (string Hash, int Length)> ByOffset,
        Dictionary<(string Hash, int Length), long> ByHash);

    private static async Task<SourceIndex> IndexSourceAsync(FileStream input, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var byOffset = new Dictionary<long, (string Hash, int Length)>();
        var byHash = new Dictionary<(string Hash, int Length), long>();
        var buffer = new byte[BlockLength];
        long offset = 0;
        while (true)
        {
            var count = await ReadBlockAsync(input, buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (byOffset.Count >= 2_000_000) throw new InvalidDataException("Исходный файл слишком велик для индекса бинарного перехода.");
            hash.AppendData(buffer, 0, count);
            var key = (Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count))), count);
            byOffset.Add(offset, key); byHash.TryAdd(key, offset);
            offset = checked(offset + count);
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), byOffset, byHash);
    }

    private static async Task<int> ReadBlockAsync(Stream input, byte[] buffer, CancellationToken token)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        return count;
    }
}
