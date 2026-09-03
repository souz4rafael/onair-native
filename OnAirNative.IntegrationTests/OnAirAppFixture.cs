using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace OnAirNative.IntegrationTests;

/// <summary>
/// Launches the real, already-built OnAirNative.exe as a child process and drives it purely over
/// the RemoteControlService WebSocket protocol (127.0.0.1:47823) — the exact same transport and
/// message shapes the Stream Deck plugin and MCP server use. This turns the ad-hoc Node
/// WebSocket scripts used earlier this session (to test the chapter/formatting feature without a
/// UI-automatable file picker) into a checked-in, repeatable xUnit suite.
///
/// One instance of this fixture is shared across every [Fact] in a test class via
/// IClassFixture&lt;OnAirAppFixture&gt; — the app is launched exactly once per test class run,
/// not once per test method (WinUI startup is too slow/heavy to pay per-test).
///
/// Isolation: sets ONAIR_CONFIG_DIR to a fresh temp directory on the child process before
/// launch, so the real developer/CI-machine %LocalAppData%\onAIr\config.json is never read or
/// written by these tests (mirrors the same isolation discipline as ConfigServiceTests/
/// ToolGateTests — see App.xaml.cs's ONAIR_CONFIG_DIR handling). RemoteControlEnabled defaults to
/// true in a fresh config, so the WebSocket server starts without any extra setup.
///
/// Message pump design: a single background loop owns the real socket ReceiveAsync calls and
/// pushes every parsed message into an unbounded Channel. This is deliberate, not incidental —
/// .NET's ClientWebSocket permanently ABORTS the connection if a pending ReceiveAsync is
/// cancelled (this is documented framework behavior, not a bug), so a naive
/// "cancel-after-my-timeout" helper wrapped directly around the socket would corrupt the
/// connection for every later test the moment any single wait legitimately timed out. Reading
/// from the in-memory Channel instead means callers can cancel their own wait after any timeout
/// they like with zero risk to the underlying socket — only the channel read is cancelled, never
/// the socket's own receive loop.
/// </summary>
public sealed class OnAirAppFixture : IAsyncLifetime
{
    /// <summary>Must match RemoteControlService.Port exactly (mirrored here, not referenced,
    /// since this project intentionally has zero dependency on the WinUI project's assembly).</summary>
    public const int Port = 47823;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private Process? _process;
    private ClientWebSocket? _socket;
    private string? _tempConfigDir;
    private int _nextId;

    private readonly Channel<JsonDocument> _incoming = Channel.CreateUnbounded<JsonDocument>();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _receiveLoopTask;

    /// <summary>Path to the OnAirNative.exe this fixture launched — exposed for diagnostics in
    /// test failure messages.</summary>
    public string ExePath { get; private set; } = "";

    public async Task InitializeAsync()
    {
        ExePath = ResolveExePath();
        _tempConfigDir = Path.Combine(Path.GetTempPath(), "OnAirIntegrationTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempConfigDir);

        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute  = false,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        psi.Environment["ONAIR_CONFIG_DIR"] = _tempConfigDir;

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for '{ExePath}'.");

        try
        {
            _socket = await ConnectWithRetryAsync(TimeSpan.FromSeconds(30));
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_lifetimeCts.Token));

            // First frame after connecting is always an unsolicited "state" broadcast (see
            // RemoteControlService.SendStateToNewClientAsync) — drain it so later per-test reads
            // start from a clean slate.
            await ReadNextAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            KillProcess();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        _lifetimeCts.Cancel();
        try { _socket?.Dispose(); } catch { /* best-effort cleanup */ }
        if (_receiveLoopTask is not null)
        {
            try { await _receiveLoopTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
        }
        KillProcess();

        if (_tempConfigDir is not null && Directory.Exists(_tempConfigDir))
        {
            try { Directory.Delete(_tempConfigDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
        _lifetimeCts.Dispose();
    }

    private void KillProcess()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch { /* best-effort cleanup — never let teardown itself fail the test run */ }
        finally { _process.Dispose(); }
    }

    /// <summary>Locates OnAirNative.exe: ONAIR_EXE_PATH env var if set (what CI should use,
    /// pointing at whatever path its own build step produced), otherwise walks up from the test
    /// assembly's own output directory to find OnAirNative.sln and descends into the well-known
    /// Debug/win-x64 build output path documented in DEVELOPMENT.md.</summary>
    private static string ResolveExePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("ONAIR_EXE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!File.Exists(overridePath))
                throw new FileNotFoundException($"ONAIR_EXE_PATH is set but no file exists there: {overridePath}");
            return overridePath;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OnAirNative.sln")))
            dir = dir.Parent;

        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not find OnAirNative.sln by walking up from the test output directory. " +
                "Set the ONAIR_EXE_PATH environment variable to the built OnAirNative.exe explicitly.");
        }

        var candidate = Path.Combine(
            dir.FullName, "OnAirNative", "bin", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "OnAirNative.exe");
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                "OnAirNative.exe not found at the expected Debug build output path — build " +
                $"OnAirNative.sln first, or set ONAIR_EXE_PATH.\nExpected: {candidate}");
        }
        return candidate;
    }

    private static async Task<ClientWebSocket> ConnectWithRetryAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            var socket = new ClientWebSocket();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/"), cts.Token);
                return socket;
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
                await Task.Delay(500);
            }
        }
        throw new TimeoutException(
            $"Could not connect to RemoteControlService at ws://127.0.0.1:{Port}/ within {timeout}. " +
            $"Last error: {lastError?.Message}");
    }

    /// <summary>Owns the only ReceiveAsync calls against the real socket for its whole lifetime —
    /// see the class doc comment for why this must never be cancelled by a per-request timeout.
    /// Runs until the socket closes/errors or the fixture itself is disposed, then completes the
    /// channel (propagating the failure reason to any current/future reader).</summary>
    private async Task ReceiveLoopAsync(CancellationToken lifetimeToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket!.ReceiveAsync(buffer, lifetimeToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                ms.Position = 0;
                var doc = await JsonDocument.ParseAsync(ms, cancellationToken: lifetimeToken);
                await _incoming.Writer.WriteAsync(doc, lifetimeToken);
            }
            _incoming.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            _incoming.Writer.TryComplete(); // normal shutdown via DisposeAsync
        }
        catch (Exception ex)
        {
            // Real disconnect/error — surface it to whatever is currently (or next) awaiting a
            // channel read, instead of leaving it to hang until its own timeout expires.
            _incoming.Writer.TryComplete(ex);
        }
    }

    /// <summary>Reads the next message off the in-memory channel (NOT the socket directly —
    /// see class doc comment), cancelling only the channel read on timeout. Safe to call
    /// repeatedly after a prior call has timed out.</summary>
    private async Task<JsonDocument> ReadNextAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _incoming.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"No message received from OnAirNative within {timeout}.");
        }
        catch (ChannelClosedException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException("The connection to OnAirNative closed unexpectedly.", ex.InnerException);
        }
    }

    public async Task SendAsync(object payload)
    {
        if (_socket is null) throw new InvalidOperationException("Not connected.");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>Sends a fire-and-forget "command" op (mirrors a Stream Deck button press / global
    /// hotkey). Per RemoteControlService's protocol, this — like "adjust" and "getState" — never
    /// gets a correlated result; the server relies purely on a subsequent state broadcast, which
    /// callers should observe via <see cref="WaitForStateAsync"/>.</summary>
    public Task SendCommandAsync(string hotkeyAction) => SendAsync(new { op = "command", action = hotkeyAction });

    /// <summary>Sends "getState" (fire-and-forget, like command/adjust — no correlated result)
    /// and waits for the next state broadcast it provokes.</summary>
    public async Task<JsonElement> GetStateAsync(TimeSpan? timeout = null)
    {
        await SendAsync(new { op = "getState" });
        return await WaitForStateAsync(_ => true, timeout ?? TimeSpan.FromSeconds(10));
    }

    /// <summary>Sends a request op that gets a correlated {"op":"result","id":...} reply — only
    /// valid for set/loadScript/getScriptText/listFonts (NOT command/adjust/getState, which are
    /// fire-and-forget per RemoteControlService's protocol; use SendCommandAsync/GetStateAsync
    /// for those instead). Transparently skips over any state broadcasts interleaved on the same
    /// connection while waiting for the matching id.</summary>
    public async Task<JsonDocument> RequestAsync(string op, IDictionary<string, object?>? extra = null, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var payload = new Dictionary<string, object?> { ["op"] = op, ["id"] = id };
        if (extra is not null)
            foreach (var (k, v) in extra) payload[k] = v;
        await SendAsync(payload);

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"No 'result' reply for op='{op}' id='{id}' within {effectiveTimeout}.");

            var doc = await ReadNextAsync(remaining);
            var root = doc.RootElement;
            if (root.TryGetProperty("op", out var opEl) && opEl.GetString() == "result" &&
                root.TryGetProperty("id", out var idEl) && idEl.GetString() == id)
            {
                return doc;
            }
            doc.Dispose(); // not the reply we're waiting for (likely an interleaved state broadcast) — discard
        }
    }

    /// <summary>Polls incoming "state" broadcasts until one satisfies <paramref name="predicate"/>
    /// or the timeout elapses — used after a fire-and-forget command/adjust/getState op.</summary>
    public async Task<JsonElement> WaitForStateAsync(Func<JsonElement, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"No matching 'state' broadcast within {timeout}.");

            using var doc = await ReadNextAsync(remaining);
            var root = doc.RootElement;
            if (root.TryGetProperty("op", out var opEl) && opEl.GetString() == "state" &&
                root.TryGetProperty("data", out var dataEl) && predicate(dataEl))
            {
                return dataEl.Clone();
            }
        }
    }
}
