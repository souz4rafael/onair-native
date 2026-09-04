using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace OnAirNative.Services;

/// <summary>Snapshot of remotely-interesting app state, pushed to connected Stream Deck plugin
/// clients for button/dial visual feedback (Layer 2). Property names are serialized as-is
/// (camelCase via <see cref="RemoteControlService"/>'s JsonSerializerOptions) — the Stream Deck
/// plugin's TypeScript side has a matching interface, keep both in sync when adding fields.</summary>
public sealed class RemoteState
{
    public bool   TpOpen                  { get; set; }
    public bool   TpLocked                { get; set; }
    public bool   TpHiddenInShare         { get; set; }
    public bool   ControllerHiddenInShare { get; set; }
    public bool   Recording               { get; set; }
    public string ChatProvider            { get; set; } = "";
    public bool   WhisperLocalLoaded      { get; set; }
    public string WhisperModelStatus      { get; set; } = "";
    public double Opacity                 { get; set; }
    public int    FontSize                { get; set; }
    public int    ScrollSpeed             { get; set; }
    public int    VoiceScrollSpeed        { get; set; }
    public int    ScrollStep              { get; set; }
    public double VoiceThreshold          { get; set; }
    /// <summary>"Manual" | "Auto" | "Voice" — mirrors <c>OverlayViewModel.ScrollMode</c>'s enum
    /// name exactly (see <see cref="OnAirNative.ViewModels.ScrollMode"/>).</summary>
    public string ScrollMode              { get; set; } = "";
    public string FontFamily              { get; set; } = "";
    /// <summary>Display filename of the currently loaded script (e.g. "demo.txt"), or the
    /// placeholder text when nothing is loaded — mirrors <c>ScrollTabViewModel.LoadedFileName</c>.</summary>
    public string LoadedScriptName        { get; set; } = "";

    // ── Q&A monitoring + Copilot insights (Block 6) ───────────────────────────
    // See OverlayViewModel's own doc comments for the full design rationale. All of these are
    // read-only from an MCP/WebSocket client's perspective except InsightText, which is written
    // via the "showInsight"/"clearInsight" ops (see RemoteControlService's protocol doc comment).

    /// <summary>The most recently transcribed question, regardless of whether a Q&amp;A session
    /// (Markdown recording — see <see cref="QaSessionActive"/>) is active. Empty until the first
    /// successful Q&amp;A round this app launch.</summary>
    public string LastQuestion             { get; set; } = "";
    /// <summary>The AI's answer to <see cref="LastQuestion"/>.</summary>
    public string LastAnswer               { get; set; } = "";
    /// <summary>Increments once per successfully completed Q&amp;A round — a change-detection
    /// heartbeat for a polling monitor: remember the last value seen and treat a higher one as
    /// "there's a new turn to look at", cheaper and more robust than diffing question/answer text
    /// (which could legitimately repeat verbatim across two different turns).</summary>
    public int    QaTurnCount              { get; set; }
    /// <summary>Words-per-minute pacing summary for the most recent recording (see
    /// PacingAnalyzer), or a neutral "not enough data" message — same text shown in the
    /// Controller's USAGE &amp; CONVERSATION card, never on the TP itself.</summary>
    public string PacingSummary            { get; set; } = "";
    /// <summary>"None" | "Slow" | "Good" | "Fast" — coarse pacing classification mirroring
    /// <see cref="OnAirNative.Services.PacingLevel"/>, kept separate from
    /// <see cref="PacingSummary"/>'s free English text specifically so the Stream Deck plugin's
    /// pacing-status tile can pick an icon by simple string match instead of parsing a sentence.</summary>
    public string PacingLevel               { get; set; } = "None";
    /// <summary>Follow-up questions the presenter could ask the client next (see
    /// AiChatService.GetFollowUpSuggestionsAsync) — empty unless AppConfig.
    /// ShowFollowUpSuggestions is on AND the most recent turn actually returned some.</summary>
    public List<string> FollowUpSuggestions { get; set; } = [];
    /// <summary>Whether a Q&amp;A session (an explicit, presenter-started Markdown transcript —
    /// see QaSessionService) is currently recording.</summary>
    public bool   QaSessionActive          { get; set; }
    /// <summary>Free text currently shown in the TP's Copilot-insight footer (visible in both
    /// Script and Q&amp;A modes) — set via the "showInsight" op / onair_show_insight tool, cleared
    /// via "clearInsight" / onair_clear_insight. Empty means no insight is currently shown.</summary>
    public string InsightText              { get; set; } = "";
    /// <summary>Mirrors <c>AppConfig.ShowFollowUpSuggestions</c> — settable via SetRemoteField
    /// ("ShowFollowUpSuggestions") so a remote client can toggle the AI Insights tab's "Suggest
    /// questions to ask the client" checkbox without opening the Controller.</summary>
    public bool   ShowFollowUpSuggestions  { get; set; }
    /// <summary>Mirrors <c>OverlayViewModel.ShowPacingInInsights</c> — settable via SetRemoteField
    /// ("ShowPacingInInsights") so a remote client can toggle whether the AI Insights window shows
    /// its Pacing section, without opening the Controller. Pure display toggle — pacing is always
    /// computed regardless.</summary>
    public bool   ShowPacingInInsights     { get; set; }
    /// <summary>Mirrors <c>OverlayViewModel.ShowTokenUsageInInsights</c> — settable via
    /// SetRemoteField ("ShowTokenUsageInInsights"), same rationale as
    /// <see cref="ShowPacingInInsights"/> but for the Token Usage section.</summary>
    public bool   ShowTokenUsageInInsights { get; set; }
    /// <summary>Mirrors <c>OverlayViewModel.ShowFollowUpsInInsights</c> — settable via
    /// SetRemoteField ("ShowFollowUpsInInsights"), same rationale as
    /// <see cref="ShowPacingInInsights"/> but for the Questions (follow-up suggestions) section.
    /// Independent of <see cref="ShowFollowUpSuggestions"/> (which instead controls whether
    /// suggestions are generated at all).</summary>
    public bool   ShowFollowUpsInInsights        { get; set; }
    /// <summary>Mirrors <c>OverlayViewModel.ShowExternalInsightsInInsights</c> — settable via
    /// SetRemoteField ("ShowExternalInsightsInInsights"), same rationale as
    /// <see cref="ShowPacingInInsights"/> but for the External AI Insights section.</summary>
    public bool   ShowExternalInsightsInInsights { get; set; }
    /// <summary>How many Q&amp;A turns the AI currently remembers (see
    /// <c>OverlayViewModel.ConversationTurnCount</c>), capped at 6 — separate from
    /// <see cref="QaTurnCount"/>'s all-time monotonic counter. Zero after "Clear conversation" or
    /// starting a new Q&amp;A session.</summary>
    public int    ConversationTurnCount    { get; set; }
    /// <summary>The actual Q&amp;A pairs behind <see cref="ConversationTurnCount"/> (see
    /// <c>OverlayViewModel.ConversationTurns</c>) — lets a remote client show a "view
    /// conversation" popup instead of just the count. Same 6-turn cap, oldest first.</summary>
    public List<ConversationTurnState> ConversationHistory { get; set; } = [];
    /// <summary>Human-readable token usage summary for this session (see
    /// <c>AiTabViewModel.UsageSummary</c>) — same text shown in the Controller's USAGE card.</summary>
    public string UsageSummary             { get; set; } = "";

    // ── AI Insights window (separate resizable Controller-tab-driven window) ─────────────────
    // Mirrors the equivalent TP fields above (TpOpen/TpLocked/TpHiddenInShare/FontSize/Opacity/
    // FontFamily) but for the independent floating InsightWindow — see InsightsTabViewModel and
    // InsightAppearanceConfig for the source of truth. As with FontColor, InsightFontColor is
    // settable (SetRemoteField / onair_set_insight_font_color) but deliberately not mirrored here
    // (colors aren't dial/key-display-friendly).

    /// <summary>Whether the AI Insights window is currently open/visible.</summary>
    public bool   InsightsOpen             { get; set; }
    /// <summary>Whether the AI Insights window is currently locked (click-through, can't be
    /// accidentally moved).</summary>
    public bool   InsightsLocked           { get; set; }
    /// <summary>Whether the AI Insights window is currently hidden from screen share/recording.</summary>
    public bool   InsightsHiddenInShare    { get; set; }
    /// <summary>Font size (points) of the AI Insights window's text.</summary>
    public int    InsightFontSize          { get; set; }
    /// <summary>Opacity of the AI Insights window, as a 0-100 percentage (same convention as
    /// <see cref="Opacity"/>).</summary>
    public double InsightOpacity           { get; set; }
    /// <summary>Font family of the AI Insights window's text.</summary>
    public string InsightFontFamily        { get; set; } = "";

    // ── App Stealth (window embed) — Web Remote only ──────────────────────────
    // Mirrors WindowEmbedService.IsEmbedding/TargetTitle so a reconnecting Web Remote client can
    // show the current embed status immediately, without waiting for another state push. Not
    // surfaced on the Stream Deck/MCP loopback server — a dynamic per-window picker doesn't suit
    // a physical button or a headless tool call, so this stays Web-Remote-exclusive for now.

    /// <summary>Whether a window is currently embedded in the App Stealth container.</summary>
    public bool   StealthEmbedded          { get; set; }
    /// <summary>Title of the currently embedded window, if any (see <see cref="StealthEmbedded"/>).</summary>
    public string StealthEmbedTitle        { get; set; } = "";
}

/// <summary>One remembered Q&amp;A pair for <see cref="RemoteState.ConversationHistory"/> — mirrors
/// <c>OnAirNative.Core.Services.ChatTurn</c>'s two strings under names matching the Controller's
/// existing LastQuestion/LastAnswer convention.</summary>
public sealed class ConversationTurnState
{
    public string Question { get; set; } = "";
    public string Answer   { get; set; } = "";
}

/// <summary>
/// Localhost-only WebSocket server that lets the onAIr Stream Deck plugin (and, since v1.2.0,
/// the onAIr MCP server) remote-control this app: fire the same <see cref="HotkeyAction"/>s as
/// the global hotkeys, push a <see cref="RemoteState"/> snapshot for button/dial visual
/// feedback, and — for MCP — apply absolute setter values and load scripts by path.
///
/// Security model: binds to 127.0.0.1 only (see <see cref="Port"/>) — no other machine on the
/// network can ever reach it. Trusts anything running as the current Windows user on this
/// machine, the same trust boundary as the global hotkeys themselves (any process can already
/// call RegisterHotKey or simulate input). No pairing token — deliberately kept simple for v1.
///
/// Protocol (newline-delimited JSON per WebSocket text frame):
///   plugin/MCP → onAIr: {"op":"command","action":"ToggleOverlayVisibility"}
///                       {"op":"adjust","action":"IncreaseOpacity"}   (adjust and command are
///                                                                     handled identically — both
///                                                                     just parse into a HotkeyAction
///                                                                     and call ExecuteAction)
///                       {"op":"getState"}
///                       {"op":"set","id":"<optional>","field":"FontSize","value":24}
///                       {"op":"loadScript","id":"<optional>","path":"C:\\scripts\\demo.txt"}
///                       {"op":"getScriptText","id":"<optional>"}
///                       {"op":"listFonts","id":"<optional>"}
///                       {"op":"showInsight","id":"<optional>","text":"..."}
///                       {"op":"clearInsight","id":"<optional>"}
///   onAIr → plugin/MCP: {"op":"state","data":{ ...RemoteState fields... }}   (broadcast to all)
///                       {"op":"result","id":"<echoed>","success":true|false,"error":"...","data":...}
///                                                       (reply to exactly the requesting client,
///                                                        only for set/loadScript/getScriptText/
///                                                        listFonts/showInsight/clearInsight —
///                                                        command/adjust/getState stay
///                                                        fire-and-forget, relying on the state
///                                                        broadcast instead, unchanged for the
///                                                        existing Stream Deck plugin)
/// </summary>
public sealed class RemoteControlService : IDisposable
{
    /// <summary>Fixed default port in the private/dynamic range. No UI to reconfigure it yet —
    /// if it ever conflicts with something else on a user's machine, change this constant.</summary>
    public const int Port = 47823;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Action<HotkeyAction> _executeAction;
    private readonly Func<RemoteState> _getState;
    private readonly Func<string, JsonElement, (bool Success, string? Error)> _setField;
    private readonly Func<string, Task<(bool Success, string? Error)>> _loadScript;
    private readonly Func<string> _getScriptText;
    private readonly Func<List<string>> _listFonts;
    private readonly Func<string, (bool Success, string? Error)> _showInsight;
    private readonly Func<(bool Success, string? Error)> _clearInsight;
    private readonly DispatcherQueue _uiQueue;
    private readonly HttpListener _listener = new();
    private readonly List<WebSocket> _clients = [];
    private readonly object _clientsLock = new();
    private readonly DispatcherQueueTimer _refreshTimer;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public RemoteControlService(
        Action<HotkeyAction> executeAction,
        Func<RemoteState> getState,
        DispatcherQueue uiQueue,
        Func<string, JsonElement, (bool Success, string? Error)> setField,
        Func<string, Task<(bool Success, string? Error)>> loadScript,
        Func<string> getScriptText,
        Func<List<string>> listFonts,
        Func<string, (bool Success, string? Error)> showInsight,
        Func<(bool Success, string? Error)> clearInsight)
    {
        _executeAction = executeAction;
        _getState      = getState;
        _setField      = setField;
        _loadScript    = loadScript;
        _getScriptText = getScriptText;
        _listFonts     = listFonts;
        _showInsight   = showInsight;
        _clearInsight  = clearInsight;
        _uiQueue       = uiQueue;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");

        // Safety-net periodic broadcast — catches state changes made purely via mouse click in
        // the Controller UI (many toggles live in XAML code-behind, not a ViewModel, so there's
        // no single PropertyChanged source to hook for all of them). This runs on top of the
        // immediate push that already follows every ExecuteAction call (see
        // NotifyStateMayHaveChanged), so in practice it's a rarely-needed fallback — 2s is
        // frequent enough for Stream Deck tile feedback without being wasteful.
        _refreshTimer = _uiQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += (_, _) => BroadcastCurrentState();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _refreshTimer.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>Call after any <see cref="HotkeyAction"/> executes so connected clients see the
    /// result immediately instead of waiting for the next timer tick. Must be called on the UI
    /// thread — true for every current caller (HotkeyService's dispatch and this class's own
    /// incoming-command handler both already marshal onto <see cref="_uiQueue"/> first).</summary>
    public void NotifyStateMayHaveChanged() => BroadcastCurrentState();

    private void BroadcastCurrentState()
    {
        List<WebSocket> targets;
        lock (_clientsLock)
        {
            if (_clients.Count == 0) return;
            targets = [.. _clients];
        }

        var state = _getState(); // safe: caller is always on the UI thread (see doc comments above)
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
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }
            _ = HandleClientAsync(ctx, ct);
        }
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
                // ExecuteAction touches ViewModels/XAML elements — must run on the UI thread.
                _uiQueue.TryEnqueue(() => _executeAction(action));
                return;
            }

            // The four request/response ops below all touch ViewModels/XAML elements, so — like
            // ExecuteAction above — the actual work must run on the UI thread. Unlike
            // command/adjust/getState (fire-and-forget, relying on the state broadcast), each of
            // these replies directly to the REQUESTING socket with a correlated "result" message,
            // since an MCP tool call needs to know synchronously whether it succeeded.
            if (op == "set" &&
                root.TryGetProperty("field", out var fieldEl) &&
                fieldEl.GetString() is string field &&
                root.TryGetProperty("value", out var valueEl))
            {
                var valueClone = valueEl.Clone(); // JsonElement ties to `doc`, which is disposed at method exit
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
        }
        catch { /* malformed message from the plugin — ignore, keep the connection alive */ }
    }

    /// <summary>loadScript is the one op that's genuinely async (file I/O) — awaited here, still
    /// on the UI thread's continuation, before sending the correlated result back.</summary>
    private async Task HandleLoadScriptAsync(WebSocket socket, string? id, string path)
    {
        var (success, error) = await _loadScript(path);
        await SendResultAsync(socket, id, success, error);
    }

    /// <summary>Sends a correlated result back to exactly the requesting client — never
    /// broadcast to all clients, unlike state pushes.</summary>
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

    /// <summary>getState requests arrive on this client's background receive-loop thread, so
    /// (unlike NotifyStateMayHaveChanged) this one does need to hop onto the UI thread itself.</summary>
    private void BroadcastCurrentStateFromBackgroundThread() =>
        _uiQueue.TryEnqueue(BroadcastCurrentState);

    /// <summary>Sends the current state to exactly one newly-connected client. Called from the
    /// accept loop's background thread, so — unlike <see cref="NotifyStateMayHaveChanged"/> —
    /// this hops onto the UI thread itself to read state safely.</summary>
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
