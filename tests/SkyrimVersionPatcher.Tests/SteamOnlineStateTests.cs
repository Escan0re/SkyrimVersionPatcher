using System.Text;
using SkyrimVersionPatcher.Core.Downloading;

public static class SteamOnlineStateTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Steam online state ignores an inactive network adapter disconnect", () =>
        {
            using var log = new ConnectionLog("""
                [2026-10-03 21:32:45] [Logged On,4,7] LogOnResponse() processing complete.
                [2026-10-03 22:01:51] CCMInterface::OnNetworkDeviceStateChange -- Lost device (Connected -> Disconnected)
                [2026-10-03 22:01:51] Lost device does not match current connection, ignoring
                [2026-10-03 22:03:53] Already connected, ignoring new network device
                """);
            Assert.Equal<bool?>(true, SteamClientLocator.ReadOnlineState(log.Path));
        });
        suite.Add("Steam online state does not infer a session from unrelated log text", () =>
        {
            foreach (var text in new[]
            {
                "[2026-10-03 22:01:51] CCMInterface::OnNetworkDeviceStateChange -- Lost device (Connected -> Disconnected)",
                "[2026-10-03 22:01:51] [0,0] LogOnResponse() [OK]",
                "[2026-10-03 22:01:51] [Not Logged On,0,0] Offline mode",
                "LogOff()",
                "Logged On"
            })
            {
                using var log = new ConnectionLog(text);
                Assert.Equal<bool?>(null, SteamClientLocator.ReadOnlineState(log.Path));
            }
        });
        suite.Add("Steam online state recognizes authenticated CM state without a special message", () =>
        {
            using var log = new ConnectionLog("[2026-10-03 21:32:45] [Logged On,4,7] Heartbeat completed.");
            Assert.Equal<bool?>(true, SteamClientLocator.ReadOnlineState(log.Path));
        });
        suite.Add("Steam online state recognizes explicit client disconnect and logoff", () =>
        {
            foreach (var state in new[]
            {
                "[Logged On,4,7] ConnectionDisconnected(reason)",
                "[Logged On,4,7] LogOff(reason)",
                "[Logged Off,0,0] Completed",
                "[Logging Off,4,7] Requested",
                "[Offline,0,0] Client ready",
                "[Connection] Disconnected",
                "[Connection] Offline mode enabled"
            })
            {
                using var log = new ConnectionLog("[Connection] Logged On\n" + state);
                Assert.Equal<bool?>(false, SteamClientLocator.ReadOnlineState(log.Path));
            }
        });
        suite.Add("Steam online state clears previous offline state while reconnecting", () =>
        {
            foreach (var state in new[] { "Disconnected", "Connecting", "Connected", "Logging On" })
            {
                using var log = new ConnectionLog($"[Logged Off,0,0] Stopped\n[{state},1,1] Starting connection");
                Assert.Equal<bool?>(null, SteamClientLocator.ReadOnlineState(log.Path));
            }
        });
        suite.Add("Steam online state recognizes only successful exact logon results", () =>
        {
            foreach (var result in new[] { "[OK]", "'OK'" })
            {
                using var log = new ConnectionLog($"[Logging On,1,1] LogOnResponse({result})");
                Assert.Equal<bool?>(true, SteamClientLocator.ReadOnlineState(log.Path));
            }
            foreach (var result in new[] { "[Fail]", "'OKAY'", "[NOT OK]", "OK" })
            {
                using var log = new ConnectionLog($"[Logging On,1,1] LogOnResponse({result})");
                Assert.Equal<bool?>(null, SteamClientLocator.ReadOnlineState(log.Path));
            }
        });
        suite.Add("Steam online state accepts successful reconnect after an explicit disconnect", () =>
        {
            using var log = new ConnectionLog("""
                [2026-10-03 21:32:45] [Logged On,4,7] ConnectionDisconnected(reason)
                [2026-10-03 21:32:46] [Connecting,1,1] Trying connection
                [2026-10-03 21:32:47] [Logging On,1,1] LogOnResponse('OK')
                """);
            Assert.Equal<bool?>(true, SteamClientLocator.ReadOnlineState(log.Path));
        });
        suite.Add("Steam online state parses actual CM events and ignores mentions of event names", () =>
        {
            using var log = new ConnectionLog("[Logging On,4,7] [U:1:123] RecvMsgClientLogOnResponse() : 'OK'");
            Assert.Equal<bool?>(true, SteamClientLocator.ReadOnlineState(log.Path));
            File.AppendAllText(log.Path, "\n[Logged On,4,7] [U:1:123] LogOff()\n");
            Assert.Equal<bool?>(false, SteamClientLocator.ReadOnlineState(log.Path));
            foreach (var message in new[] { "Mention of LogOnResponse with 'OK'", "Previous LogOnResponse('OK')" })
            {
                using var mention = new ConnectionLog("[Logging On,4,7] " + message);
                Assert.Equal<bool?>(null, SteamClientLocator.ReadOnlineState(mention.Path));
            }
            using var priorDisconnect = new ConnectionLog("[Logged On,4,7] Previous ConnectionDisconnected() event");
            Assert.Equal<bool?>(true, SteamClientLocator.ReadOnlineState(priorDisconnect.Path));
        });
        suite.Add("Steam online state excludes records from an earlier client process", () =>
        {
            using var log = new ConnectionLog("""
                [2026-10-03 21:32:44] [Logged On,4,7] ConnectionDisconnected(reason)
                [2026-10-03 21:32:45] [Connected,1,1] Starting connection
                """);
            Assert.Equal<bool?>(null, SteamClientLocator.ReadOnlineState(log.Path, new DateTime(2026, 10, 3, 21, 32, 45)));
        });
        suite.Add("Steam online state includes a startup event in the process start second", () =>
        {
            using var log = new ConnectionLog("[2026-10-03 21:32:45] [Logged On,4,7] Ready");
            Assert.Equal<bool?>(true, SteamClientLocator.ReadOnlineState(log.Path, new DateTime(2026, 10, 3, 21, 32, 45, 750)));
        });
        suite.Add("Steam online state requires valid recent timestamps for a known client process", () =>
        {
            foreach (var prefix in new[] { "", "[invalid] ", "[2026-99-03 21:32:45] ", "[2026-10-03 21:32:44] " })
            {
                using var log = new ConnectionLog(prefix + "[Offline,0,0] Client ready");
                Assert.Equal<bool?>(null, SteamClientLocator.ReadOnlineState(log.Path, new DateTime(2026, 10, 3, 21, 32, 45)));
            }
        });
        suite.Add("Steam online state ignores a truncated first tail line", () =>
        {
            const int tailLength = 128 * 1024;
            const string fakeEvent = "[Connection] Disconnected\n";
            using var log = new ConnectionLog(new string('x', tailLength) + fakeEvent +
                new string('x', tailLength - fakeEvent.Length));
            Assert.Equal<bool?>(null, SteamClientLocator.ReadOnlineState(log.Path));
        });
        suite.Add("Steam online state retains a complete first line exactly at the tail boundary", () =>
        {
            const int tailLength = 128 * 1024;
            const string firstEvent = "[Connection] Logged On\n";
            using var log = new ConnectionLog(new string('x', tailLength - 1) + "\n" + firstEvent +
                new string('x', tailLength - firstEvent.Length));
            Assert.Equal<bool?>(true, SteamClientLocator.ReadOnlineState(log.Path));
        });
    }

    private sealed class ConnectionLog : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "svpatcher-connection-" + Guid.NewGuid().ToString("N") + ".txt");

        public ConnectionLog(string text) => File.WriteAllText(Path, text, new UTF8Encoding(false));
        public void Dispose() => File.Delete(Path);
    }
}
