namespace SkyrimVersionPatcher.App;

internal enum SteamConsoleInputDispatchResult
{
    Sent,
    WindowUnavailable,
    FocusUnavailable,
    ValueUnavailable,
    FocusLost,
    ModifiersPressed
}

/// <summary>Waits for asynchronous accessibility acknowledgements before sending Enter once.</summary>
internal static class SteamConsoleInputDispatch
{
    internal static SteamConsoleInputDispatchResult Run(string command, Func<bool> activateWindow,
        Action focus, Func<bool> isForegroundAndTrusted, Func<bool> hasExactFocus,
        Func<string?> readValue, Action<string> setValue, Func<bool> modifiersPressed,
        Action submitEnter, CancellationToken token, Action<CancellationToken>? pollDelay = null,
        int maxPolls = 30)
    {
        SteamCdpTransport.ValidateCommand(command);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPolls);
        pollDelay ??= Delay;
        token.ThrowIfCancellationRequested();
        if (!activateWindow()) return SteamConsoleInputDispatchResult.WindowUnavailable;
        var state = CheckSafety(false);
        if (state != SteamConsoleInputDispatchResult.Sent) return state;
        focus();

        for (var poll = 0; ; poll++)
        {
            state = CheckSafety(false);
            if (state != SteamConsoleInputDispatchResult.Sent) return state;
            var focused = hasExactFocus();
            token.ThrowIfCancellationRequested();
            if (focused) break;
            if (poll == maxPolls) return SteamConsoleInputDispatchResult.FocusUnavailable;
            pollDelay(token);
        }

        state = CheckSafety(true);
        if (state != SteamConsoleInputDispatchResult.Sent) return state;
        setValue(command);
        for (var poll = 0; ; poll++)
        {
            state = CheckSafety(true);
            if (state != SteamConsoleInputDispatchResult.Sent) return state;
            var value = readValue();
            token.ThrowIfCancellationRequested();
            if (string.Equals(value, command, StringComparison.Ordinal)) break;
            if (poll == maxPolls) return SteamConsoleInputDispatchResult.ValueUnavailable;
            pollDelay(token);
        }

        // Read again: acknowledging the setter does not prove the value is still current.
        token.ThrowIfCancellationRequested();
        var finalValue = readValue();
        token.ThrowIfCancellationRequested();
        if (!string.Equals(finalValue, command, StringComparison.Ordinal))
            return SteamConsoleInputDispatchResult.ValueUnavailable;
        state = CheckSafety(true);
        if (state != SteamConsoleInputDispatchResult.Sent) return state;
        token.ThrowIfCancellationRequested();
        submitEnter();
        // Enter may have been partially submitted if the delegate throws. Never retry or inspect afterward.
        return SteamConsoleInputDispatchResult.Sent;

        SteamConsoleInputDispatchResult CheckSafety(bool requireFocus)
        {
            token.ThrowIfCancellationRequested();
            if (requireFocus && !hasExactFocus()) return SteamConsoleInputDispatchResult.FocusLost;
            token.ThrowIfCancellationRequested();
            if (!isForegroundAndTrusted()) return SteamConsoleInputDispatchResult.FocusLost;
            token.ThrowIfCancellationRequested();
            if (modifiersPressed()) return SteamConsoleInputDispatchResult.ModifiersPressed;
            token.ThrowIfCancellationRequested();
            return SteamConsoleInputDispatchResult.Sent;
        }
    }

    private static void Delay(CancellationToken token)
    {
        token.WaitHandle.WaitOne(50);
        token.ThrowIfCancellationRequested();
    }
}
