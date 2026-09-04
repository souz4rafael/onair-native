using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using OnAirNative.Views;

namespace OnAirNative.Services;

/// <summary>
/// LAN-reachable sibling of <see cref="RemoteControlService"/>: serves a small static control
/// page (Assets/web-remote/*) plus the exact same WebSocket protocol/op vocabulary
/// (command/adjust/getState/set/loadScript/getScriptText/listFonts/showInsight/clearInsight/
/// listStealthWindows/embedStealthWindow, pushing <see cref="RemoteState"/> broadcasts) so a
/// phone/tablet/other PC on the same Wi-Fi can control onAIr like the Stream Deck plugin does
/// locally. listStealthWindows/embedStealthWindow are deliberately Web-Remote-exclusive — see
/// <see cref="RemoteState.StealthEmbedded"/>'s doc comment for why they're not mirrored on
/// <see cref="RemoteControlService"/>.
///
/// Deliberately a SEPARATE, standalone class rather than a shared base with
/// <see cref="RemoteControlService"/> — that server is loopback-only, production-critical, and
/// already covered by integration tests; duplicating ~150 lines of protocol glue here is a
/// worthwhile tradeoff for zero regression risk to it. Keep the two protocol implementations in
/// sync by hand when the op vocabulary changes.
///
/// Security model is necessarily stricter than the loopback server's, since this one is reachable
/// from other devices on the network:
///  - Binds to <c>http://+:{Port}/</c> (wildcard, all interfaces) only when explicitly enabled.
///  - The static page itself is served unauthenticated (harmless UI chrome, no private data).
///  - Every WebSocket upgrade requires a correct <c>?pin=</c> query parameter, checked against the
///    live <see cref="AppConfig.WebRemote"/> PIN on every single connection attempt (no
///    server-side session/token store — regenerating the PIN instantly revokes every device).
///  - Windows itself requires either admin elevation or a one-time
///    <c>netsh http add urlacl url=http://+:{Port}/ user=Everyone</c> reservation before any
///    non-loopback HttpListener prefix can bind — see <see cref="TryGrantUrlAcl"/>, invoked only
///    from an explicit user gesture (Settings → WEB REMOTE → "Grant Network Access"), never
///    silently at launch.
/// </summary>
public sealed class WebRemoteService : IDisposable
{
    /// <summary>Distinct from <see cref="RemoteControlService.Port"/> (47823) so both servers can
    /// run side by side without conflict.</summary>
    public const int Port = 47824;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
    };

    private static readonly string AssetsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "web-remote");

    private readonly Action<HotkeyAction> _executeAction;
    private readonly Func<RemoteState> _getState;
    private readonly Func<string, JsonElement, (bool Success, string? Error)> _setField;
    private readonly Func<string, Task<(bool Success, string? Error)>> _loadScript;
    private readonly Func<string> _getScriptText;
    private readonly Func<List<string>> _listFonts;
    private readonly Func<string, (bool Success, string? Error)> _showInsight;
    private readonly Func<(bool Success, string? Error)> _clearInsight;
    private readonly Func<List<ControllerWindow.RemoteWindowInfo>> _listStealthWindows;
    private readonly Func<string, (bool Success, string? Error)> _embedStealthWindow;
    private readonly Func<string> _getPin;
    private readonly DispatcherQueue _uiQueue;
    private readonly HttpListener _listener = new();
    private readonly List<WebSocket> _clients = [];
    private readonly object _clientsLock = new();
    private readonly DispatcherQueueTimer _refreshTimer;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public WebRemoteService(
        Action<HotkeyAction> executeAction,
        Func<RemoteState> getState,
        DispatcherQueue uiQueue,
        Func<string, JsonElement, (bool Success, string? Error)> setField,
        Func<string, Task<(bool Success, string? Error)>> loadScript,
        Func<string> getScriptText,
        Func<List<string>> listFonts,
        Func<string, (bool Success, string? Error)> showInsight,
        Func<(bool Success, string? Error)> clearInsight,
        Func<List<ControllerWindow.RemoteWindowInfo>> listStealthWindows,
        Func<string, (bool Success, string? Error)> embedStealthWindow,
        Func<string> getPin)
    {
        _executeAction = executeAction;
        _getState      = getState;
        _setField      = setField;
        _loadScript    = loadScript;
        _getScriptText = getScriptText;
        _listFonts     = listFonts;
        _showInsight   = showInsight;
        _clearInsight  = clearInsight;
        _listStealthWindows = listStealthWindows;
        _embedStealthWindow = embedStealthWindow;
        _getPin        = getPin;
        _uiQueue       = uiQueue;
        _listener.Prefixes.Add($"http://+:{Port}/");

        // Same safety-net rationale as RemoteControlService's own timer — see that class's
        // constructor doc comment.
        _refreshTimer = _uiQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += (_, _) => BroadcastCurrentState();
    }

    /// <summary>Starts listening. Throws <see cref="HttpListenerException"/> (ErrorCode 5) if the
    /// wildcard prefix hasn't been ACL-granted yet — callers must catch that specifically to
    /// drive the "needs elevation" UI state rather than treating it as a generic failure.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _refreshTimer.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>Call after any <see cref="HotkeyAction"/> executes, or any other state-affecting
    /// change, so connected web clients see the result immediately. Must be called on the UI
    /// thread. Mirrors <see cref="RemoteControlService.NotifyStateMayHaveChanged"/> exactly.</summary>
    public void NotifyStateMayHaveChanged() => BroadcastCurrentState();

    private void BroadcastCurrentState()
    {
        List<WebSocket> targets;
        lock (_clientsLock)
        {
            if (_clients.Count == 0) return;
            targets = [.. _clients];
        }

        var state = _getState();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { op = "state", data = state }, JsonOptions));
        _ = SendToAllAsync(targets, bytes);
    }

    private static async Task SendToAllAsync(List<WebSocket> targets, byte[] bytes)
    {
        foreach (var socket in targets)
        {
            if (socket.State != WebSocketState.Open) continue;
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
            catch { /* client gone — its own receive loop will notice and prune it */ }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (HttpListenerException) { return; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                _ = ServeStaticAsync(ctx);
                continue;
            }

            var pin = ctx.Request.QueryString["pin"] ?? "";
            var expectedPin = _getPin();
            if (string.IsNullOrEmpty(expectedPin) || !string.Equals(pin, expectedPin, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Close();
                continue;
            }

            _ = HandleClientAsync(ctx, ct);
        }
    }

    /// <summary>Serves the static control page — deliberately unauthenticated (no PIN check),
    /// since it's just inert HTML/CSS/JS with no private data; only the WebSocket upgrade is
    /// gated. <see cref="Path.GetFileName"/> strips any directory segments from the requested
    /// path, so this can never escape <see cref="AssetsDir"/>.</summary>
    private static async Task ServeStaticAsync(HttpListenerContext ctx)
    {
        try
        {
            var requestPath = ctx.Request.Url?.AbsolutePath ?? "/";
            var fileName = requestPath == "/" ? "index.html" : Path.GetFileName(requestPath);
            var ext = Path.GetExtension(fileName);

            if (string.IsNullOrEmpty(fileName) || !ContentTypes.TryGetValue(ext, out var contentType))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var fullPath = Path.Combine(AssetsDir, fileName);
            if (!File.Exists(fullPath))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var bytes = await File.ReadAllBytesAsync(fullPath);
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch { /* client disconnected mid-response — nothing to do */ }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocket socket;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            socket = wsCtx.WebSocket;
        }
        catch { return; }

        lock (_clientsLock) _clients.Add(socket);
        try
        {
            await SendStateToNewClientAsync(socket, ct);

            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                HandleIncomingMessage(socket, Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch { /* client disconnected or errored — fall through to cleanup */ }
        finally
        {
            lock (_clientsLock) _clients.Remove(socket);
            try { socket.Dispose(); } catch { }
        }
    }

    private void HandleIncomingMessage(WebSocket socket, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var opEl)) return;
            var op = opEl.GetString();
            string? id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

            if (op == "getState")
            {
                BroadcastCurrentStateFromBackgroundThread();
                return;
            }

            if ((op == "command" || op == "adjust") &&
                root.TryGetProperty("action", out var actionEl) &&
                actionEl.GetString() is string actionName &&
                Enum.TryParse<HotkeyAction>(actionName, out var action))
            {
                _uiQueue.TryEnqueue(() => _executeAction(action));
                return;
            }

            if (op == "set" &&
                root.TryGetProperty("field", out var fieldEl) &&
                fieldEl.GetString() is string field &&
                root.TryGetProperty("value", out var valueEl))
            {
                var valueClone = valueEl.Clone();
                _uiQueue.TryEnqueue(() =>
                {
                    var (success, error) = _setField(field, valueClone);
                    _ = SendResultAsync(socket, id, success, error);
                });
                return;
            }

            if (op == "loadScript" &&
                root.TryGetProperty("path", out var pathEl) &&
                pathEl.GetString() is string path)
            {
                _uiQueue.TryEnqueue(() => _ = HandleLoadScriptAsync(socket, id, path));
                return;
            }

            if (op == "getScriptText")
            {
                _uiQueue.TryEnqueue(() =>
                {
                    var text = _getScriptText();
                    _ = SendResultAsync(socket, id, true, null, text);
                });
                return;
            }

            if (op == "listFonts")
            {
                _uiQueue.TryEnqueue(() =>
                {
                    var fonts = _listFonts();
                    _ = SendResultAsync(socket, id, true, null, fonts);
                });
                return;
            }

            if (op == "showInsight" &&
                root.TryGetProperty("text", out var textEl) &&
                textEl.GetString() is string insightText)
            {
                _uiQueue.TryEnqueue(() =>
                {
                    var (success, error) = _showInsight(insightText);
                    _ = SendResultAsync(socket, id, success, error);
                });
                return;
            }

            if (op == "clearInsight")
            {
                _uiQueue.TryEnqueue(() =>
                {
                    var (success, error) = _clearInsight();
                    _ = SendResultAsync(socket, id, success, error);
                });
                return;
            }

            if (op == "listStealthWindows")
            {
                _uiQueue.TryEnqueue(() =>
                {
                    var windows = _listStealthWindows();
                    _ = SendResultAsync(socket, id, true, null, windows);
                });
                return;
            }

            if (op == "embedStealthWindow" &&
                root.TryGetProperty("windowId", out var windowIdEl) &&
                windowIdEl.GetString() is string windowId)
            {
                _uiQueue.TryEnqueue(() =>
                {
                    var (success, error) = _embedStealthWindow(windowId);
                    _ = SendResultAsync(socket, id, success, error);
                });
                return;
            }
        }
        catch { /* malformed message from the client — ignore, keep the connection alive */ }
    }

    private async Task HandleLoadScriptAsync(WebSocket socket, string? id, string path)
    {
        var (success, error) = await _loadScript(path);
        await SendResultAsync(socket, id, success, error);
    }

    private static async Task SendResultAsync(WebSocket socket, string? id, bool success, string? error, object? data = null)
    {
        if (socket.State != WebSocketState.Open) return;
        var payload = new Dictionary<string, object?> { ["op"] = "result", ["id"] = id, ["success"] = success };
        if (error is not null) payload["error"] = error;
        if (data is not null) payload["data"] = data;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { /* client gone — its own receive loop will notice and prune it */ }
    }

    private void BroadcastCurrentStateFromBackgroundThread() =>
        _uiQueue.TryEnqueue(BroadcastCurrentState);

    private async Task SendStateToNewClientAsync(WebSocket socket, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<RemoteState>();
        _uiQueue.TryEnqueue(() => tcs.TrySetResult(_getState()));
        RemoteState state;
        try { state = await tcs.Task; }
        catch { return; }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { op = "state", data = state }, JsonOptions));
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        catch { /* client already gone */ }
    }

    /// <summary>Generates a fresh 6-digit numeric PIN (zero-padded, e.g. "004821").</summary>
    public static string GeneratePin() => Random.Shared.Next(0, 1_000_000).ToString("D6");

    /// <summary>Non-loopback IPv4 addresses of this machine's active network interfaces — used to
    /// build the connect URL shown/copied in Settings. Best-effort: returns an empty list rather
    /// than throwing if enumeration fails for any reason.</summary>
    public static List<string> GetLanAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var addrInfo in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addrInfo.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addrInfo.Address)) continue;
                    addresses.Add(addrInfo.Address.ToString());
                }
            }
        }
        catch { /* best-effort — Settings just shows "unknown" if this fails */ }
        return addresses;
    }

    /// <summary>Runs the one-time elevated <c>netsh http add urlacl</c> reservation that lets this
    /// (non-admin) process bind an HttpListener to a wildcard prefix on subsequent attempts —
    /// persists across app restarts in HTTP.SYS's own namespace store, no ongoing elevation
    /// needed afterward. Must be called ONLY in direct response to an explicit user gesture
    /// (Settings → WEB REMOTE → "Grant Network Access") — it triggers a UAC consent prompt, never
    /// call this silently at app launch. Blocking (waits for the elevated process to exit) — run
    /// off the UI thread. Returns true if the reservation now exists (either just added, or
    /// already did — netsh's own exit code is 0 in both cases); false if the user declined the
    /// UAC prompt or netsh itself failed.</summary>
    public static bool TryGrantUrlAcl()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"http add urlacl url=http://+:{Port}/ user=Everyone")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(15000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // user clicked "No" on the UAC prompt
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _refreshTimer.Stop();
        _cts?.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }

        lock (_clientsLock)
        {
            foreach (var socket in _clients)
            {
                try { socket.Dispose(); } catch { }
            }
            _clients.Clear();
        }
        _cts?.Dispose();
    }
}
