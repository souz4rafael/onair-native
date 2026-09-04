using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace OnAirNative.IntegrationTests;

/// <summary>
/// Sibling of <see cref="OnAirAppFixture"/> for testing <c>WebRemoteService</c> (port 47824,
/// PIN-gated) instead of RemoteControlService (port 47823, loopback-only, no PIN). Launches its
/// own OnAirNative.exe child process with an isolated ONAIR_CONFIG_DIR, exactly like
/// OnAirAppFixture — but unlike that fixture, this one must pre-seed the temp config.json with
/// <c>webRemote.enabled = true</c> and a fixed test PIN BEFORE the process starts, since
/// WebRemote.Enabled defaults to false (unlike RemoteControlEnabled, which defaults to true) —
/// ConfigService.Load() runs synchronously in its constructor, so the file must already exist on
/// disk at that point.
///
/// Duplicates rather than reuses OnAirAppFixture's process-launch/receive-loop plumbing —
/// consistent with this project's established "isolation over DRY" choice for
/// WebRemoteService vs RemoteControlService itself (see that class's doc comment): keeping the
/// two fixtures independent means a change to one protocol's test harness can never accidentally
/// affect the other's.
/// </summary>
public sealed class WebRemoteAppFixture : IAsyncLifetime
{
    /// <summary>Must match WebRemoteService.Port exactly (mirrored here, not referenced, since
    /// this project intentionally has zero dependency on the WinUI project's assembly).</summary>
    public const int Port = 47824;

    /// <summary>Fixed test PIN pre-seeded into the isolated config.json this fixture writes
    /// before launch — arbitrary 6 digits, never a real user's PIN.</summary>
    public const string Pin = "135790";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private Process? _process;
    private ClientWebSocket? _socket;
    private string? _tempConfigDir;
    private int _nextId;

    private readonly Channel<JsonDocument> _incoming = Channel.CreateUnbounded<JsonDocument>();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _receiveLoopTask;

    /// <summary>Plain HTTP client for the static-file-serving tests (GET /, /app.js, /style.css)
    /// — those requests are deliberately unauthenticated, unlike the WebSocket upgrade.</summary>
    public HttpClient Http { get; } = new() { BaseAddress = new Uri($"http://127.0.0.1:{Port}/") };

    /// <summary>Path to the OnAirNative.exe this fixture launched — exposed for diagnostics in
    /// test failure messages.</summary>
    public string ExePath { get; private set; } = "";

    public async Task InitializeAsync()
    {
        ExePath = ResolveExePath();
        _tempConfigDir = Path.Combine(Path.GetTempPath(), "OnAirWebRemoteIntegrationTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempConfigDir);

        var seedConfig = $$"""
            {
              "webRemote": { "enabled": true, "pin": "{{Pin}}" }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_tempConfigDir, "config.json"), seedConfig);

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
            // Give the HTTP listener a head start before the WS handshake retry loop, and confirm
            // it's actually serving (also exercises the static-file path as a side effect).
            await WaitForHttpReadyAsync(TimeSpan.FromSeconds(30));

            _socket = await ConnectWithRetryAsync(TimeSpan.FromSeconds(30));
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_lifetimeCts.Token));

            // First frame after connecting is always an unsolicited "state" broadcast (see
            // WebRemoteService.SendStateToNewClientAsync) — drain it so later per-test reads
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
        Http.Dispose();

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

    /// <summary>Identical strategy to OnAirAppFixture.ResolveExePath — see that method's doc
    /// comment for the rationale (ONAIR_EXE_PATH override, else walk up to OnAirNative.sln and
    /// descend into the well-known Debug/win-x64 output path).</summary>
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

    private async Task WaitForHttpReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var resp = await Http.GetAsync("/", cts.Token);
                if (resp.IsSuccessStatusCode) return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"WebRemoteService HTTP endpoint never became ready within {timeout}. Last error: {lastError?.Message}");
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
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/?pin={Pin}"), cts.Token);
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
            $"Could not connect to WebRemoteService at ws://127.0.0.1:{Port}/ within {timeout}. " +
            $"Last error: {lastError?.Message}");
    }

    /// <summary>One-shot connection attempt (no retry) with an arbitrary/missing PIN — used by
    /// tests asserting the server rejects bad credentials. Returns normally if the WS upgrade
    /// succeeds (test should Dispose the returned socket), throws WebSocketException otherwise.</summary>
    public static async Task<ClientWebSocket> ConnectOnceAsync(string? pin)
    {
        var socket = new ClientWebSocket();
        var url = pin is null ? $"ws://127.0.0.1:{Port}/" : $"ws://127.0.0.1:{Port}/?pin={pin}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.ConnectAsync(new Uri(url), cts.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Owns the only ReceiveAsync calls against the real socket for its whole lifetime —
    /// see OnAirAppFixture's class doc comment for why this must never be cancelled by a
    /// per-request timeout. Runs until the socket closes/errors or the fixture itself is
    /// disposed, then completes the channel (propagating the failure reason to any current/future
    /// reader).</summary>
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
            _incoming.Writer.TryComplete(ex);
        }
    }

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

    public Task SendCommandAsync(string hotkeyAction) => SendAsync(new { op = "command", action = hotkeyAction });

    public async Task<JsonElement> GetStateAsync(TimeSpan? timeout = null)
    {
        await SendAsync(new { op = "getState" });
        return await WaitForStateAsync(_ => true, timeout ?? TimeSpan.FromSeconds(10));
    }

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
            doc.Dispose();
        }
    }

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
