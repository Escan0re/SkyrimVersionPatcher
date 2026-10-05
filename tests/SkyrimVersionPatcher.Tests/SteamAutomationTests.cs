using SkyrimVersionPatcher.App;
using SkyrimVersionPatcher.Core.Downloading;

public static class SteamAutomationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Steam console identity requires one console CSS module", () =>
        {
            const string source = "e.exports={Console:\"console-abc\",SpewLine:\"spew-abc\",ConsoleInput:\"form-abc\",InputBox:\"input-abc\"}";
            Assert.Equal(new SteamConsoleCssClasses("console-abc", "form-abc", "input-abc"),
                SteamConsoleIdentity.ExtractClasses(source).Single());
            Assert.Equal(0, SteamConsoleIdentity.ExtractClasses(source.Replace("SpewLine", "OtherProp")).Count);
            Assert.Equal(0, SteamConsoleIdentity.ExtractClasses(source.Replace("ConsoleInput:\"form-abc\",",
                "}x.exports={ConsoleInput:\"form-abc\",", StringComparison.Ordinal)).Count);
        });
        suite.Add("Steam console class tokens cannot match an unrelated edit", () =>
        {
            Assert.True(SteamConsoleIdentity.HasClass("other input-abc next", "input-abc"));
            Assert.True(!SteamConsoleIdentity.HasClass("input-abc-other", "input-abc"));
            Assert.True(!SteamConsoleIdentity.HasClass("INPUT-ABC", "input-abc"));
        });
        suite.Add("Steam console route requires its exact internal HTTPS origin", () =>
        {
            Assert.True(SteamConsoleIdentity.IsConsoleRoute("https://steamloopback.host/routes/console"));
            Assert.True(SteamConsoleIdentity.IsConsoleRoute("https://steamloopback.host/console/"));
            foreach (var url in new[] { "https://steamloopback.host.evil/routes/console", "https://steamloopback.host/routes/login",
                         "http://steamloopback.host/console", "https://user@steamloopback.host/console",
                         "https://steamloopback.host:4444/console", "https://steamloopback.host/routes/console/other" })
                Assert.True(!SteamConsoleIdentity.IsConsoleRoute(url));
        });
        suite.Add("Steam shell document identity requires its exact internal HTTPS index path", () =>
        {
            foreach (var url in new[] { "https://steamloopback.host/index.html",
                         "https://STEAMLOOPBACK.HOST:443/index.html?IN_CLIENT=1#console" })
            {
                Assert.True(SteamConsoleIdentity.IsSteamClientDocument(url));
                Assert.True(!SteamConsoleIdentity.IsConsoleRoute(url));
            }
            foreach (var url in new string?[] { null, "", "index.html", "https://steamloopback.host.evil/index.html",
                         "https://evil.example/index.html", "http://steamloopback.host/index.html",
                         "https://user:password@steamloopback.host/index.html", "https://steamloopback.host:4444/index.html",
                         "https://steamloopback.host/index.HTML", "https://steamloopback.host/index.html/",
                         "https://steamloopback.host/index.html/other", "https://steamloopback.host/other/index.html",
                         "https://steamloopback.host/routes/login", "https://steamloopback.host/console",
                         "https://steamloopback.host/routes/console" })
                Assert.True(!SteamConsoleIdentity.IsSteamClientDocument(url));
            Assert.True(SteamConsoleIdentity.IsConsoleRoute("https://steamloopback.host/routes/console"));
        });
        suite.Add("Steam console preparation uses existing trusted debug transport without UI changes", async () =>
        {
            var environment = new FakeEnvironment(Session(100));
            environment.DebugResults.Enqueue(true);
            Assert.Equal(100, (await new SteamConsolePreparer(environment).PrepareAsync(Session(100), default)).ProcessId);
            Assert.Equal(1, environment.DebugCalls);
            Assert.Equal(0, environment.OpenCalls);
            Assert.Equal(0, environment.AccessibilityCalls);
        });
        suite.Add("Steam console preparation uses accessibility in the same process", async () =>
        {
            var environment = new FakeEnvironment(Session(100)) { AccessibilityResult = true };
            Assert.Equal(100, (await new SteamConsolePreparer(environment).PrepareAsync(Session(100), default)).ProcessId);
            Assert.Equal(1, environment.DebugCalls);
            Assert.Equal(1, environment.OpenCalls);
            Assert.Equal(1, environment.AccessibilityCalls);
        });
        suite.Add("Steam inaccessible console can use existing debug transport after opening", async () =>
        {
            var environment = new FakeEnvironment(Session(100));
            environment.DebugResults.Enqueue(false);
            environment.DebugResults.Enqueue(true);
            Assert.Equal(100, (await new SteamConsolePreparer(environment).PrepareAsync(Session(100), default)).ProcessId);
            Assert.Equal(2, environment.DebugCalls);
            Assert.Equal(1, environment.OpenCalls);
            Assert.Equal(1, environment.AccessibilityCalls);
        });
        suite.Add("Steam UI access denied still tries existing debug transport", async () =>
        {
            foreach (var error in new Exception[] { new UnauthorizedAccessException(),
                         new SteamConsoleUnavailableException("Windows не разрешила доступ к Steam Console.") })
            {
                var environment = new FakeEnvironment(Session(100)) { AccessibilityError = error };
                environment.DebugResults.Enqueue(false);
                environment.DebugResults.Enqueue(true);
                Assert.Equal(100, (await new SteamConsolePreparer(environment).PrepareAsync(Session(100), default)).ProcessId);
                Assert.Equal(2, environment.DebugCalls);
                Assert.Equal(1, environment.OpenCalls);
            }
        });
        suite.Add("Steam unavailable transports report failure in existing client", async () =>
        {
            var environment = new FakeEnvironment(Session(100));
            var error = await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() =>
                new SteamConsolePreparer(environment).PrepareAsync(Session(100), default));
            Assert.True(error.Message.Contains("Steam не перезапускался", StringComparison.Ordinal));
            Assert.Equal(100, environment.Current.ProcessId);
            Assert.Equal(2, environment.DebugCalls);
            Assert.Equal(1, environment.OpenCalls);
            Assert.Equal(1, environment.AccessibilityCalls);
        });
        suite.Add("Steam console opening access denied can still use an existing debug connection", async () =>
        {
            var environment = new FakeEnvironment(Session(100)) { DuringOpen = _ => throw new UnauthorizedAccessException() };
            environment.DebugResults.Enqueue(false);
            environment.DebugResults.Enqueue(true);
            Assert.Equal(100, (await new SteamConsolePreparer(environment).PrepareAsync(Session(100), default)).ProcessId);
            Assert.Equal(2, environment.DebugCalls);
            Assert.Equal(1, environment.OpenCalls);
            Assert.Equal(0, environment.AccessibilityCalls);
        });
        suite.Add("Steam access diagnostics survive when existing transports are unavailable", async () =>
        {
            var denied = new SteamConsoleUnavailableException("Windows не разрешила доступ. Steam и патчер должны работать с одинаковыми правами.");
            var environment = new FakeEnvironment(Session(100)) { AccessibilityError = denied };
            var actual = await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() =>
                new SteamConsolePreparer(environment).PrepareAsync(Session(100), default));
            Assert.True(ReferenceEquals(denied, actual));
            Assert.Equal(2, environment.DebugCalls);
        });
        suite.Add("Steam preparation rejects changed process account and installation before UI actions", async () =>
        {
            foreach (var changed in new[] { Session(101), Session(100) with { ActiveUser = 0 }, Session(100) with { ActiveUser = 999 },
                         Session(100) with { ExecutablePath = Path.Combine(Path.GetTempPath(), "other-steam", "steam.exe") },
                         Session(100) with { SteamDirectory = Path.Combine(Path.GetTempPath(), "other-steam") } })
            {
                var environment = new FakeEnvironment(changed);
                await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() =>
                    new SteamConsolePreparer(environment).PrepareAsync(Session(100), default));
                Assert.Equal(0, environment.DebugCalls);
                Assert.Equal(0, environment.OpenCalls);
                Assert.Equal(0, environment.AccessibilityCalls);
            }
        });
        suite.Add("Steam changes during connection cannot authorize accessibility actions", async () =>
        {
            foreach (var connected in new[] { true, false })
            {
                var environment = new FakeEnvironment(Session(100)) { DuringDebug = value => value.Current = Session(101) };
                environment.DebugResults.Enqueue(connected);
                await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() =>
                    new SteamConsolePreparer(environment).PrepareAsync(Session(100), default));
                Assert.Equal(0, environment.OpenCalls);
                Assert.Equal(0, environment.AccessibilityCalls);
            }
        });
        suite.Add("Steam process change while opening console prevents input discovery", async () =>
        {
            var environment = new FakeEnvironment(Session(100)) { DuringOpen = value => value.Current = Session(101) };
            await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() =>
                new SteamConsolePreparer(environment).PrepareAsync(Session(100), default));
            Assert.Equal(1, environment.OpenCalls);
            Assert.Equal(0, environment.AccessibilityCalls);
            Assert.Equal(1, environment.DebugCalls);
        });
        suite.Add("Steam session changes during UI preparation cannot be hidden by access errors", async () =>
        {
            var environment = new FakeEnvironment(Session(100))
            {
                DuringAccessibility = value => value.Current = Session(101), AccessibilityError = new UnauthorizedAccessException()
            };
            var error = await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() =>
                new SteamConsolePreparer(environment).PrepareAsync(Session(100), default));
            Assert.True(error.Message.Contains("Процесс Steam изменился", StringComparison.Ordinal));
            Assert.Equal(1, environment.DebugCalls);
        });
        suite.Add("Steam preparation preserves non-access failures without fallback", async () =>
        {
            var failure = new SteamConsoleUnavailableException("Steam identity no longer trusted");
            var environment = new FakeEnvironment(Session(100)) { AccessibilityError = failure };
            var error = await Assert.ThrowsAsync<SteamConsoleUnavailableException>(() =>
                new SteamConsolePreparer(environment).PrepareAsync(Session(100), default));
            Assert.True(ReferenceEquals(failure, error));
            Assert.Equal(1, environment.DebugCalls);
        });
        suite.Add("Steam preparation cancelled before work has no side effects", async () =>
        {
            var environment = new FakeEnvironment(Session(100));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new SteamConsolePreparer(environment).PrepareAsync(Session(100), cancellation.Token));
            Assert.Equal(0, environment.DebugCalls);
            Assert.Equal(0, environment.OpenCalls);
        });
        suite.Add("Steam preparation cancellation after status update prevents console opening", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var environment = new FakeEnvironment(Session(100));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new SteamConsolePreparer(environment, _ => cancellation.Cancel()).PrepareAsync(Session(100), cancellation.Token));
            Assert.Equal(1, environment.DebugCalls);
            Assert.Equal(0, environment.OpenCalls);
            Assert.Equal(0, environment.AccessibilityCalls);
        });
        suite.Add("Steam preparation cancelled during UI discovery stops further transport attempts", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var environment = new FakeEnvironment(Session(100)) { DuringAccessibility = _ => cancellation.Cancel() };
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new SteamConsolePreparer(environment).PrepareAsync(Session(100), cancellation.Token));
            Assert.Equal(1, environment.DebugCalls);
            Assert.Equal(1, environment.OpenCalls);
            Assert.Equal(1, environment.AccessibilityCalls);
            Assert.Equal(100, environment.Current.ProcessId);
        });
    }

    private static SteamClientSession Session(int pid)
    {
        var root = Path.Combine(Path.GetTempPath(), "steam-automation-test", "Steam");
        return new(root, Path.Combine(root, "steam.exe"), pid, 123, true);
    }

    private sealed class FakeEnvironment(SteamClientSession session) : ISteamConsoleEnvironment
    {
        public SteamClientSession Current { get; set; } = session;
        public Queue<bool> DebugResults { get; } = [];
        public bool AccessibilityResult { get; init; }
        public Exception? AccessibilityError { get; init; }
        public Action<FakeEnvironment>? DuringDebug { get; init; }
        public Action<FakeEnvironment>? DuringOpen { get; init; }
        public Action<FakeEnvironment>? DuringAccessibility { get; init; }
        public int DebugCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public int AccessibilityCalls { get; private set; }
        public SteamClientSession GetSession() => Current;
        public Task<bool> TryConnectExistingDebugAsync(SteamClientSession expected, CancellationToken token)
        {
            DebugCalls++;
            DuringDebug?.Invoke(this);
            return Task.FromResult(DebugResults.TryDequeue(out var result) && result);
        }
        public void OpenConsole(SteamClientSession expected) { OpenCalls++; DuringOpen?.Invoke(this); }
        public Task<bool> TryPrepareAccessibilityAsync(SteamClientSession expected, CancellationToken token)
        {
            AccessibilityCalls++;
            DuringAccessibility?.Invoke(this);
            return AccessibilityError is null ? Task.FromResult(AccessibilityResult) : Task.FromException<bool>(AccessibilityError);
        }
    }
}
