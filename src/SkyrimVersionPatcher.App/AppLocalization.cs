using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace SkyrimVersionPatcher.App;

internal static class AppLocalization
{
    private sealed record TextEntry(string Ru, string En);
    private sealed record MessageRule(Regex Pattern, string Replacement);
    private static readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<string, TextEntry> remembered = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> rememberedOrder = new();
    private static readonly Lazy<Dictionary<string, TextEntry>> resources = new(() =>
        ReadJson<Dictionary<string, TextEntry>>("ui-texts.json") ?? []);
    private static readonly Lazy<MessageRule[]> russianMessages = new(() => BuildMessageRules(toEnglish: true));
    private static readonly Lazy<MessageRule[]> englishMessages = new(() => BuildMessageRules(toEnglish: false));
    private static ResourceDictionary? targetResources;

    public static CultureInfo SystemCulture
    {
        get
        {
            try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()); }
            catch (Exception error) when (error is CultureNotFoundException or DllNotFoundException or EntryPointNotFoundException)
            { return CultureInfo.InstalledUICulture; }
        }
    }

    public static string CurrentLanguage { get; private set; } = ResolveLanguage(null);
    public static string ResolveLanguage(string? saved) => saved is "ru" or "en" ? saved
        : SystemCulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";

    public static void SetLanguage(string? language)
    {
        CurrentLanguage = ResolveLanguage(language);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
        if (targetResources is not null) LoadResources(targetResources);
    }

    public static void LoadResources(ResourceDictionary dictionary)
    {
        targetResources = dictionary;
        foreach (var (key, value) in resources.Value)
            dictionary[key] = Translate(value.Ru, value.En);
    }

    public static string Translate(string ru, string en)
    {
        var entry = new TextEntry(ru, en);
        Remember(ru, entry);
        Remember(en, entry);
        return CurrentLanguage == "ru" ? ru : en;
    }

    private static void Remember(string text, TextEntry entry)
    {
        if (remembered.TryAdd(text, entry)) rememberedOrder.Enqueue(text);
        while (remembered.Count > 2048 && rememberedOrder.TryDequeue(out var oldest))
            remembered.TryRemove(oldest, out _);
    }

    // Match complete application messages; captured file paths and identifiers stay untouched.
    public static string TranslateMessage(string message) => TranslateMessage(message, 0);

    private static string TranslateMessage(string message, int depth)
    {
        if (string.IsNullOrEmpty(message)) return message;
        if (remembered.TryGetValue(message, out var known))
            return CurrentLanguage == "ru" ? known.Ru : known.En;
        if (depth > 4) return message;
        var rules = CurrentLanguage == "en" ? russianMessages.Value : englishMessages.Value;
        foreach (var rule in rules)
        {
            Match match;
            try { match = rule.Pattern.Match(message); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;
            var output = Regex.Replace(rule.Replacement, @"\{(\d+)\}", placeholder =>
            {
                var captured = match.Groups["p" + placeholder.Groups[1].Value].Value;
                return TranslateMessage(captured, depth + 1);
            });
            return CurrentLanguage == "en" ? Translate(message, output) : Translate(output, message);
        }
        return message;
    }

    private static MessageRule[] BuildMessageRules(bool toEnglish)
    {
        var entries = ReadJson<TextEntry[]>("message-texts.json") ?? [];
        return entries.OrderByDescending(entry => Regex.Replace(toEnglish ? entry.Ru : entry.En, @"\{\d+\}", "").Length)
            .Select(entry =>
            {
                var source = toEnglish ? entry.Ru : entry.En;
                var seen = new HashSet<string>();
                var pieces = Regex.Split(source, @"(\{\d+\})").Select(piece =>
                {
                    var placeholder = Regex.Match(piece, @"^\{(\d+)\}$");
                    if (!placeholder.Success) return Regex.Escape(piece);
                    var group = "p" + placeholder.Groups[1].Value;
                    return seen.Add(group) ? "(?<" + group + ">[\\s\\S]*?)" : "\\k<" + group + ">";
                });
                return new MessageRule(new Regex("\\A" + string.Concat(pieces) + "\\z", RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)), toEnglish ? entry.En : entry.Ru);
            }).ToArray();
    }

    private static T? ReadJson<T>(string file)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith("." + file, StringComparison.Ordinal));
        if (resource is null) return default;
        using var stream = assembly.GetManifestResourceStream(resource);
        return stream is null ? default : JsonSerializer.Deserialize<T>(stream, jsonOptions);
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}
