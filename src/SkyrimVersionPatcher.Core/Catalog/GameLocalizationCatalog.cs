using System.Globalization;

namespace SkyrimVersionPatcher.Core.Catalog;

/// <summary>The nine official Steam localizations for Skyrim Special Edition (App 489830).</summary>
public static class GameLocalizationCatalog
{
    public const string English = "english";
    public const string Russian = "russian";

    public static IReadOnlyList<GameLocalizationDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new GameLocalizationDefinition(English, "English", "English", "Английский", null, "ENGLISH", "en0"),
        new GameLocalizationDefinition("french", "Français", "French", "Французский", 489834, "FRENCH", "fr0"),
        new GameLocalizationDefinition("italian", "Italiano", "Italian", "Итальянский", 489835, "ITALIAN", "it0"),
        new GameLocalizationDefinition("german", "Deutsch", "German", "Немецкий", 489836, "GERMAN", "de0"),
        new GameLocalizationDefinition("spanish", "Español", "Spanish (Spain)", "Испанский (Испания)", 489837, "SPANISH", "es0"),
        new GameLocalizationDefinition("polish", "Polski", "Polish", "Польский", 489839, "POLISH", "pl0"),
        new GameLocalizationDefinition("chinese", "繁體中文", "Traditional Chinese", "Китайский (традиционный)", 544860, "CHINESE", "en0"),
        new GameLocalizationDefinition(Russian, "Русский", "Russian", "Русский", 489838, "RUSSIAN", "ru0"),
        new GameLocalizationDefinition("japanese", "日本語", "Japanese", "Японский", 544861, "JAPANESE", "ja0")
    });

    public static GameLocalizationDefinition Get(string id) =>
        All.FirstOrDefault(language => string.Equals(language.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Неизвестная локализация Skyrim: " + id, nameof(id));

    public static bool IsSupported(string? id) => id is not null &&
        All.Any(language => string.Equals(language.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsLocalizationDepot(uint depotId) => All.Any(language => language.DepotId == depotId);

    /// <summary>Unsupported system languages fall back to English; all Chinese cultures use the available Traditional Chinese localization.</summary>
    public static string ResolveSystemLanguage(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "fr" => "french", "it" => "italian", "de" => "german", "es" => "spanish",
            "pl" => "polish", "zh" => "chinese", "ru" => Russian, "ja" => "japanese", _ => English
        };
    }
}

public sealed record GameLocalizationDefinition(string Id, string NativeName, string EnglishName,
    string RussianName, uint? DepotId, string IniLanguage, string VoiceLanguage)
{
    public string VoiceArchiveName => "Skyrim - Voices_" + VoiceLanguage + ".bsa";
    public override string ToString() => NativeName;
}
