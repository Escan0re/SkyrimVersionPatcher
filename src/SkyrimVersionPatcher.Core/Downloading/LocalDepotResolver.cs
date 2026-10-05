using SkyrimVersionPatcher.Core.Catalog;

namespace SkyrimVersionPatcher.Core.Downloading;

/// <summary>Resolves explicitly selected folders without recursive searches into unrelated game directories.</summary>
public static class LocalDepotResolver
{
    public static IReadOnlyList<string> ResolveSources(string selectedDirectory, IReadOnlyList<DepotManifest> depots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedDirectory);
        var root = Path.GetFullPath(selectedDirectory);
        DownloadPathSafety.EnsureNoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Папка со скачанными файлами не найдена: " + root);
        if (depots.Count == 0) throw new ArgumentException("Не выбраны депо.");
        foreach (var depot in depots) SteamConsoleCommands.ValidateDepot(depot);
        var cacheCandidates = new List<string> { root };
        if (Path.GetFileName(root).Equals("depots", StringComparison.OrdinalIgnoreCase)) cacheCandidates.Add(Path.GetDirectoryName(root)!);
        if (Path.GetFileName(root).Equals("489830", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(root)), "depots", StringComparison.OrdinalIgnoreCase))
            cacheCandidates.Add(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!);
        foreach (var cacheRoot in cacheCandidates)
        {
            var sources = depots.Select(d => Path.Combine(SteamClientDownloadService.GetDepotCacheDirectory(cacheRoot, d), "content")).ToArray();
            if (!sources.Any(Directory.Exists)) continue;
            var missing = depots.Where((d, i) => !Directory.Exists(sources[i])).Select(d => $"{d.DepotId}/{d.ManifestId}").ToArray();
            if (missing.Length > 0) throw new InvalidDataException("В кэше нет выбранных депо/манифестов: " + string.Join(", ", missing));
            foreach (var source in sources) DownloadPathSafety.EnsureNoLinks(source);
            return sources;
        }
        // Selecting the executable depot uses its siblings so the complete version is applied.
        if (Path.GetFileName(root).StartsWith("depot_", StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(root)!;
        var candidates = new[] { root, Path.Combine(root, "app_489830"), Path.Combine(root, "content", "app_489830"),
            Path.Combine(root, "steamapps", "content", "app_489830") };
        foreach (var parent in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sources = depots.Select(d => Path.Combine(parent, "depot_" + d.DepotId)).ToArray();
            if (!sources.Any(Directory.Exists)) continue;
            var missing = sources.Where(p => !Directory.Exists(p)).Select(Path.GetFileName).ToArray();
            if (missing.Length != 0)
                throw new InvalidDataException("Не найдены выбранные депо: " + string.Join(", ", missing) +
                    ". Выберите родительскую папку всех депо или другую локализацию.");
            foreach (var source in sources) DownloadPathSafety.EnsureNoLinks(source);
            return sources;
        }
        if (File.Exists(Path.Combine(root, "SkyrimSE.exe")))
        {
            if (!depots.Any(depot => GameLocalizationCatalog.IsLocalizationDepot(depot.DepotId)))
                throw new InvalidDataException("В объединённой папке нельзя отделить локализацию от основных депо. " +
                    "Для английской локализации выберите родительскую папку отдельных depot_* или кэш патчера.");
            // The requested language depot is explicit. ImportLocalDepotAsync must still
            // verify every selected file against its exact Steam manifest before caching.
            // A guessed INI token cannot establish that identity and rejects valid aliases
            // such as TCHINESE or harmless INI whitespace/comments.
            return [root];
        }
        throw new InvalidDataException("В папке нет SkyrimSE.exe или полного набора выбранных depot_*.");
    }
}
