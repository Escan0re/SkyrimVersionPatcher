using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.Core.Compatibility;

/// <summary>Updates existing profile INIs so a previous language does not override the selected Steam depot.</summary>
public static class GameLocalizationProfile
{
    private const int MaximumIniBytes = 4 * 1024 * 1024;
    private static readonly Regex VoiceArchive = new(@"(?<prefix>(?:^|,)\s*)Skyrim - Voices_(?:en0|fr0|it0|de0|es0|pl0|ru0|ja0)\.bsa(?=\s*(?:,|$))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Missing profiles and INIs are left to Skyrim, which creates them from Skyrim_Default.ini.</summary>
    public static IReadOnlyList<GameLocalizationIniPlan> Prepare(string profileDirectory, string gameLanguage,
        string? defaultIniPath = null, string? defaultIniSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory) || !Path.IsPathFullyQualified(profileDirectory))
            throw new InvalidDataException("Папка профиля Skyrim должна иметь абсолютный путь.");
        var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory));
        if (profile.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(profile)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Корень диска нельзя использовать как папку профиля Skyrim.");
        DownloadPathSafety.EnsureNoLinks(profile);
        var language = GameLocalizationCatalog.Get(gameLanguage);
        // Steam's selected, verified Skyrim_Default.ini is authoritative for the exact INI
        // language token, including Traditional Chinese. Do not infer it from Steam's ID.
        if (defaultIniPath is not null) language = language with { IniLanguage = ReadDefaultLanguage(defaultIniPath, defaultIniSha256) };
        var result = new List<GameLocalizationIniPlan>();
        foreach (var name in new[] { "Skyrim.ini", "SkyrimCustom.ini", "SkyrimPrefs.ini" })
        {
            var path = Path.Combine(profile, name);
            DownloadPathSafety.EnsureNoLinks(path);
            if (Directory.Exists(path)) throw new IOException("Вместо файла настроек Skyrim существует папка: " + path);
            byte[] original;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > MaximumIniBytes) throw new InvalidDataException("Файл настроек Skyrim превышает допустимые 4 МиБ: " + path);
                original = new byte[checked((int)stream.Length)];
                stream.ReadExactly(original);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            var (encoding, preambleLength) = DetectEncoding(original);
            var content = encoding.GetString(original, preambleLength, original.Length - preambleLength);
            var updated = Update(content, language, ensureKeys: name == "Skyrim.ini");
            if (updated == content) continue;
            // Fail before game-file replacement when an INI that needs changes cannot be written.
            using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var bytes = original.Take(preambleLength).Concat(encoding.GetBytes(updated)).ToArray();
            result.Add(new(path, Convert.ToHexString(SHA256.HashData(original)), original, bytes));
        }
        return result.AsReadOnly();
    }

    private static string ReadDefaultLanguage(string path, string? expectedSha256)
    {
        DownloadPathSafety.EnsureNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumIniBytes) throw new InvalidDataException("Skyrim_Default.ini превышает допустимые 4 МиБ.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (expectedSha256 is not null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Проверенный Skyrim_Default.ini изменился. Игра не изменена; повторите установку.");
        var (encoding, offset) = DetectEncoding(bytes);
        var section = "";
        foreach (var line in encoding.GetString(bytes, offset, bytes.Length - offset).Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { section = trimmed[1..^1].Trim(); continue; }
            var equals = line.IndexOf('=');
            if (equals < 0 || !section.Equals("General", StringComparison.OrdinalIgnoreCase) ||
                !line[..equals].Trim().Equals("sLanguage", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(equals + 1)..].Split(';', '#')[0].Trim();
            if (value.Length is > 0 and <= 32 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                return value;
            throw new InvalidDataException("Некорректный язык в проверенном Skyrim_Default.ini.");
        }
        throw new InvalidDataException("В проверенном Skyrim_Default.ini отсутствует [General] sLanguage. Игра не изменена.");
    }

    /// <summary>Locks and checks every existing INI before writing it without auxiliary profile files.</summary>
    public static void Apply(IReadOnlyList<GameLocalizationIniPlan> plans)
    {
        var streams = new List<FileStream>();
        try
        {
            foreach (var plan in plans)
            {
                if (Convert.ToHexString(SHA256.HashData(plan.OriginalBytes)) != plan.OriginalSha256)
                    throw new InvalidDataException("Повреждён снимок настроек Skyrim: " + plan.Path);
                DownloadPathSafety.EnsureNoLinks(plan.Path);
                var stream = new FileStream(plan.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                    4096, FileOptions.WriteThrough);
                streams.Add(stream);
                if (stream.Length != plan.OriginalBytes.Length ||
                    Convert.ToHexString(SHA256.HashData(stream)) != plan.OriginalSha256)
                    throw new IOException("Настройки Skyrim изменились после проверки. Повторите установку: " + plan.Path);
            }
            for (var index = 0; index < plans.Count; index++)
            {
                var stream = streams[index];
                stream.Position = 0;
                stream.Write(plans[index].UpdatedBytes);
                stream.SetLength(plans[index].UpdatedBytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }
        finally { foreach (var stream in streams) stream.Dispose(); }
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(false, true), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, false, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, false, true), 2);
        try { new UTF8Encoding(false, true).GetString(bytes); return (new UTF8Encoding(false, true), 0); }
        catch (DecoderFallbackException) { return (Encoding.Latin1, 0); } // losslessly retain legacy ANSI comments
    }

    private static string Update(string content, GameLocalizationDefinition language, bool ensureKeys)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var section = "";
        var languageFound = false;
        var voiceFound = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { section = trimmed[1..^1].Trim(); continue; }
            var equals = lines[i].IndexOf('=');
            if (equals < 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            var key = lines[i][..equals].Trim();
            var (activeValue, trailingComment) = SplitValue(lines[i][(equals + 1)..]);
            if (section.Equals("General", StringComparison.OrdinalIgnoreCase) && key.Equals("sLanguage", StringComparison.OrdinalIgnoreCase))
            {
                var leadingWhitespace = activeValue[..(activeValue.Length - activeValue.TrimStart().Length)];
                var trailingWhitespace = activeValue[activeValue.TrimEnd().Length..];
                lines[i] = lines[i][..(equals + 1)] + leadingWhitespace + language.IniLanguage + trailingWhitespace + trailingComment;
                languageFound = true;
            }
            if (section.Equals("Archive", StringComparison.OrdinalIgnoreCase) && key.StartsWith("sResourceArchiveList", StringComparison.OrdinalIgnoreCase)
                && VoiceArchive.IsMatch(activeValue))
            {
                lines[i] = lines[i][..(equals + 1)] + VoiceArchive.Replace(activeValue,
                    match => match.Groups["prefix"].Value + language.VoiceArchiveName) + trailingComment;
                voiceFound = true;
            }
        }
        if (ensureKeys && !languageFound) AddKey(lines, "General", "sLanguage", language.IniLanguage);
        if (ensureKeys && !voiceFound) AddKey(lines, "Archive", "sResourceArchiveList2", language.VoiceArchiveName, appendValue: true);
        return string.Join(newline, lines);
    }

    private static (string ActiveValue, string TrailingComment) SplitValue(string value)
    {
        var commentStart = value.IndexOfAny([';', '#']);
        return commentStart < 0 ? (value, "") : (value[..commentStart], value[commentStart..]);
    }

    private static void AddKey(List<string> lines, string section, string key, string value, bool appendValue = false)
    {
        var sectionStart = lines.FindIndex(line => line.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            lines.Add("[" + section + "]");
            lines.Add(key + "=" + value);
            return;
        }
        var sectionEnd = lines.FindIndex(sectionStart + 1, line => line.TrimStart().StartsWith('['));
        if (sectionEnd < 0) sectionEnd = lines.Count;
        if (appendValue)
        {
            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                var equals = lines[i].IndexOf('=');
                if (equals >= 0 && lines[i][..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    var (activeValue, trailingComment) = SplitValue(lines[i][(equals + 1)..]);
                    var trailingWhitespace = activeValue[activeValue.TrimEnd().Length..];
                    lines[i] = lines[i][..(equals + 1)] + activeValue.TrimEnd() +
                        (string.IsNullOrWhiteSpace(activeValue) ? "" : ", ") + value + trailingWhitespace + trailingComment;
                    return;
                }
            }
        }
        lines.Insert(sectionEnd, key + "=" + value);
    }
}

public sealed record GameLocalizationIniPlan(string Path, string OriginalSha256, byte[] OriginalBytes, byte[] UpdatedBytes);
