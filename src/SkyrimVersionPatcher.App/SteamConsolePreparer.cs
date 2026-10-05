using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.App;

/// <summary>Only inspects or opens a console in the existing Steam client.</summary>
internal interface ISteamConsoleEnvironment
{
    SteamClientSession GetSession();
    Task<bool> TryConnectExistingDebugAsync(SteamClientSession session, CancellationToken token);
    void OpenConsole(SteamClientSession session);
    Task<bool> TryPrepareAccessibilityAsync(SteamClientSession session, CancellationToken token);
}

/// <summary>No shutdown, process launch flags or restart operation is available to preparation.</summary>
internal sealed class SteamConsolePreparer(ISteamConsoleEnvironment environment, Action<string>? reportStatus = null)
{
    internal async Task<SteamClientSession> PrepareAsync(SteamClientSession expected, CancellationToken token)
    {
        RequireUnchangedSession(expected, token);
        var connected = await environment.TryConnectExistingDebugAsync(expected, token).ConfigureAwait(false);
        RequireUnchangedSession(expected, token);
        if (connected) return expected;

        reportStatus?.Invoke("Подготовка консоли в открытом клиенте Steam…");
        RequireUnchangedSession(expected, token);
        Exception? accessError = null;
        var accessible = false;
        try
        {
            environment.OpenConsole(expected);
            RequireUnchangedSession(expected, token);
            accessible = await environment.TryPrepareAccessibilityAsync(expected, token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsAccessError(error)) { accessError = error; }
        RequireUnchangedSession(expected, token);
        if (accessible) return expected;

        connected = await environment.TryConnectExistingDebugAsync(expected, token).ConfigureAwait(false);
        RequireUnchangedSession(expected, token);
        if (connected) return expected;
        if (accessError is SteamConsoleUnavailableException actionable) throw actionable;
        if (accessError is not null)
            throw new SteamConsoleUnavailableException("Windows не разрешила доступ к открытой Steam Console. " +
                "Steam и патчер должны работать от одного пользователя с одинаковыми правами. " +
                "Steam не перезапускался; файлы игры не изменены.");
        throw new SteamConsoleUnavailableException("В открытом клиенте Steam не найден доступный способ автоматической загрузки. " +
            "Steam не перезапускался; файлы игры не изменены. Проверьте открытые диалоги Steam и повторите установку.");
    }

    private void RequireUnchangedSession(SteamClientSession expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = environment.GetSession();
        SteamConsoleIdentity.RequireSameAccountAndExecutable(expected, current);
        if (current.ProcessId <= 0 || current.ProcessId != expected.ProcessId)
            throw new SteamConsoleUnavailableException("Процесс Steam изменился во время подготовки загрузки. Повторите установку в открытом клиенте Steam.");
    }

    private static bool IsAccessError(Exception error) => error is UnauthorizedAccessException ||
        error.HResult == unchecked((int)0x80070005) ||
        error is SteamConsoleUnavailableException && error.Message.StartsWith("Windows не разрешила", StringComparison.Ordinal);
}
