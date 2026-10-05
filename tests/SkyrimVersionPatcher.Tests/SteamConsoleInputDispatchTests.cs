using SkyrimVersionPatcher.App;

public static class SteamConsoleInputDispatchTests
{
    private const string Command = "download_depot 489830 489833 10";

    public static void Register(TestSuite suite)
    {
        suite.Add("Steam input dispatch waits for delayed focus and value acknowledgements", () =>
        {
            var field = new Field { Focused = false, FocusImmediately = false, SetImmediately = false };
            field.OnDelay = () =>
            {
                if (field.Delays == 2) field.Focused = true;
                if (field.Delays == 4) field.Value = Command;
            };
            Assert.Equal(SteamConsoleInputDispatchResult.Sent, field.Run());
            Assert.Equal(1, field.FocusCalls);
            Assert.Equal(1, field.SetCalls);
            Assert.Equal(1, field.EnterCalls);
            Assert.Equal(4, field.Delays);
        });
        suite.Add("Steam input dispatch cancellation prevents every subsequent callback", () =>
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var initial = new Field();
            Assert.Throws<OperationCanceledException>(() => initial.Run(cancelled.Token));
            Assert.Equal(0, initial.Callbacks);
            foreach (var waitingForFocus in new[] { true, false })
            {
                using var cancellation = new CancellationTokenSource();
                var field = new Field
                {
                    Focused = !waitingForFocus,
                    FocusImmediately = !waitingForFocus,
                    SetImmediately = false
                };
                field.OnDelay = cancellation.Cancel;
                Assert.Throws<OperationCanceledException>(() => field.Run(cancellation.Token));
                Assert.Equal(1, field.Delays);
                Assert.Equal(waitingForFocus ? 0 : 1, field.SetCalls);
                Assert.Equal(0, field.EnterCalls);
            }
        });
        suite.Add("Steam input dispatch bounds both acknowledgement waits", () =>
        {
            var focus = new Field { Focused = false, FocusImmediately = false };
            Assert.Equal(SteamConsoleInputDispatchResult.FocusUnavailable, focus.Run());
            Assert.Equal(3, focus.Delays);
            Assert.Equal(1, focus.FocusCalls);
            Assert.Equal(0, focus.SetCalls);
            var value = new Field { SetImmediately = false };
            Assert.Equal(SteamConsoleInputDispatchResult.ValueUnavailable, value.Run());
            Assert.Equal(3, value.Delays);
            Assert.Equal(1, value.SetCalls);
            Assert.Equal(0, value.EnterCalls);
        });
        suite.Add("Steam input dispatch rejects activation failure before requesting focus", () =>
        {
            var field = new Field { Activated = false };
            Assert.Equal(SteamConsoleInputDispatchResult.WindowUnavailable, field.Run());
            Assert.Equal(1, field.Callbacks);
            Assert.Equal(0, field.FocusCalls);
        });
        suite.Add("Steam input dispatch stops when foreground trust is lost during either wait", () =>
        {
            foreach (var waitingForFocus in new[] { true, false })
            {
                var field = new Field
                {
                    Focused = !waitingForFocus,
                    FocusImmediately = !waitingForFocus,
                    SetImmediately = false
                };
                field.OnDelay = () => field.ForegroundAndTrusted = false;
                Assert.Equal(SteamConsoleInputDispatchResult.FocusLost, field.Run());
                Assert.Equal(1, field.Delays);
                Assert.Equal(0, field.EnterCalls);
            }
        });
        suite.Add("Steam input dispatch stops when exact focus is lost during the value wait", () =>
        {
            var field = new Field { SetImmediately = false };
            field.OnDelay = () => field.Focused = false;
            Assert.Equal(SteamConsoleInputDispatchResult.FocusLost, field.Run());
            Assert.Equal(1, field.SetCalls);
            Assert.Equal(0, field.EnterCalls);
        });
        suite.Add("Steam input dispatch stops for modifiers before focus and during either wait", () =>
        {
            var initial = new Field { Modifiers = true };
            Assert.Equal(SteamConsoleInputDispatchResult.ModifiersPressed, initial.Run());
            Assert.Equal(0, initial.FocusCalls);
            foreach (var waitingForFocus in new[] { true, false })
            {
                var field = new Field
                {
                    Focused = !waitingForFocus,
                    FocusImmediately = !waitingForFocus,
                    SetImmediately = false
                };
                field.OnDelay = () => field.Modifiers = true;
                Assert.Equal(SteamConsoleInputDispatchResult.ModifiersPressed, field.Run());
                Assert.Equal(0, field.EnterCalls);
            }
        });
        suite.Add("Steam input dispatch rechecks the value immediately before Enter", () =>
        {
            var field = new Field();
            field.OnRead = () => field.ReadCalls == 1 ? Command : "other";
            Assert.Equal(SteamConsoleInputDispatchResult.ValueUnavailable, field.Run());
            Assert.Equal(2, field.ReadCalls);
            Assert.Equal(1, field.SetCalls);
            Assert.Equal(0, field.EnterCalls);
        });
        suite.Add("Steam input dispatch rechecks focus foreground modifiers and cancellation before Enter", () =>
        {
            foreach (var lost in new[] { "focus", "foreground", "modifiers", "cancellation" })
            {
                using var cancellation = new CancellationTokenSource();
                var field = new Field();
                field.OnRead = () =>
                {
                    if (field.ReadCalls == 2)
                    {
                        if (lost == "focus") field.Focused = false;
                        if (lost == "foreground") field.ForegroundAndTrusted = false;
                        if (lost == "modifiers") field.Modifiers = true;
                        if (lost == "cancellation") cancellation.Cancel();
                    }
                    return Command;
                };
                if (lost == "cancellation")
                    Assert.Throws<OperationCanceledException>(() => field.Run(cancellation.Token));
                else
                    Assert.Equal(lost == "modifiers" ? SteamConsoleInputDispatchResult.ModifiersPressed :
                        SteamConsoleInputDispatchResult.FocusLost, field.Run(cancellation.Token));
                Assert.Equal(0, field.EnterCalls);
            }
        });
        suite.Add("Steam input dispatch propagates an ambiguous Enter exception without retry or callbacks", () =>
        {
            var error = new InvalidOperationException("One Enter event may already have been submitted.");
            var field = new Field { EnterError = error };
            Assert.True(ReferenceEquals(error, Assert.Throws<InvalidOperationException>(() => field.Run())));
            Assert.Equal(1, field.EnterCalls);
            Assert.Equal(1, field.SetCalls);
        });
        suite.Add("Steam input dispatch makes no callbacks after a successful Enter", () =>
        {
            var field = new Field();
            Assert.Equal(SteamConsoleInputDispatchResult.Sent, field.Run());
            Assert.Equal(1, field.EnterCalls);
        });
        suite.Add("Steam input dispatch validates command before every callback", () =>
        {
            var field = new Field();
            foreach (var command in new[] { "quit", Command + "\nquit", "download_depot 489830 489833 0" })
                Assert.Throws<ArgumentException>(() => field.Run(command: command));
            Assert.Equal(0, field.Callbacks);
        });
    }

    private sealed class Field
    {
        public bool Activated = true, ForegroundAndTrusted = true, Focused = true;
        public bool FocusImmediately = true, SetImmediately = true, Modifiers;
        public string? Value;
        public int Callbacks, FocusCalls, SetCalls, EnterCalls, Delays, ReadCalls;
        public Action? OnDelay;
        public Func<string?>? OnRead;
        public Exception? EnterError;
        private bool submitted;

        public SteamConsoleInputDispatchResult Run(CancellationToken token = default, string command = Command) =>
            SteamConsoleInputDispatch.Run(command,
                () => { Callback(); return Activated; },
                () => { Callback(); FocusCalls++; if (FocusImmediately) Focused = true; },
                () => { Callback(); return ForegroundAndTrusted; },
                () => { Callback(); return Focused; },
                () => { Callback(); ReadCalls++; return OnRead is null ? Value : OnRead(); },
                value => { Callback(); SetCalls++; if (SetImmediately) Value = value; },
                () => { Callback(); return Modifiers; },
                () => { Callback(); EnterCalls++; submitted = true; if (EnterError is not null) throw EnterError; },
                token,
                cancellation => { Callback(); Delays++; OnDelay?.Invoke(); cancellation.ThrowIfCancellationRequested(); },
                maxPolls: 3);

        private void Callback()
        {
            Assert.True(!submitted, "No callback may run after submitting Enter.");
            Callbacks++;
        }
    }
}
