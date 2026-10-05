using System.Text;
using static SkyrimVersionPatcher.Core.Downloading.SteamManifestReader;

namespace SkyrimVersionPatcher.Core.Downloading;

public sealed record SteamManifestChunk(long Offset, int Length, string Sha1, int CompressedLength = 0);
public sealed record SteamChunkFile(string RelativePath, long Length, string Sha1, IReadOnlyList<SteamManifestChunk> Chunks);

/// <summary>Reads public, decrypted manifests; no depot keys or saved Steam credentials are read.
/// Field numbers follow SteamRE/SteamKit's ContentManifestPayload.FileMapping.ChunkData.</summary>
public static class SteamManifestChunkReader
{
    public const int MaximumChunkLength = 16 * 1024 * 1024;

    public static IReadOnlyList<SteamChunkFile> Read(Stream stream, uint expectedDepot, ulong expectedManifest)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[128 * 1024];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (copy.Length + count > 128 * 1024 * 1024) throw new InvalidDataException("Манифест превышает допустимый размер.");
            copy.Write(buffer, 0, count);
        }
        copy.Position = 0;
        var files = SteamManifestReader.Read(copy, expectedDepot, expectedManifest)
            .ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        copy.Position = 0;
        using var reader = new BinaryReader(copy, Encoding.UTF8, leaveOpen: true);
        byte[]? payload = null;
        while (reader.ReadUInt32() is var magic && magic != 0x32C415AB)
        {
            var length = reader.ReadInt32();
            if (magic == 0x71F617D0) payload = reader.ReadBytes(length);
            else copy.Position += length;
        }
        var cursor = new ProtoCursor(payload!);
        var result = new List<SteamChunkFile>(files.Count);
        while (cursor.ReadTag(out var field, out var wire))
        {
            if (field != 1 || wire != 2) { cursor.Skip(wire); continue; }
            var entry = new ProtoCursor(cursor.ReadBytes());
            string? name = null;
            var chunks = new List<SteamManifestChunk>();
            while (entry.ReadTag(out field, out wire))
            {
                if (field == 1 && wire == 2) name = new UTF8Encoding(false, true).GetString(entry.ReadBytes())
                    .Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                else if (field == 6 && wire == 2)
                {
                    if (chunks.Count >= 1_000_000) throw new InvalidDataException("Слишком много блоков в манифесте.");
                    chunks.Add(ReadChunk(entry.ReadBytes()));
                }
                else entry.Skip(wire);
            }
            if (name is null || !files.TryGetValue(name, out var file)) continue; // Directory entries were checked by the file reader.
            var ordered = chunks.OrderBy(chunk => chunk.Offset).ToArray();
            if (ordered.Length != 0)
            {
                long end = 0;
                foreach (var chunk in ordered)
                {
                    if (chunk.Offset != end) throw new InvalidDataException("Блоки файла пересекаются или содержат пропуски: " + name);
                    end = checked(end + chunk.Length);
                }
                if (end != file.Length) throw new InvalidDataException("Размер блоков не соответствует файлу: " + name);
            }
            result.Add(new(file.RelativePath, file.Length, file.Sha1, ordered));
        }
        if (result.Count != files.Count) throw new InvalidDataException("Неполный список файлов блокового манифеста.");
        return result;
    }

    private static SteamManifestChunk ReadChunk(ReadOnlySpan<byte> data)
    {
        var cursor = new ProtoCursor(data);
        byte[]? sha = null;
        ulong offset = 0, length = 0, compressed = 0;
        var seen = new HashSet<int>();
        while (cursor.ReadTag(out var field, out var wire))
        {
            if (field is 1 or 3 or 4 or 5 && !seen.Add(field)) throw new InvalidDataException("Повторное поле блока Steam.");
            if (field == 1 && wire == 2) sha = cursor.ReadBytes().ToArray();
            else if (field == 3 && wire == 0) offset = cursor.ReadVarint();
            else if (field == 4 && wire == 0) length = cursor.ReadVarint();
            else if (field == 5 && wire == 0) compressed = cursor.ReadVarint();
            else cursor.Skip(wire);
        }
        if (sha is not { Length: 20 } || offset > long.MaxValue || length is 0 or > MaximumChunkLength || compressed > int.MaxValue)
            throw new InvalidDataException("Некорректный размер или SHA1 блока Steam.");
        return new((long)offset, (int)length, Convert.ToHexString(sha), (int)compressed);
    }
}
