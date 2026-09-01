using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnAirMcp;

/// <summary>
/// Singleton WebSocket client connecting to onAIr's RemoteControlService (loopback-only,
/// 127.0.0.1:47823) — the exact same server the Stream Deck plugin talks to
/// (see streamdeck-plugin/src/onair-client.ts for that sibling client, and
/// OnAirNative/Services/RemoteControlService.cs for the authoritative protocol definition).
///
/// Unlike the Stream Deck plugin (which only ever fires commands and reads the periodic state
/// broadcast), MCP tool calls need a genuine synchronous request/response: "did setting the
/// font size to 999 actually succeed, or did it get rejected?" So this client additionally
/// tags every set/loadScript/getScriptText/listFonts request with a client-chosen "id" and
/// correlates the server's "result" reply back to the awaiting caller via a
/// ConcurrentDictionary of TaskCompletionSources — command/adjust/getState stay fire-and-forget
/// exactly as onAIr's side implements them (no "result" reply for those, by design).
///
/// Connects lazily on first use and reconnects transparently on the next call if the connection
/// drops (e.g. onAIr was closed and reopened) — never throws until a real attempt fails, so a
/// brief onAIr restart doesn't require restarting the MCP server process too.
/// </summary>
public sealed class OnAirClient : IAsyncDisposable
{
    public const int Port = 47823;
    private const int ConnectTimeoutMs = 2000;
    private const int RequestTimeoutMs = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static readonly OnAirClient Instance = new();

    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private RemoteState? _latestState;
    private long _nextId;
    private CancellationTokenSource? _receiveCts;

    private OnAirClient() { }

    /// <summary>Ensures a live connection, (re)connecting if needed. Throws
    /// <see cref="InvalidOperationException"/> with a clear, user-facing message if onAIr isn't
    /// reachable — every public method funnels through this first so MCP tool callers get a
    /// consistent, friendly error instead of a raw socket exception.</summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_ws?.State == WebSocketState.Open) return;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_ws?.State == WebSocketState.Open) return;

            _ws?.Dispose();
            _ws = new ClientWebSocket();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ConnectTimeoutMs);
            try
            {
                await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/"), timeoutCts.Token);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not connect to onAIr. Make sure onAIr is running and its Stream Deck " +
                    "remote control server is enabled (Settings tab → STREAM DECK).", ex);
            }

            // Any pending requests from a previous, now-dead connection can never be answered.
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            _pending.Clear();
            _latestState = null;

            _receiveCts?.Cancel();
            _receiveCts = new CancellationTokenSource();
            _ = ReceiveLoopAsync(_ws, _receiveCts.Token);
        }
        finally { _connectLock.Release(); }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                Dispatch(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch { /* connection dropped — EnsureConnectedAsync reconnects on the next call */ }
    }

    private void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var opEl)) return;
            var op = opEl.GetString();

            if (op == "state" && root.TryGetProperty("data", out var dataEl))
            {
                _latestState = JsonSerializer.Deserialize<RemoteState>(dataEl.GetRawText(), JsonOptions);
                return;
            }

            if (op == "result" && root.TryGetProperty("id", out var idEl) &&
                idEl.GetString() is string id && _pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult(root.Clone());
            }
        }
        catch { /* malformed message — ignore, keep the connection alive */ }
    }

    private string NextId() => Interlocked.Increment(ref _nextId).ToString();

    /// <summary>Sends a request that expects a correlated "result" reply — used for
    /// set/loadScript/getScriptText/listFonts. Not used for command/adjust/getState, which stay
    /// fire-and-forget on the onAIr side (see RemoteControlService.HandleIncomingMessage).</summary>
    private async Task<JsonElement> SendRequestAsync(JsonObject message, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var id = NextId();
        message["id"] = id;

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(JsonOptions));
            await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException("Lost connection to onAIr while sending the request.", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeoutMs);
        await using var registration = timeoutCts.Token.Register(() => tcs.TrySetCanceled());
        try
        {
            return await tcs.Task;
        }
        catch (TaskCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException("onAIr did not respond in time.");
        }
    }

    private static (bool Success, string? Error) ParseResult(JsonElement result)
    {
        var success = result.TryGetProperty("success", out var s) && s.GetBoolean();
        var error = result.TryGetProperty("error", out var e) ? e.GetString() : null;
        return (success, error);
    }

    /// <summary>Fetches a fresh state snapshot. Sends an explicit "getState" request rather than
    /// only relying on the periodic broadcast, so a just-opened connection doesn't have to wait
    /// up to 2s for onAIr's safety-net timer.</summary>
    public async Task<RemoteState> GetStateAsync(CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { op = "getState" }, JsonOptions));
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

        var deadline = DateTime.UtcNow.AddMilliseconds(RequestTimeoutMs);
        while (_latestState is null && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct);

        return _latestState ?? throw new TimeoutException("onAIr did not report its state in time.");
    }

    /// <summary>Fires a toggle/one-shot <c>HotkeyAction</c> by name (e.g. "ToggleOverlayVisibility")
    /// — fire-and-forget, exactly like a Stream Deck button press. Callers should follow up with
    /// <see cref="GetStateAsync"/> if they need to confirm the resulting state.</summary>
    public async Task SendCommandAsync(string action, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { op = "command", action }, JsonOptions));
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public async Task<(bool Success, string? Error)> SetFieldAsync(string field, object value, CancellationToken ct = default)
    {
        var msg = new JsonObject
        {
            ["op"] = "set",
            ["field"] = field,
            ["value"] = JsonSerializer.SerializeToNode(value, JsonOptions),
        };
        var result = await SendRequestAsync(msg, ct);
        return ParseResult(result);
    }

    public async Task<(bool Success, string? Error)> LoadScriptAsync(string path, CancellationToken ct = default)
    {
        var msg = new JsonObject { ["op"] = "loadScript", ["path"] = path };
        var result = await SendRequestAsync(msg, ct);
        return ParseResult(result);
    }

    public async Task<string> GetScriptTextAsync(CancellationToken ct = default)
    {
        var result = await SendRequestAsync(new JsonObject { ["op"] = "getScriptText" }, ct);
        var (success, error) = ParseResult(result);
        if (!success) throw new InvalidOperationException(error ?? "Unknown error getting script text.");
        return result.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
    }

    public async Task<List<string>> ListFontsAsync(CancellationToken ct = default)
    {
        var result = await SendRequestAsync(new JsonObject { ["op"] = "listFonts" }, ct);
        var (success, error) = ParseResult(result);
        if (!success) throw new InvalidOperationException(error ?? "Unknown error listing fonts.");
        return result.TryGetProperty("data", out var d)
            ? d.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
            : [];
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCts?.Cancel();
        if (_ws is not null)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None); }
            catch { /* best-effort close */ }
            _ws.Dispose();
        }
    }
}
