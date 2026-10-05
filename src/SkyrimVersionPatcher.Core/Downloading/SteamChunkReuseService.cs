using System.Security.Cryptography;

namespace SkyrimVersionPatcher.Core.Downloading;

public sealed record SteamChunkSource(string Directory, IReadOnlyList<SteamChunkFile> Files);
public sealed record SteamReuseChunkPlan(long TargetOffset, long Length, string Sha1, string? SourcePath, long SourceOffset);
public sealed record SteamReuseFilePlan(SteamChunkFile Target, IReadOnlyList<SteamReuseChunkPlan> Chunks);
public sealed record SteamReusePlan(IReadOnlyList<SteamReuseFilePlan> Files, long ReusedBytes, long MissingBytes, long TotalBytes)
{
    public bool IsComplete => MissingBytes == 0;
}

/// <summary>Reconstructs an entire verified depot in a separate working directory. It never modifies
/// the installed game and never pretends download_depot can fetch individual missing chunks.</summary>
public static class SteamChunkReuseService
{
    public static async Task<SteamReusePlan> AnalyzeAsync(IReadOnlyList<SteamChunkFile> targets,
        IReadOnlyList<SteamChunkSource> sources, CancellationToken cancellationToken = default)
    {
        var required = new HashSet<(string Hash, long Length)>();
        var plans = new List<SteamReuseFilePlan>(targets.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            ValidateFile(target);
            if (!names.Add(target.RelativePath)) throw new InvalidDataException("Повторный целевой файл блока.");
            foreach (var chunk in Ranges(target)) required.Add((chunk.Sha1.ToUpperInvariant(), chunk.Length));
        }
        var verified = new Dictionary<(string Hash, long Length), (string Path, long Offset)>();
        var visited = new HashSet<(string Path, long Offset, long Length, string Hash)>();
        foreach (var source in sources)
        {
            DownloadPathSafety.EnsureNoLinks(source.Directory);
            foreach (var file in source.Files)
            {
                ValidateFile(file);
                var path = DownloadPathSafety.ResolvePayloadPath(source.Directory, file.RelativePath);
                if (!File.Exists(path)) continue;
                foreach (var chunk in Ranges(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = (chunk.Sha1.ToUpperInvariant(), chunk.Length);
                    if (!required.Contains(key) || verified.ContainsKey(key) ||
                        !visited.Add((path.ToUpperInvariant(), chunk.Offset, chunk.Length, key.Item1))) continue;
                    try
                    {
                        if (await VerifyRangeAsync(path, chunk.Offset, chunk.Length, chunk.Sha1, cancellationToken).ConfigureAwait(false))
                            verified.Add(key, (path, chunk.Offset));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        // An unreadable or changed source is not reusable; missing data stays explicit.
                    }
                }
            }
        }
        long reused = 0, missing = 0;
        foreach (var target in targets)
        {
            var chunks = new List<SteamReuseChunkPlan>();
            foreach (var chunk in Ranges(target))
            {
                if (verified.TryGetValue((chunk.Sha1.ToUpperInvariant(), chunk.Length), out var source))
                {
                    reused = checked(reused + chunk.Length);
                    chunks.Add(new(chunk.Offset, chunk.Length, chunk.Sha1, source.Path, source.Offset));
                }
                else
                {
                    missing = checked(missing + chunk.Length);
                    chunks.Add(new(chunk.Offset, chunk.Length, chunk.Sha1, null, 0));
                }
            }
            plans.Add(new(target, chunks));
        }
        return new(plans, reused, missing, checked(reused + missing));
    }

    public static async Task<IReadOnlyList<DownloadedFile>> PrepareAsync(SteamReusePlan plan, string stagingDirectory,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!plan.IsComplete) throw new InvalidOperationException("Не все целевые блоки доступны локально; требуется загрузка Steam.");
        var expectedTotal = plan.Files.Aggregate(0L, (sum, file) => checked(sum + file.Target.Length));
        if (plan.TotalBytes != expectedTotal || plan.ReusedBytes != expectedTotal || plan.MissingBytes != 0)
            throw new InvalidDataException("Размер плана не соответствует целевым файлам блоков.");
        var staging = Path.GetFullPath(stagingDirectory);
        DownloadPathSafety.EnsureNoLinks(staging);
        if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException("Рабочая папка уже существует.");
        var stagingPrefix = staging + Path.DirectorySeparatorChar;
        var validatedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Files)
        {
            ValidateFile(file.Target);
            if (!validatedNames.Add(file.Target.RelativePath)) throw new InvalidDataException("Повторный целевой файл блока.");
            DownloadPathSafety.ResolvePayloadPath(staging, file.Target.RelativePath);
            var expected = Ranges(file.Target).ToArray();
            if (expected.Length != file.Chunks.Count) throw new InvalidDataException("План не соответствует манифесту блоков.");
            for (var index = 0; index < expected.Length; index++)
            {
                var chunk = file.Chunks[index];
                if (chunk.TargetOffset != expected[index].Offset || chunk.Length != expected[index].Length ||
                    !string.Equals(chunk.Sha1, expected[index].Sha1, StringComparison.OrdinalIgnoreCase) ||
                    chunk.SourcePath is null || chunk.SourceOffset < 0)
                    throw new InvalidDataException("План не соответствует манифесту блоков.");
                var source = Path.GetFullPath(chunk.SourcePath);
                if (source.Equals(staging, StringComparison.OrdinalIgnoreCase) || source.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Рабочая папка пересекается с источником блоков.");
                DownloadPathSafety.EnsureNoLinks(source);
            }
        }
        EnsureFreeSpace(staging, plan.TotalBytes);
        Directory.CreateDirectory(staging);
        var files = new List<DownloadedFile>(plan.Files.Count);
        var buffer = new byte[128 * 1024];
        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = DownloadPathSafety.ResolvePayloadPath(staging, file.Target.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (var chunk in file.Chunks)
                {
                    DownloadPathSafety.EnsureNoLinks(chunk.SourcePath!);
                    await using var input = new FileStream(chunk.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length,
                        FileOptions.Asynchronous | FileOptions.RandomAccess);
                    if (input.Length < checked(chunk.SourceOffset + chunk.Length)) throw new InvalidDataException("Источник блока изменился после проверки.");
                    input.Position = chunk.SourceOffset;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    var remaining = chunk.Length;
                    while (remaining != 0)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException("Источник блока оборван.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        remaining -= read;
                    }
                    if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(chunk.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Источник блока изменился после проверки: " + file.Target.RelativePath);
                }
            }
            await using var result = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (result.Length != file.Target.Length || !Convert.ToHexString(await SHA1.HashDataAsync(result, cancellationToken).ConfigureAwait(false))
                .Equals(file.Target.Sha1, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Собранный файл не соответствует целевому манифесту.");
            result.Position = 0;
            files.Add(new(file.Target.RelativePath, file.Target.Length,
                Convert.ToHexString(await SHA256.HashDataAsync(result, cancellationToken).ConfigureAwait(false))));
            progress?.Report(new("Собраны проверенные локальные блоки: " + file.Target.RelativePath, files.Count * 100d / plan.Files.Count));
        }
        return files;
    }

    private static IEnumerable<(long Offset, long Length, string Sha1)> Ranges(SteamChunkFile file)
    {
        if (file.Chunks.Count == 0)
        {
            if (file.Length != 0) yield return (0, file.Length, file.Sha1);
            yield break;
        }
        foreach (var chunk in file.Chunks) yield return (chunk.Offset, chunk.Length, chunk.Sha1);
    }

    private static void ValidateFile(SteamChunkFile file)
    {
        DownloadPathSafety.ResolvePayloadPath(Path.GetTempPath(), file.RelativePath);
        if (file.Length < 0 || !IsHash(file.Sha1, 40) || file.Chunks is null) throw new InvalidDataException("Некорректное описание файла блоков.");
        long end = 0;
        foreach (var chunk in file.Chunks)
        {
            if (chunk.Offset != end || chunk.Length is <= 0 or > SteamManifestChunkReader.MaximumChunkLength || !IsHash(chunk.Sha1, 40))
                throw new InvalidDataException("Некорректное описание блока.");
            end = checked(end + chunk.Length);
        }
        if (file.Chunks.Count != 0 && end != file.Length) throw new InvalidDataException("Размер блоков не соответствует файлу.");
    }

    internal static bool IsHash(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);

    internal static void EnsureFreeSpace(string destination, long required)
    {
        var available = new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
        if (available < required) throw new IOException($"Для подготовки файлов нужно {required:N0} байт, доступно {available:N0}.");
    }

    private static async Task<bool> VerifyRangeAsync(string path, long offset, long length, string sha1, CancellationToken token)
    {
        DownloadPathSafety.EnsureNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (input.Length < checked(offset + length)) return false;
        input.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[128 * 1024];
        while (length != 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token).ConfigureAwait(false);
            if (read == 0) return false;
            hash.AppendData(buffer, 0, read); length -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }
}
