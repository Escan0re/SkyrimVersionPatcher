namespace SkyrimVersionPatcher.App;

internal enum SteamConsoleTransport
{
    UiAutomation,
    Msaa
}

/// <summary>Only a confirmed failure before Enter permits trying another accessibility provider.</summary>
internal static class SteamConsoleTransportFallback
{
    internal static SteamConsoleTransport? TryDispatch(SteamConsoleTransport preferred,
        Func<bool> tryUiAutomation, Func<bool> tryMsaa, CancellationToken token)
    {
        var alternate = preferred switch
        {
            SteamConsoleTransport.UiAutomation => SteamConsoleTransport.Msaa,
            SteamConsoleTransport.Msaa => SteamConsoleTransport.UiAutomation,
            _ => throw new ArgumentOutOfRangeException(nameof(preferred))
        };
        token.ThrowIfCancellationRequested();
        if ((preferred == SteamConsoleTransport.UiAutomation ? tryUiAutomation : tryMsaa)()) return preferred;
        token.ThrowIfCancellationRequested();
        if ((alternate == SteamConsoleTransport.UiAutomation ? tryUiAutomation : tryMsaa)()) return alternate;
        return null;
    }
}
