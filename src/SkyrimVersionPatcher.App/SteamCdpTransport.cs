using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.App;

/// <summary>Uses only Steam's Console API in its verified local CEF shared context.</summary>
internal sealed class SteamCdpTransport : IAsyncDisposable
{
    internal const int MaximumMessageBytes = 2 * 1024 * 1024;
    internal const int MaximumConsoleCharacters = 524_288;
    private const int MaximumTargetsBytes = 262_144;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);
    private static readonly Regex DepotCommand = new(
        @"\Adownload_depot 489830 (?:48983[1-9]|54486[01]) ([1-9][0-9]{0,19})\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> SharedTitles = new(StringComparer.Ordinal)
        { "SharedJSContext", "Steam Shared Context presented by Valve™", "Steam", "SP" };
    private readonly WebSocket socket;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly string collectorKey;
    private int nextId;
    private bool faulted;
    private bool disposed;

    // The WebSocket abstraction permits protocol tests without contacting a user's Steam client.
    internal SteamCdpTransport(WebSocket socket, string? collectorKey = null)
    {
        this.socket = socket;
        this.collectorKey = collectorKey ?? "__skyrim_version_patcher_" + Guid.NewGuid().ToString("N");
    }

    public static async Task<SteamCdpTransport?> TryConnectAsync(SteamClientSession session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var owner = SteamCdpListener.FindTrustedOwner(session);
        if (owner is null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(OperationTimeout);
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        SteamCdpTransport? transport = null;
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{SteamCdpListener.Port}/json/list",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                MaximumTargetsBytes, timeout.Token).ConfigureAwait(false);
            using var targets = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var endpoints = FindSharedEndpoints(targets.RootElement);
            if (SteamCdpListener.FindTrustedOwner(session) != owner) return null;
            foreach (var endpoint in endpoints)
            {
                var connection = new ClientWebSocket();
                connection.Options.Proxy = null;
                try
                {
                    await connection.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                    if (SteamCdpListener.FindTrustedOwner(session) != owner)
                    {
                        connection.Dispose();
                        return null;
                    }
                    transport = new SteamCdpTransport(connection);
                    if (await transport.InstallCollectorAsync(timeout.Token).ConfigureAwait(false)) return transport;
                    await transport.DisposeAsync().ConfigureAwait(false);
                    transport = null;
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or WebSocketException or IOException or InvalidDataException or JsonException)
        {
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static IReadOnlyList<Uri> FindSharedEndpoints(JsonElement targets)
    {
        if (targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() > 256) return [];
        var endpoints = new List<Uri>();
        foreach (var target in targets.EnumerateArray())
        {
            if (target.ValueKind != JsonValueKind.Object || ReadString(target, "type") != "page" ||
                !SharedTitles.Contains(ReadString(target, "title") ?? "") ||
                !Uri.TryCreate(ReadString(target, "url"), UriKind.Absolute, out var origin) ||
                origin.Scheme != "https" || origin.Host != "steamloopback.host" || !origin.IsDefaultPort ||
                origin.UserInfo.Length != 0 ||
                !(origin.AbsolutePath == "/index.html" || origin.AbsolutePath.StartsWith("/routes/", StringComparison.Ordinal)) ||
                !Uri.TryCreate(ReadString(target, "webSocketDebuggerUrl"), UriKind.Absolute, out var advertised) ||
                advertised.Scheme != "ws" || advertised.Port != SteamCdpListener.Port ||
                advertised.UserInfo.Length != 0 || advertised.Query.Length != 0 || advertised.Fragment.Length != 0 ||
                !(advertised.Host == "localhost" || IPAddress.TryParse(advertised.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)) ||
                !advertised.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal) ||
                advertised.AbsolutePath.Length > 256) continue;
            // Steam advertises localhost; connect to the exact IPv4 listener whose owner we verified.
            var endpoint = new UriBuilder(advertised) { Host = "127.0.0.1" }.Uri;
            if (!endpoints.Contains(endpoint)) endpoints.Add(endpoint);
        }
        return endpoints;
    }

    public async Task DispatchAsync(string command, CancellationToken token)
    {
        ValidateCommand(command);
        var key = JsonSerializer.Serialize(collectorKey);
        var expression = "(() => { const s = window[" + key + "]; " +
            "if (!s || !s.handle) throw new Error('Console subscription unavailable'); " +
            "SteamClient.Console.ExecCommand(" + JsonSerializer.Serialize(command) + "); return true; })()";
        try
        {
            var result = await EvaluateAsync(expression, token).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.True)
            {
                FaultConnection();
                throw new SteamConsoleUnavailableException("Steam не подтвердил отправку команды загрузки.", command);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or WebSocketException)
        {
            // The backend command may already be running. Never retry it through another transport.
            throw new SteamConsoleUnavailableException("Связь со Steam прервалась при отправке команды. " +
                "Загрузка может продолжаться в Steam; установка остановлена. Повторите установку после восстановления связи.", command);
        }
    }

    public async Task<string?> ReadAsync(CancellationToken token)
    {
        var key = JsonSerializer.Serialize(collectorKey);
        var result = await EvaluateAsync("(() => { const s = window[" + key + "]; " +
            "if (!s || !s.handle) throw new Error('Console subscription unavailable'); return s.text; })()", token).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.String)
            throw new SteamConsoleUnavailableException("Steam перестал предоставлять подтверждения загрузки. Повторите установку.");
        return result.GetString();
    }

    internal static void ValidateCommand(string command)
    {
        var match = DepotCommand.Match(command);
        if (!match.Success || !ulong.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var manifest) || manifest == 0)
            throw new ArgumentException("Разрешены только команды загрузки депо Skyrim Special Edition.", nameof(command));
    }

    internal async Task<bool> InstallCollectorAsync(CancellationToken token)
    {
        var key = JsonSerializer.Serialize(collectorKey);
        var expression = "(() => { if (typeof SteamClient === 'undefined' || " +
            "typeof SteamClient.Console?.ExecCommand !== 'function' || " +
            "typeof SteamClient.Console?.RegisterForSpewOutput !== 'function') return false; " +
            "const k = " + key + "; try { window[k]?.handle?.unregister(); } catch {} " +
            "const s = { text: '', handle: null }; window[k] = s; " +
            "s.handle = SteamClient.Console.RegisterForSpewOutput(o => { " +
            "if (!o || typeof o.spew !== 'string') return; " +
            "const t = o.spew + (o.spew_type === 'input' && !o.spew.endsWith('\\n') ? '\\n' : ''); " +
            "s.text = (s.text + t).slice(-" + MaximumConsoleCharacters.ToString(CultureInfo.InvariantCulture) + "); }); " +
            "if (!s.handle || typeof s.handle.unregister !== 'function') { delete window[k]; return false; } return true; })()";
        return (await EvaluateAsync(expression, token).ConfigureAwait(false)).ValueKind == JsonValueKind.True;
    }

    internal async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (faulted || socket.State != WebSocketState.Open)
                throw new SteamConsoleUnavailableException("Соединение со Steam недоступно. Повторите установку.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(OperationTimeout);
            var id = ++nextId;
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id, method = "Runtime.evaluate", @params = new
                { expression, returnByValue = true, awaitPromise = true, timeout = (int)OperationTimeout.TotalMilliseconds }
            });
            try
            {
                await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var message = await ReceiveMessageAsync(timeout.Token).ConfigureAwait(false);
                    using var response = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 32 });
                    var root = response.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Steam отправил некорректное подтверждение команды.");
                    if (!root.TryGetProperty("id", out var responseId)) continue; // Unsolicited CDP events.
                    if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var receivedId))
                        throw new InvalidDataException("Steam отправил некорректный номер подтверждения команды.");
                    if (receivedId != id) continue;
                    if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result) ||
                        result.ValueKind != JsonValueKind.Object || result.TryGetProperty("exceptionDetails", out _) ||
                        !result.TryGetProperty("result", out var remote) || remote.ValueKind != JsonValueKind.Object ||
                        ReadString(remote, "subtype") == "error" || !remote.TryGetProperty("value", out var value))
                        throw new SteamConsoleUnavailableException("Steam не выполнил запрос к своей консоли. Повторите установку.");
                    return value.Clone();
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                FaultConnection();
                throw new SteamConsoleUnavailableException("Steam не ответил вовремя. Повторите установку.");
            }
            catch (JsonException)
            {
                FaultConnection();
                throw new InvalidDataException("Steam отправил повреждённое подтверждение команды.");
            }
            catch
            {
                FaultConnection();
                throw;
            }
        }
        finally { operations.Release(); }
    }

    private async Task<byte[]> ReceiveMessageAsync(CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var fragment = new byte[16_384];
        while (true)
        {
            var result = await socket.ReceiveAsync(fragment.AsMemory(), token).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text)
                throw new SteamConsoleUnavailableException("Steam закрыл соединение или прислал неподдерживаемый ответ.");
            if (buffer.Length + result.Count > MaximumMessageBytes)
                throw new InvalidDataException("Ответ Steam превышает допустимый размер.");
            buffer.Write(fragment, 0, result.Count);
            if (result.EndOfMessage) return buffer.ToArray();
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
        await using (stream.ConfigureAwait(false))
        {
            using var result = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (result.Length + read > maximumBytes) throw new InvalidDataException("Список окон Steam превышает допустимый размер.");
                result.Write(buffer, 0, read);
            }
            return result.ToArray();
        }
    }

    private static string? ReadString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void FaultConnection()
    {
        faulted = true;
        socket.Abort();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        try
        {
            if (!faulted && socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var key = JsonSerializer.Serialize(collectorKey);
                await EvaluateAsync("(() => { const k = " + key + "; try { window[k]?.handle?.unregister(); } " +
                    "finally { delete window[k]; } return true; })()", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or WebSocketException or OperationCanceledException or JsonException) { }
        finally
        {
            disposed = true;
            socket.Abort();
            socket.Dispose();
        }
    }
}
