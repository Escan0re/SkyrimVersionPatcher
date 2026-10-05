using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyrimVersionPatcher.Core.Catalog;

/// <summary>Validated, offline catalog. Importing a catalog never executes code or opens its URLs.</summary>
public static class VersionCatalogService
{
    public const uint AppId = 489830;
    public const uint RussianDepotId = 489838;
    public static IReadOnlyList<string> SupportedVersionIds { get; } = Array.AsReadOnly(new[] { "1.6.1170", "1.5.97" });

    public static bool IsSupportedVersion(string? versionId) =>
        versionId is "1.6.1170" or "1.5.97";
    private const int MaxCatalogCharacters = 2_000_000;
    private static readonly uint[] RequiredDepotIds = [489831, 489832, 489833];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<GameVersionDefinition> LoadBuiltIn()
    {
        using var stream = typeof(VersionCatalogService).Assembly
            .GetManifestResourceStream("SkyrimVersionPatcher.Core.versions.json")
            ?? throw new InvalidOperationException("Встроенный каталог версий не найден.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<GameVersionDefinition> Load(string path)
    {
        if (new FileInfo(path).Length > MaxCatalogCharacters * 4L)
            throw new InvalidDataException("Файл каталога слишком большой.");
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Returns the two supported targets with user changes. Removed legacy targets are skipped.</summary>
    public static IReadOnlyList<GameVersionDefinition> LoadWithCustom(string path, Action<string>? reportDiagnostic = null)
        => LoadWithCustom(LoadBuiltIn(), path, reportDiagnostic);

    /// <summary>Merges validated base entries and user changes; user metadata and manifests take precedence.</summary>
    public static IReadOnlyList<GameVersionDefinition> LoadWithCustom(
        IEnumerable<GameVersionDefinition> baseVersions, string path, Action<string>? reportDiagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(baseVersions);
        var versions = LoadBuiltIn().ToDictionary(v => v.Id, StringComparer.Ordinal);
        foreach (var entry in ValidateAll(baseVersions.ToArray())) versions[entry.Id] = WithMissingLocalizations(entry, versions[entry.Id]);
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > MaxCatalogCharacters * 4L)
                throw new InvalidDataException("Файл каталога слишком большой.");
            foreach (var entry in NormalizeOverrides(ParseSupportedVersions(File.ReadAllText(path, Encoding.UTF8), reportDiagnostic)))
                versions[entry.Id] = WithMissingLocalizations(entry, versions[entry.Id]);
        }
        return Array.AsReadOnly(versions.Values.OrderByDescending(v => Version.Parse(v.Id)).ToArray());
    }

    // Older custom and remote schema-1 catalogs contain only English/Russian. Preserve their
    // metadata and chosen manifests while adding the newly supported localization depots.
    private static GameVersionDefinition WithMissingLocalizations(GameVersionDefinition entry, GameVersionDefinition baseline) =>
        entry with { Depots = Array.AsReadOnly(entry.Depots.Concat(baseline.Depots.Where(depot =>
            GameLocalizationCatalog.IsLocalizationDepot(depot.DepotId) && depot.DepotId != RussianDepotId &&
            !entry.Depots.Any(existing => existing.DepotId == depot.DepotId))).ToArray()) };

    /// <summary>
    /// Keeps only actual changes to the two built-ins. This also normalizes legacy files
    /// containing the whole merged catalog, without discarding user notes or source links.
    /// </summary>
    public static IReadOnlyList<GameVersionDefinition> NormalizeOverrides(IEnumerable<GameVersionDefinition> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var validated = ValidateAll(versions.ToArray());
        var builtIns = LoadBuiltIn().ToDictionary(v => v.Id, StringComparer.Ordinal);
        return Array.AsReadOnly(validated
            .Where(v => !builtIns.TryGetValue(v.Id, out var builtIn) || !HasSameValues(v, builtIn))
            .OrderByDescending(v => Version.Parse(v.Id)).ToArray());
    }

    /// <summary>Atomically saves user changes only. An unchanged merged catalog produces no overrides.</summary>
    public static void SaveOverrides(string path, IEnumerable<GameVersionDefinition> versions) =>
        Save(path, NormalizeOverrides(versions));

    private static bool HasSameValues(GameVersionDefinition left, GameVersionDefinition right) =>
        left.Id == right.Id && left.DisplayName == right.DisplayName &&
        left.Notes == right.Notes && left.SourceUrl == right.SourceUrl &&
        left.Depots.OrderBy(d => d.DepotId).SequenceEqual(right.Depots.OrderBy(d => d.DepotId));

    public static IReadOnlyList<GameVersionDefinition> Parse(string json)
        => ParseCore(json, skipUnsupported: false, reportDiagnostic: null);

    /// <summary>Migration path for existing local or remote catalogs; removed targets never enter the result.</summary>
    internal static IReadOnlyList<GameVersionDefinition> ParseSupportedVersions(string json, Action<string>? reportDiagnostic)
        => ParseCore(json, skipUnsupported: true, reportDiagnostic);

    private static IReadOnlyList<GameVersionDefinition> ParseCore(string json, bool skipUnsupported, Action<string>? reportDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxCatalogCharacters)
            throw new InvalidDataException("Каталог слишком большой.");

        CatalogDocument document;
        try
        {
            using (var syntax = JsonDocument.Parse(json, new() { MaxDepth = 16 }))
                RejectDuplicateMembers(syntax.RootElement);
            document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("Каталог не может быть null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Неверный JSON каталога: {exception.Message}", exception);
        }

        if (document.SchemaVersion != 1)
            throw new InvalidDataException("Поддерживается только schemaVersion = 1.");
        if (document.AppId != AppId)
            throw new InvalidDataException($"Каталог должен относиться к Steam App ID {AppId}.");
        if (!skipUnsupported) return ValidateAll(document.Versions);
        if (document.Versions is null || document.Versions.Count > 1000)
            throw new InvalidDataException("Каталог должен содержать массив не более чем из 1000 версий.");
        var supported = new List<GameVersionDefinition>();
        foreach (var entry in document.Versions)
        {
            if (entry is null) throw new InvalidDataException("Запись версии не может быть null.");
            ValidateVersionId(entry.Id);
            if (!IsSupportedVersion(entry.Id))
            {
                reportDiagnostic?.Invoke($"Версия {entry.Id} удалена из поддерживаемого каталога и пропущена. Доступны только 1.6.1170 и 1.5.97.");
                continue;
            }
            supported.Add(entry);
        }
        return ValidateAll(supported);
    }

    internal static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("Повторное поле JSON каталога: " + property.Name);
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateMembers(item);
    }

    public static string Serialize(IEnumerable<GameVersionDefinition> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var validated = ValidateAll(versions.ToArray());
        var json = JsonSerializer.Serialize(new CatalogDocument(1, AppId, validated), JsonOptions)
            + Environment.NewLine;
        if (json.Length > MaxCatalogCharacters)
            throw new InvalidDataException("Каталог слишком большой.");
        return json;
    }

    /// <summary>Validates before writing, then atomically replaces the destination within its directory.</summary>
    public static void Save(string path, IEnumerable<GameVersionDefinition> versions)
    {
        var json = Serialize(versions);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static void Validate(GameVersionDefinition version)
    {
        if (version is null)
            throw new InvalidDataException("Запись версии не может быть null.");
        ValidateVersionId(version.Id);
        if (!IsSupportedVersion(version.Id))
            throw new InvalidDataException($"Версия {version.Id} не поддерживается. Доступны только 1.6.1170 и 1.5.97.");
        if (string.IsNullOrWhiteSpace(version.DisplayName) || version.DisplayName.Length > 160 ||
            version.DisplayName.Any(char.IsControl))
            throw new InvalidDataException($"Версия {version.Id}: укажите короткое отображаемое название.");
        if (version.Notes?.Length > 4000)
            throw new InvalidDataException($"Версия {version.Id}: примечание слишком длинное.");
        if (version.SourceUrl is not null &&
            (version.SourceUrl.Length > 2000 ||
             !Uri.TryCreate(version.SourceUrl, UriKind.Absolute, out var source) ||
             (source.Scheme != Uri.UriSchemeHttps && source.Scheme != Uri.UriSchemeHttp) ||
             !string.IsNullOrEmpty(source.UserInfo)))
            throw new InvalidDataException($"Версия {version.Id}: sourceUrl должен быть ссылкой HTTP или HTTPS без учетных данных.");
        if (version.Depots is null || version.Depots.Count is < 3 or > 11)
            throw new InvalidDataException($"Версия {version.Id}: нужны три основных депо и доступные депо локализаций.");

        var seen = new HashSet<uint>();
        foreach (var depot in version.Depots)
        {
            if (depot is null)
                throw new InvalidDataException($"Версия {version.Id}: депо не может быть null.");
            if (!RequiredDepotIds.Contains(depot.DepotId) && !GameLocalizationCatalog.IsLocalizationDepot(depot.DepotId))
                throw new InvalidDataException($"Версия {version.Id}: неподдерживаемый Depot ID {depot.DepotId}.");
            if (!seen.Add(depot.DepotId))
                throw new InvalidDataException($"Версия {version.Id}: Depot ID {depot.DepotId} повторяется.");
            if (depot.IsRussian != (depot.DepotId == RussianDepotId))
                throw new InvalidDataException($"Версия {version.Id}: isRussian должен быть true только для депо {RussianDepotId}.");
            if (string.IsNullOrEmpty(depot.ManifestId) || depot.ManifestId.Length > 20 ||
                depot.ManifestId.Any(c => c < '0' || c > '9') ||
                !ulong.TryParse(depot.ManifestId, NumberStyles.None, CultureInfo.InvariantCulture, out var manifest) ||
                manifest == 0 || manifest.ToString(CultureInfo.InvariantCulture) != depot.ManifestId)
                throw new InvalidDataException($"Версия {version.Id}, депо {depot.DepotId}: manifestId должен быть строкой с положительным UInt64 без ведущих нулей.");
        }
        if (RequiredDepotIds.Any(id => !seen.Contains(id)))
            throw new InvalidDataException($"Версия {version.Id}: обязательны депо 489831, 489832 и 489833.");
    }

    /// <summary>Compatibility overload: true selects Russian, false selects English.</summary>
    public static IReadOnlyList<DepotManifest> GetDepots(GameVersionDefinition version, bool includeRussian)
        => GetDepots(version, includeRussian ? GameLocalizationCatalog.Russian : GameLocalizationCatalog.English);

    /// <summary>Applies only the selected localization depot, last, to override the core Skyrim_Default.ini.</summary>
    public static IReadOnlyList<DepotManifest> GetDepots(GameVersionDefinition version, string gameLanguage)
    {
        Validate(version);
        var language = GameLocalizationCatalog.Get(gameLanguage);
        if (language.DepotId is { } localizationDepot && !version.Depots.Any(d => d.DepotId == localizationDepot))
            throw new InvalidDataException($"Для версии {version.Id} не задан manifest локализации «{language.NativeName}». Добавьте его в каталог или выберите другую локализацию.");
        return Array.AsReadOnly(version.Depots.Where(d => RequiredDepotIds.Contains(d.DepotId) || d.DepotId == language.DepotId)
            .OrderBy(d => GameLocalizationCatalog.IsLocalizationDepot(d.DepotId)).ThenBy(d => d.DepotId).ToArray());
    }

    private static void ValidateVersionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 ||
            !Version.TryParse(id, out var parsedVersion) || parsedVersion.Build < 0 ||
            parsedVersion.ToString() != id || id.Any(c => (c < '0' || c > '9') && c != '.'))
            throw new InvalidDataException("id должен быть версией SkyrimSE.exe, например 1.6.1170.");
    }

    private static IReadOnlyList<GameVersionDefinition> ValidateAll(IReadOnlyList<GameVersionDefinition>? versions)
    {
        if (versions is null || versions.Count > 1000)
            throw new InvalidDataException("Каталог должен содержать массив не более чем из 1000 версий.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GameVersionDefinition>(versions.Count);
        foreach (var version in versions)
        {
            Validate(version);
            if (!seen.Add(version.Id))
                throw new InvalidDataException($"Версия {version.Id} повторяется в каталоге.");
            result.Add(version with { Depots = Array.AsReadOnly(version.Depots.ToArray()) });
        }
        return result.AsReadOnly();
    }

    private sealed record CatalogDocument(int SchemaVersion, uint AppId, IReadOnlyList<GameVersionDefinition>? Versions);
}
