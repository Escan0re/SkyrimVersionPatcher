using SkyrimVersionPatcher.App;
using SkyrimVersionPatcher.Core.Downloading;

public static class SteamConsoleTransportFallbackTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Steam dispatch can recover when the prepared UI Automation provider disappears", () =>
        {
            var attempts = new List<SteamConsoleTransport>();
            var selected = SteamConsoleTransportFallback.TryDispatch(SteamConsoleTransport.UiAutomation,
                () => { attempts.Add(SteamConsoleTransport.UiAutomation); return false; },
                () => { attempts.Add(SteamConsoleTransport.Msaa); return true; }, default);
            Assert.Equal<SteamConsoleTransport?>(SteamConsoleTransport.Msaa, selected);
            Assert.Equal(2, attempts.Count);
            Assert.Equal(SteamConsoleTransport.UiAutomation, attempts[0]);
            Assert.Equal(SteamConsoleTransport.Msaa, attempts[1]);
        });
        suite.Add("Steam dispatch can recover when the prepared MSAA provider disappears", () =>
        {
            var attempts = new List<SteamConsoleTransport>();
            var selected = SteamConsoleTransportFallback.TryDispatch(SteamConsoleTransport.Msaa,
                () => { attempts.Add(SteamConsoleTransport.UiAutomation); return true; },
                () => { attempts.Add(SteamConsoleTransport.Msaa); return false; }, default);
            Assert.Equal<SteamConsoleTransport?>(SteamConsoleTransport.UiAutomation, selected);
            Assert.Equal(2, attempts.Count);
            Assert.Equal(SteamConsoleTransport.Msaa, attempts[0]);
            Assert.Equal(SteamConsoleTransport.UiAutomation, attempts[1]);
        });
        suite.Add("Steam successful dispatch never invokes another provider or checks cancellation afterward", () =>
        {
            foreach (var preferred in new[] { SteamConsoleTransport.UiAutomation, SteamConsoleTransport.Msaa })
            {
                using var cancellation = new CancellationTokenSource();
                var calls = 0;
                bool Submit()
                {
                    calls++;
                    cancellation.Cancel(); // A submitted Enter remains successful even if cancellation arrives now.
                    return true;
                }
                bool Unexpected() => throw new InvalidOperationException("Another provider cannot run after Enter.");
                var selected = SteamConsoleTransportFallback.TryDispatch(preferred,
                    preferred == SteamConsoleTransport.UiAutomation ? Submit : Unexpected,
                    preferred == SteamConsoleTransport.Msaa ? Submit : Unexpected, cancellation.Token);
                Assert.Equal<SteamConsoleTransport?>(preferred, selected);
                Assert.Equal(1, calls);
            }
        });
        suite.Add("Steam dispatch cancellation before work prevents both providers", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var calls = 0;
            bool Attempt() { calls++; return false; }
            Assert.Throws<OperationCanceledException>(() => SteamConsoleTransportFallback.TryDispatch(
                SteamConsoleTransport.UiAutomation, Attempt, Attempt, cancellation.Token));
            Assert.Equal(0, calls);
        });
        suite.Add("Steam dispatch cancellation after an unavailable provider prevents fallback", () =>
        {
            foreach (var preferred in new[] { SteamConsoleTransport.UiAutomation, SteamConsoleTransport.Msaa })
            {
                using var cancellation = new CancellationTokenSource();
                var calls = 0;
                bool Unavailable() { calls++; cancellation.Cancel(); return false; }
                bool Unexpected() => throw new InvalidOperationException("Cancellation must prevent fallback.");
                Assert.Throws<OperationCanceledException>(() => SteamConsoleTransportFallback.TryDispatch(preferred,
                    preferred == SteamConsoleTransport.UiAutomation ? Unavailable : Unexpected,
                    preferred == SteamConsoleTransport.Msaa ? Unavailable : Unexpected, cancellation.Token));
                Assert.Equal(1, calls);
            }
        });
        suite.Add("Steam uncertain Enter errors propagate without attempting another provider", () =>
        {
            foreach (var preferred in new[] { SteamConsoleTransport.UiAutomation, SteamConsoleTransport.Msaa })
            {
                var error = new SteamConsoleUnavailableException("One Enter event may already have been submitted.");
                var submitted = 0;
                bool Submit() { submitted++; throw error; }
                bool Unexpected() => throw new InvalidOperationException("An uncertain send cannot trigger fallback.");
                var actual = Assert.Throws<SteamConsoleUnavailableException>(() => SteamConsoleTransportFallback.TryDispatch(preferred,
                    preferred == SteamConsoleTransport.UiAutomation ? Submit : Unexpected,
                    preferred == SteamConsoleTransport.Msaa ? Submit : Unexpected, default));
                Assert.True(ReferenceEquals(error, actual));
                Assert.Equal(1, submitted);
            }
        });
        suite.Add("Steam dispatch preserves a session guard failure without fallback", () =>
        {
            var error = new SteamConsoleUnavailableException("The Steam process or account changed before input.");
            var fallbackCalls = 0;
            var actual = Assert.Throws<SteamConsoleUnavailableException>(() => SteamConsoleTransportFallback.TryDispatch(
                SteamConsoleTransport.UiAutomation, () => throw error,
                () => { fallbackCalls++; return true; }, default));
            Assert.True(ReferenceEquals(error, actual));
            Assert.Equal(0, fallbackCalls);
        });
        suite.Add("Steam dispatch reports no selected provider when both fields are unavailable", () =>
        {
            foreach (var preferred in new[] { SteamConsoleTransport.UiAutomation, SteamConsoleTransport.Msaa })
            {
                var calls = 0;
                bool Unavailable() { calls++; return false; }
                Assert.Equal<SteamConsoleTransport?>(null, SteamConsoleTransportFallback.TryDispatch(
                    preferred, Unavailable, Unavailable, default));
                Assert.Equal(2, calls);
            }
        });
    }
}
