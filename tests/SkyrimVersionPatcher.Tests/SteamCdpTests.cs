using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SkyrimVersionPatcher.App;

public static class SteamCdpTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Steam CDP only accepts Skyrim depot commands", () =>
        {
            SteamCdpTransport.ValidateCommand("download_depot 489830 489831 8442952117333549665");
            SteamCdpTransport.ValidateCommand("download_depot 489830 489838 3873588632592923754");
            foreach (var depot in SkyrimVersionPatcher.Core.Catalog.GameLocalizationCatalog.All.Where(language => language.DepotId is not null))
                SteamCdpTransport.ValidateCommand($"download_depot 489830 {depot.DepotId} 1");
            foreach (var command in new[]
            {
                "download_depot 570 489831 1", "download_depot 489830 489841 1", "download_depot 489830 489831 0",
                "download_depot 489830 489831 18446744073709551616", "download_depot 489830 489831 01",
                "download_depot 489830 489831 1\nquit", "download_depot 489830 489831 1; quit", "quit"
            }) Assert.Throws<ArgumentException>(() => SteamCdpTransport.ValidateCommand(command));
        });

        suite.Add("Steam CDP rejects remote, arbitrary, and misleading targets", () =>
        {
            var trusted = Target("SharedJSContext", "https://steamloopback.host/routes/", "ws://localhost:8080/devtools/page/1");
            var targets = new[]
            {
                trusted,
                Target("SharedJSContext", "https://steamloopback.host.evil.example/routes/", "ws://localhost:8080/devtools/page/2"),
                Target("SharedJSContext", "https://steamloopback.host/routes/", "ws://remote.example:8080/devtools/page/3"),
                Target("SharedJSContext", "https://steamloopback.host/routes/", "ws://localhost:9222/devtools/page/4"),
                Target("Login", "https://steamloopback.host/routes/", "ws://localhost:8080/devtools/page/5"),
                Target("SharedJSContext", "https://steamloopback.host/login", "ws://localhost:8080/devtools/page/6"),
                Target("SharedJSContext", "https://user@steamloopback.host/routes/", "ws://localhost:8080/devtools/page/7"),
                Target("SharedJSContext", "https://steamloopback.host/routes/", "ws://localhost:8080/json/list")
            };
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(targets));
            var endpoints = SteamCdpTransport.FindSharedEndpoints(document.RootElement);
            Assert.Equal(1, endpoints.Count);
            Assert.Equal("ws://127.0.0.1:8080/devtools/page/1", endpoints[0].AbsoluteUri);
        });

        suite.Add("Steam CDP requires the exact helper name inside the Steam installation", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "steam-cdp-path-test", "Steam");
            Assert.True(SteamCdpListener.IsSteamHelperPath(Path.Combine(root, "bin", "cef", "steamwebhelper.exe"), root));
            Assert.True(!SteamCdpListener.IsSteamHelperPath(Path.Combine(root + "Other", "steamwebhelper.exe"), root));
            Assert.True(!SteamCdpListener.IsSteamHelperPath(Path.Combine(root, "bin", "Steam.exe"), root));
            Assert.True(!SteamCdpListener.IsSteamHelperPath(Path.Combine(root, "..", "Other", "steamwebhelper.exe"), root));
        });

        suite.Add("Steam CDP process ownership requires ancestry and the patcher's Windows session", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "steam-cdp-process-test", "Steam");
            var executable = Path.Combine(root, "steam.exe");
            var helper = Path.Combine(root, "bin", "cef", "steamwebhelper.exe");
            var parents = new Dictionary<int, int> { [200] = 201, [201] = 100 };
            var metadata = new Dictionary<int, SteamCdpListener.ProcessMetadata>
            {
                [100] = new(4, executable, 1000), [201] = new(4, helper, 2000), [200] = new(4, helper, 3000)
            };
            bool Trusted(uint currentSession = 4) => SteamCdpListener.IsTrustedProcessGraph(200, 100, currentSession,
                executable, root, parents, pid => metadata.GetValueOrDefault(pid));
            Assert.True(Trusted());
            Assert.True(!Trusted(5)); // Steam is in a different Windows logon session than the patcher.
            metadata[200] = new(5, helper, 3000);
            Assert.True(!Trusted()); // The listening helper is in a different Windows logon session.
            metadata[200] = new(4, helper, 3000);
            metadata[201] = new(5, helper, 2000);
            Assert.True(!Trusted()); // An intermediate parent cannot cross sessions either.
            metadata[201] = new(4, helper, 2000);
            parents[201] = 500;
            metadata[500] = new(4, executable, 1000);
            Assert.True(!Trusted()); // Another client using the same installed Steam.exe is unrelated.
        });

        suite.Add("Steam CDP rejects stale, missing, cyclic and unexpected process parents", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "steam-cdp-process-test", "Steam");
            var executable = Path.Combine(root, "steam.exe");
            var helper = Path.Combine(root, "bin", "cef", "steamwebhelper.exe");
            var parents = new Dictionary<int, int> { [200] = 201, [201] = 100 };
            var metadata = new Dictionary<int, SteamCdpListener.ProcessMetadata>
            {
                [100] = new(4, executable, 1000), [201] = new(4, helper, 2000), [200] = new(4, helper, 3000)
            };
            bool Trusted() => SteamCdpListener.IsTrustedProcessGraph(200, 100, 4,
                executable, root, parents, pid => metadata.GetValueOrDefault(pid));
            metadata[201] = new(4, helper, 4000);
            Assert.True(!Trusted()); // A reused parent PID was created after its alleged child.
            metadata[201] = new(4, helper, 2000);
            metadata.Remove(200);
            Assert.True(!Trusted()); // The listening process must still be live and queryable.
            metadata[200] = new(4, helper, 2000);
            parents[201] = 200;
            Assert.True(!Trusted());
            parents[201] = 100;
            metadata[201] = new(4, Path.Combine(root, "SteamService.exe"), 2000);
            Assert.True(!Trusted()); // An unconfirmed service/launcher path is not a trusted ancestor.
            metadata[201] = new(4, helper, 2000);
            parents.Remove(201);
            Assert.True(!Trusted());
        });

        suite.Add("Steam CDP process ancestry traversal is bounded", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "steam-cdp-process-test", "Steam");
            var executable = Path.Combine(root, "steam.exe");
            var helper = Path.Combine(root, "bin", "cef", "steamwebhelper.exe");
            var parents = new Dictionary<int, int>();
            var metadata = new Dictionary<int, SteamCdpListener.ProcessMetadata> { [100] = new(4, executable, 1000) };
            for (var pid = 200; pid < 240; pid++)
            {
                parents[pid] = pid == 239 ? 100 : pid + 1;
                metadata[pid] = new(4, helper, 4000 - pid);
            }
            var reads = 0;
            Assert.True(!SteamCdpListener.IsTrustedProcessGraph(200, 100, 4, executable, root, parents, pid =>
            {
                reads++;
                return metadata.GetValueOrDefault(pid);
            }));
            Assert.True(reads <= 33);
        });

        suite.Add("Steam CDP Windows metadata snapshot reads the current process without memory access", () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var readParents = typeof(SteamCdpListener).GetMethod("ReadParentProcessIds", flags)!;
            var parents = (IReadOnlyDictionary<int, int>?)readParents.Invoke(null, null);
            Assert.True(parents?.ContainsKey(Environment.ProcessId) == true);
            var readMetadata = typeof(SteamCdpListener).GetMethod("ReadLiveMetadata", flags)!;
            var metadata = (SteamCdpListener.ProcessMetadata?)readMetadata.Invoke(null, [Environment.ProcessId]);
            Assert.True(metadata is { CreationTime: > 0 });
            Assert.True(metadata!.ExecutablePath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
        });

        suite.Add("Steam CDP registers before dispatch and preserves fragmented Cyrillic output", async () =>
        {
            var socket = new FakeSocket((id, expression) =>
                expression.Contains("return s.text", StringComparison.Ordinal)
                    ? Response(id, "Депо скачано: D:\\Steam\\steamapps\\content\n") : Response(id, true), fragmentSize: 7);
            await using var transport = new SteamCdpTransport(socket, "__test_collector");
            Assert.True(await transport.InstallCollectorAsync(CancellationToken.None));
            await transport.DispatchAsync("download_depot 489830 489831 8442952117333549665", CancellationToken.None);
            Assert.Equal("Депо скачано: D:\\Steam\\steamapps\\content\n", await transport.ReadAsync(CancellationToken.None));
            Assert.True(socket.Expressions[0].Contains("RegisterForSpewOutput", StringComparison.Ordinal));
            Assert.True(socket.Expressions[0].Contains(".spew", StringComparison.Ordinal));
            Assert.True(socket.Expressions[0].Contains(".slice(-524288)", StringComparison.Ordinal));
            Assert.True(socket.Expressions[1].Contains("SteamClient.Console.ExecCommand(\"download_depot 489830 489831 8442952117333549665\")", StringComparison.Ordinal));
            Assert.True(socket.Expressions.All(expression => !expression.Contains("Auth", StringComparison.Ordinal)));
        });

        suite.Add("Steam CDP correlates replies and serializes socket reads", async () =>
        {
            var socket = new FakeSocket((id, _) =>
                "{\"method\":\"Runtime.executionContextCreated\",\"params\":{}}\n" +
                Response(999, "unrelated") + "\n" + Response(id, "output " + id), fragmentSize: 11);
            await using var transport = new SteamCdpTransport(socket);
            var outputs = await Task.WhenAll(transport.ReadAsync(CancellationToken.None), transport.ReadAsync(CancellationToken.None));
            Assert.Equal("output 1", outputs[0]);
            Assert.Equal("output 2", outputs[1]);
            Assert.Equal(1, socket.MaximumConcurrentReceives);
        });

        suite.Add("Steam CDP never repeats a command after an uncertain response", async () =>
        {
            var socket = new FakeSocket((id, _) => JsonSerializer.Serialize(new
                { id, result = new { exceptionDetails = new { text = "backend unavailable" } } }));
            await using var transport = new SteamCdpTransport(socket);
            const string command = "download_depot 489830 489831 8442952117333549665";
            await Assert.ThrowsAsync<SkyrimVersionPatcher.Core.Downloading.SteamConsoleUnavailableException>(() => transport.DispatchAsync(command, CancellationToken.None));
            await Assert.ThrowsAsync<SkyrimVersionPatcher.Core.Downloading.SteamConsoleUnavailableException>(() => transport.DispatchAsync(command, CancellationToken.None));
            Assert.Equal(1, socket.Expressions.Count);
            Assert.Equal(WebSocketState.Aborted, socket.State);
        });

        suite.Add("Steam CDP rejects oversized replies before interpreting them", async () =>
        {
            var socket = new FakeSocket((id, _) => Response(id, new string('x', SteamCdpTransport.MaximumMessageBytes)), fragmentSize: 16_384);
            await using var transport = new SteamCdpTransport(socket);
            await Assert.ThrowsAsync<InvalidDataException>(() => transport.ReadAsync(CancellationToken.None));
            Assert.Equal(WebSocketState.Aborted, socket.State);
        });

        suite.Add("Steam CDP damaged JSON and protocol errors terminate the connection", async () =>
        {
            foreach (var (failure, invalidData) in new[]
            {
                ("not-json", true), ("{\"id\":1,\"error\":{\"message\":\"refused\"}}", false),
                ("{\"id\":\"1\",\"result\":{}}", true)
            })
            {
                var socket = new FakeSocket((_, _) => failure);
                await using var transport = new SteamCdpTransport(socket);
                if (invalidData) await Assert.ThrowsAsync<InvalidDataException>(() => transport.ReadAsync(CancellationToken.None));
                else await Assert.ThrowsAsync<IOException>(() => transport.ReadAsync(CancellationToken.None));
                Assert.Equal(WebSocketState.Aborted, socket.State);
            }
        });

        suite.Add("Steam CDP invalid commands never reach the socket", async () =>
        {
            var socket = new FakeSocket((id, _) => Response(id, true));
            await using var transport = new SteamCdpTransport(socket);
            await Assert.ThrowsAsync<ArgumentException>(() => transport.DispatchAsync("quit", CancellationToken.None));
            Assert.Equal(0, socket.Expressions.Count);
        });

        suite.Add("Steam CDP disposal unregisters only its own collector", async () =>
        {
            var socket = new FakeSocket((id, _) => Response(id, true));
            var transport = new SteamCdpTransport(socket, "__specific_collector");
            await transport.DisposeAsync();
            Assert.Equal(1, socket.Expressions.Count);
            Assert.True(socket.Expressions[0].Contains("__specific_collector", StringComparison.Ordinal));
            Assert.True(socket.Expressions[0].Contains(".unregister()", StringComparison.Ordinal));
            Assert.True(socket.Expressions[0].Contains("delete window[k]", StringComparison.Ordinal));
        });
    }

    private static object Target(string title, string url, string webSocketDebuggerUrl) =>
        new { type = "page", title, url, webSocketDebuggerUrl };

    private static string Response(int id, object value) => JsonSerializer.Serialize(new
        { id, result = new { result = new { type = value is bool ? "boolean" : "string", value } } });

    private sealed class FakeSocket(Func<int, string, string> response, int fragmentSize = 4096) : WebSocket
    {
        private readonly Queue<(byte[] Bytes, bool End)> replies = [];
        private WebSocketState state = WebSocketState.Open;
        private int receiving;
        public List<string> Expressions { get; } = [];
        public int MaximumConcurrentReceives { get; private set; }
        public override WebSocketState State => state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = JsonDocument.Parse(buffer.AsMemory());
            var id = request.RootElement.GetProperty("id").GetInt32();
            var expression = request.RootElement.GetProperty("params").GetProperty("expression").GetString()!;
            Expressions.Add(expression);
            foreach (var message in response(id, expression).Split('\n'))
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                for (var offset = 0; offset < bytes.Length; offset += fragmentSize)
                {
                    var length = Math.Min(fragmentSize, bytes.Length - offset);
                    replies.Enqueue((bytes.AsSpan(offset, length).ToArray(), offset + length == bytes.Length));
                }
            }
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref receiving);
            MaximumConcurrentReceives = Math.Max(MaximumConcurrentReceives, concurrent);
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                if (replies.Count == 0) throw new WebSocketException("No fake response.");
                var fragment = replies.Dequeue();
                fragment.Bytes.AsSpan().CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(fragment.Bytes.Length, WebSocketMessageType.Text, fragment.End);
            }
            finally { Interlocked.Decrement(ref receiving); }
        }
    }
}
