using System.Globalization;
using SkyrimVersionPatcher.Core.Catalog;

namespace SkyrimVersionPatcher.Core.Downloading;

public static class SteamConsoleCommands
{
    public const uint AppId = 489830;
    public const string ConsoleUri = "steam://nav/console";

    public static string ForDepot(DepotManifest depot)
    {
        ValidateDepot(depot);
        return $"download_depot {AppId} {depot.DepotId} {depot.ManifestId}";
    }

    public static IReadOnlyList<string> ForVersion(IReadOnlyList<DepotManifest> depots) =>
        depots.Select(ForDepot).ToArray();

    public static void ValidateDepot(DepotManifest depot)
    {
        ArgumentNullException.ThrowIfNull(depot);
        if (depot.DepotId is not (489831 or 489832 or 489833) && !GameLocalizationCatalog.IsLocalizationDepot(depot.DepotId) ||
            !ulong.TryParse(depot.ManifestId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0 ||
            id.ToString(CultureInfo.InvariantCulture) != depot.ManifestId)
            throw new ArgumentException("Недопустимый идентификатор депо или манифеста Skyrim.");
    }
}
