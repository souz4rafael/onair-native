using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace OnAirNative.IntegrationTests;

/// <summary>
/// End-to-end tests driving a real, running OnAirNative.exe purely over the WebRemoteService
/// HTTP + WebSocket surface (port 47824) — the LAN-reachable sibling of RemoteControlService
/// exercised by RemoteControlProtocolTests. Covers the two things unique to WebRemoteService
/// that RemoteControlProtocolTests structurally cannot: (1) static-file serving of the control
/// page, including that path-traversal attempts are neutralized, and (2) the PIN gate on the
/// WebSocket upgrade. Protocol op coverage itself (set/getState/showInsight/etc.) intentionally
/// stays light here since it's the exact same handler code paths already covered in depth by
/// RemoteControlProtocolTests — this class only spot-checks that WebRemoteService's independent
/// duplicate of that glue actually works, not every op's business logic again.
/// </summary>
public class WebRemoteProtocolTests : IClassFixture<WebRemoteAppFixture>
{
    private readonly WebRemoteAppFixture _app;

    public WebRemoteProtocolTests(WebRemoteAppFixture app) => _app = app;

    // ── Static file serving ────────────────────────────────────────────────

    [Fact]
    public async Task GetIndex_ReturnsControlPageHtml()
    {
        using var resp = await _app.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("onAIr Remote", body);
    }

    [Fact]
    public async Task GetAppJs_ReturnsJavaScript()
    {
        using var resp = await _app.Http.GetAsync("/app.js");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("javascript", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(body.Length > 0);
    }

    [Fact]
    public async Task GetStyleCss_ReturnsCss()
    {
        using var resp = await _app.Http.GetAsync("/style.css");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/css", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetUnknownPath_Returns404()
    {
        using var resp = await _app.Http.GetAsync("/nope-this-does-not-exist.html");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetPathTraversalAttempt_DoesNotEscapeAssetsDir()
    {
        // Path.GetFileName() strips any directory segments server-side, so this can only ever
        // resolve to a literal file named "windows" (which doesn't exist) inside AssetsDir — it
        // must never be interpreted as an actual traversal outside Assets/web-remote/.
        using var resp = await _app.Http.GetAsync("/../../../../windows/win.ini");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── PIN gate on the WebSocket upgrade ──────────────────────────────────

    [Fact]
    public async Task Connect_WithoutPin_IsRejected()
    {
        await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            using var socket = await WebRemoteAppFixture.ConnectOnceAsync(pin: null);
        });
    }

    [Fact]
    public async Task Connect_WithWrongPin_IsRejected()
    {
        await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            using var socket = await WebRemoteAppFixture.ConnectOnceAsync(pin: "000000");
        });
    }

    [Fact]
    public async Task Connect_WithCorrectPin_Succeeds()
    {
        using var socket = await WebRemoteAppFixture.ConnectOnceAsync(WebRemoteAppFixture.Pin);
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    // ── Protocol spot-check (shared handler code, already covered in depth by
    //    RemoteControlProtocolTests — just confirming this independent duplicate works) ───────

    [Fact]
    public async Task GetState_ReturnsExpectedShape()
    {
        var data = await _app.GetStateAsync();

        Assert.True(data.TryGetProperty("tpOpen", out var tpOpen) && tpOpen.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(data.TryGetProperty("fontSize", out var fontSize) && fontSize.GetInt32() > 0);
        Assert.True(data.TryGetProperty("insightText", out var insight) && insight.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task ShowInsight_ThenGetState_ReflectsInsightText()
    {
        try
        {
            using var showResult = await _app.RequestAsync("showInsight", new Dictionary<string, object?> { ["text"] = "Web remote integration test" });
            Assert.True(showResult.RootElement.GetProperty("success").GetBoolean());

            var data = await _app.GetStateAsync();
            Assert.Equal("Web remote integration test", data.GetProperty("insightText").GetString());
        }
        finally
        {
            await _app.RequestAsync("clearInsight");
        }
    }

    [Fact]
    public async Task ToggleOverlayVisibility_FlipsTpOpenInSubsequentStateBroadcast()
    {
        var before = await _app.GetStateAsync();
        var originalTpOpen = before.GetProperty("tpOpen").GetBoolean();

        try
        {
            await _app.SendCommandAsync("ToggleOverlayVisibility");
            var flipped = await _app.WaitForStateAsync(
                data => data.GetProperty("tpOpen").GetBoolean() != originalTpOpen,
                TimeSpan.FromSeconds(15));

            Assert.Equal(!originalTpOpen, flipped.GetProperty("tpOpen").GetBoolean());
        }
        finally
        {
            await _app.SendCommandAsync("ToggleOverlayVisibility");
            await _app.WaitForStateAsync(data => data.GetProperty("tpOpen").GetBoolean() == originalTpOpen, TimeSpan.FromSeconds(15));
        }
    }
}
