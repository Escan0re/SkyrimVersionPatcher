using System.Text;

namespace SkyrimVersionPatcher.Core.Downloading;

public sealed record SteamManifestFile(string RelativePath, long Length, string Sha1);

/// <summary>
/// Reads decrypted Steam depot manifests when available. Only the protobuf fields needed for
/// file integrity are decoded; malformed, encrypted, linked or unknown-format manifests fail closed.
/// Format reference: SteamRE/SteamKit, Types/DepotManifest.cs and Base/Generated/ContentManifest.cs.
/// </summary>
public static class SteamManifestReader
{
    public static IReadOnlyList<SteamManifestFile> Read(Stream stream, uint expectedDepot, ulong expectedManifest)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        byte[]? payload = null;
        byte[]? metadata = null;
        var hasSignature = false;
        while (true)
        {
            var magic = reader.ReadUInt32();
            if (magic == 0x32C415AB) break;
            var length = reader.ReadUInt32();
            if (length > 64 * 1024 * 1024) throw new InvalidDataException("Манифест превышает допустимый размер.");
            var data = reader.ReadBytes((int)length);
            if (data.Length != length) throw new EndOfStreamException("Манифест оборван.");
            switch (magic)
            {
                case 0x71F617D0 when payload is null: payload = data; break;
                case 0x1F4812BE when metadata is null: metadata = data; break;
                case 0x1B81B817 when !hasSignature: hasSignature = true; break;
                default: throw new InvalidDataException("Неизвестная или повторная секция манифеста.");
            }
        }
        if (payload is null || metadata is null || !hasSignature || stream.ReadByte() != -1)
            throw new InvalidDataException("Неполный или повреждённый манифест.");

        ValidateMetadata(metadata, expectedDepot, expectedManifest);
        var result = new List<SteamManifestFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = new ProtoCursor(payload);
        while (cursor.ReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == 2)
            {
                var file = ReadFile(cursor.ReadBytes());
                if (file is not null)
                {
                    if (!names.Add(file.RelativePath)) throw new InvalidDataException("Повторный путь в манифесте: " + file.RelativePath);
                    result.Add(file);
                }
            }
            else cursor.Skip(wire);
        }
        return result;
    }

    private static void ValidateMetadata(ReadOnlySpan<byte> data, uint expectedDepot, ulong expectedManifest)
    {
        var cursor = new ProtoCursor(data);
        ulong depot = 0, manifest = 0;
        var encrypted = false;
        while (cursor.ReadTag(out var field, out var wire))
        {
            if (wire == 0 && field == 1) depot = cursor.ReadVarint();
            else if (wire == 0 && field == 2) manifest = cursor.ReadVarint();
            else if (wire == 0 && field == 4) encrypted = cursor.ReadVarint() != 0;
            else cursor.Skip(wire);
        }
        if (encrypted || depot != expectedDepot || manifest != expectedManifest)
            throw new InvalidDataException("Идентификатор или формат манифеста не соответствует запросу.");
    }

    private static SteamManifestFile? ReadFile(ReadOnlySpan<byte> data)
    {
        var cursor = new ProtoCursor(data);
        string? name = null;
        ulong length = 0, flags = 0;
        byte[]? hash = null;
        var hasLink = false;
        while (cursor.ReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == 2) name = new UTF8Encoding(false, true).GetString(cursor.ReadBytes());
            else if (field == 2 && wire == 0) length = cursor.ReadVarint();
            else if (field == 3 && wire == 0) flags = cursor.ReadVarint();
            else if (field == 5 && wire == 2) hash = cursor.ReadBytes().ToArray();
            else if (field == 7 && wire == 2) hasLink = !cursor.ReadBytes().IsEmpty;
            else cursor.Skip(wire);
        }
        if (hasLink || (flags & 512) != 0) throw new InvalidDataException("Ссылки в манифесте не поддерживаются.");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Пустой путь в манифесте.");
        var path = name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var segments = path.Split(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(path) || path.Contains(':') || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Небезопасный путь в манифесте.");
        if ((flags & 64) != 0) return null;
        if (length > long.MaxValue || hash is not { Length: 20 })
            throw new InvalidDataException("Недопустимый размер или SHA1 файла в манифесте.");
        return new(path, (long)length, Convert.ToHexString(hash));
    }

    internal ref struct ProtoCursor(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> remaining = data;
        internal bool ReadTag(out int field, out int wire)
        {
            field = wire = 0;
            if (remaining.IsEmpty) return false;
            var tag = ReadVarint();
            if (tag == 0 || tag > uint.MaxValue) throw new InvalidDataException("Неверное поле protobuf.");
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            if (field == 0) throw new InvalidDataException("Неверный номер поля protobuf.");
            return true;
        }

        internal ulong ReadVarint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (remaining.IsEmpty) throw new EndOfStreamException("Неполное поле protobuf.");
                var next = remaining[0];
                remaining = remaining[1..];
                if (shift == 63 && next > 1) throw new InvalidDataException("Переполнение protobuf.");
                value |= (ulong)(next & 0x7f) << shift;
                if ((next & 0x80) == 0) return value;
            }
            throw new InvalidDataException("Некорректный varint.");
        }

        internal ReadOnlySpan<byte> ReadBytes()
        {
            var count = ReadVarint();
            if (count > (ulong)remaining.Length) throw new EndOfStreamException("Неполное поле protobuf.");
            var value = remaining[..(int)count];
            remaining = remaining[(int)count..];
            return value;
        }

        internal void Skip(int wire)
        {
            switch (wire)
            {
                case 0: ReadVarint(); return;
                case 2: ReadBytes(); return;
                case 1: SkipFixed(8); return;
                case 5: SkipFixed(4); return;
                default: throw new InvalidDataException("Неизвестный тип поля protobuf.");
            }
        }

        private void SkipFixed(int length)
        {
            if (remaining.Length < length) throw new EndOfStreamException("Неполное поле protobuf.");
            remaining = remaining[length..];
        }
    }
}
