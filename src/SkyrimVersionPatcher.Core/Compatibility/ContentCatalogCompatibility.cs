using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SkyrimVersionPatcher.Core.Compatibility;

/// <summary>
/// Preserves a newer Creations catalog that older Skyrim runtimes cannot read.
/// Only the recognized GUID-based format and explicitly supported targets are handled.
/// </summary>
public static class ContentCatalogCompatibility
{
    private const string CatalogName = "ContentCatalog.txt";
    private const string RenamedCatalogName = "ContentCatalog.bak";
    // The GUID-based catalog belongs to the format introduced in 1.7.99. The file
    // does not record which exact Skyrim release created it.
    public const string ModernFormatVersion = "1.7.99+";
    private const long MaximumCatalogBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> LegacyTargets = new(StringComparer.Ordinal)
    {
        "1.5.97", "1.6.1170"
    };

    /// <summary>Reads and validates without changing the profile.</summary>
    public static ContentCatalogPlan? Prepare(string profileDirectory, string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        ArgumentNullException.ThrowIfNull(targetVersion);
        if (!LegacyTargets.Contains(targetVersion)) return null;
        var profile = CanonicalProfile(profileDirectory);
        EnsureNoLinks(profile);
        var profileAttributes = ReadAttributes(profile);
        if (profileAttributes is null) return null;
        if ((profileAttributes.Value & FileAttributes.Directory) == 0)
            throw new IOException("Вместо папки профиля Skyrim существует файл: " + profile);
        var catalog = Path.Combine(profile, CatalogName);
        EnsureNoLinks(catalog);
        var catalogAttributes = ReadAttributes(catalog);
        if (catalogAttributes is null) return null;
        if ((catalogAttributes.Value & FileAttributes.Directory) != 0)
            throw new IOException("Вместо ContentCatalog.txt существует папка. Каталог не изменён: " + catalog);

        using var stream = OpenRead(catalog);
        var length = stream.Length;
        if (length > MaximumCatalogBytes)
            throw new InvalidDataException("ContentCatalog.txt превышает допустимые 16 МиБ. Каталог сохранён без изменений: " + catalog);
        var bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        // Windows-created JSON may include a UTF-8 byte order mark.
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes.AsMemory(offset)); }
        catch (JsonException error)
        {
            throw new InvalidDataException("Не удалось прочитать JSON в ContentCatalog.txt. Каталог сохранён без изменений: " + catalog, error);
        }
        using (document)
        {
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var modernEntries = document.RootElement.EnumerateObject().Where(property =>
                property.Name.StartsWith("CSV2_", StringComparison.Ordinal) && property.Name.Length == 41 &&
                Guid.TryParseExact(property.Name.AsSpan(5), "D", out _)).ToArray();
            if (modernEntries.Length == 0) return null;
            foreach (var entry in modernEntries)
            {
                if (entry.Value.ValueKind != JsonValueKind.Object ||
                    !entry.Value.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array ||
                    files.EnumerateArray().Any(file => file.ValueKind != JsonValueKind.String || !IsSimpleFileName(file.GetString())))
                    throw new InvalidDataException("Некорректная запись нового формата ContentCatalog.txt. Каталог сохранён без изменений: " + entry.Name);
            }
        }
        return new(profile, catalog, targetVersion, length, Convert.ToHexString(SHA256.HashData(bytes)),
            Path.Combine(profile, RenamedCatalogName), ModernFormatVersion);
    }

    /// <summary>
    /// Verifies the fixed rename destination before installation starts. Detection
    /// itself remains possible when a previous ContentCatalog.bak is present.
    /// </summary>
    public static void ValidateRenameDestination(ContentCatalogPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureNoLinks(plan.ProfileDirectory);
        EnsureNoLinks(plan.RenamedPath);
        if (ReadAttributes(plan.RenamedPath) is not null)
            throw new IOException("ContentCatalog.bak уже существует и не будет перезаписан. " +
                "Переместите этот файл или отключите переименование ContentCatalog.txt перед установкой: " + plan.RenamedPath);
    }

    /// <summary>Atomically renames the verified catalog to ContentCatalog.bak without overwriting an existing file.</summary>
    public static string Apply(ContentCatalogPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureGameClosed();
        EnsureNoLinks(plan.ProfileDirectory);
        EnsureNoLinks(plan.CatalogPath);
        ValidateRenameDestination(plan);
        var attributes = ReadAttributes(plan.CatalogPath);
        if (attributes is null)
            throw new IOException("ContentCatalog.txt исчез после проверки. Повторите установку; профиль не изменён.");
        if ((attributes.Value & FileAttributes.Directory) != 0)
            throw new IOException("Вместо ContentCatalog.txt появилась папка. Повторите проверку профиля Skyrim.");
        using var stream = OpenRead(plan.CatalogPath);
        if (stream.Length != plan.Length ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), plan.Sha256, StringComparison.Ordinal))
            throw new IOException("ContentCatalog.txt изменился после проверки. Повторите установку; каталог не изменён.");
        // The held read handle denies writers while allowing this same-volume rename.
        EnsureNoLinks(plan.CatalogPath);
        ValidateRenameDestination(plan);
        EnsureGameClosed();
        File.Move(plan.CatalogPath, plan.RenamedPath, overwrite: false);
        EnsureNoLinks(plan.RenamedPath);
        using var renamed = OpenRead(plan.RenamedPath);
        if (renamed.Length != plan.Length || Convert.ToHexString(SHA256.HashData(renamed)) != plan.Sha256)
            throw new IOException("Переименованный ContentCatalog.txt отличается от проверенного файла. Каталог сохранён здесь: " + plan.RenamedPath);
        return plan.RenamedPath;
    }

    // Exists deliberately hides access failures. Only a genuinely missing path is absent;
    // unreadable profiles must not lead to a successful but incomplete installation.
    private static FileAttributes? ReadAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (ReadAttributes(current) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Путь профиля Skyrim содержит ссылку или junction: " + current);
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static string CanonicalProfile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Папка профиля Skyrim должна иметь абсолютный путь.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Корень диска нельзя использовать как папку профиля Skyrim.");
        return full;
    }

    private static bool IsSimpleFileName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        name is not "." and not ".." && !name.Contains('/') && !name.Contains('\\') && !name.Contains(':') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("ContentCatalog.txt содержит повторяющееся поле. Каталог сохранён без изменений: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void EnsureGameClosed()
    {
        foreach (var name in new[] { "SkyrimSE", "SkyrimSELauncher", "skse64_loader" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                    throw new IOException("Закройте Skyrim, его лаунчер и SKSE перед исправлением ContentCatalog.txt.");
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}

/// <summary>A read-only snapshot produced by ContentCatalogCompatibility.Prepare.</summary>
public sealed class ContentCatalogPlan
{
    internal ContentCatalogPlan(string profileDirectory, string catalogPath, string targetVersion,
        long length, string sha256, string renamedPath, string sourceVersion)
    {
        ProfileDirectory = profileDirectory;
        CatalogPath = catalogPath;
        TargetVersion = targetVersion;
        Length = length;
        Sha256 = sha256;
        RenamedPath = renamedPath;
        SourceVersion = sourceVersion;
    }

    public string ProfileDirectory { get; }
    public string CatalogPath { get; }
    public string TargetVersion { get; }
    public long Length { get; }
    public string Sha256 { get; }
    public string RenamedPath { get; }
    /// <summary>Format-based version range; the exact creating release is not stored in the catalog.</summary>
    public string SourceVersion { get; }
}
