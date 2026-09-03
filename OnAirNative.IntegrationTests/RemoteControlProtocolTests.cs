using System.Text.Json;

namespace OnAirNative.IntegrationTests;

/// <summary>
/// End-to-end tests driving a real, running OnAirNative.exe purely over the
/// RemoteControlService WebSocket protocol — the same transport the Stream Deck plugin and MCP
/// server use. Complements OnAirNative.Tests (pure logic, no live app) and OnAirMcp.Tests (the
/// MCP server's ToolGate in isolation): this is the one layer that actually exercises the real
/// running WinUI app end-to-end, catching wiring/regression bugs the other two tiers structurally
/// cannot (e.g. a protocol field rename on one side of the JSON contract, or ExecuteAction not
/// actually being reachable from the WebSocket handler).
///
/// Requires a real interactive Windows desktop session to launch the WinUI app (this is why it's
/// a separate CI job/tier from the plain unit tests — see ci.yml comments). Every test is
/// self-contained (does not assume another test's ordering or side effects — xUnit does not
/// guarantee execution order within a class) and shares one OnAirAppFixture-launched process via
/// IClassFixture, so the app is started exactly once for this whole class.
///
/// Note: getState/command/adjust are fire-and-forget per RemoteControlService's protocol (no
/// correlated "result" reply — only a subsequent "state" broadcast), so this class uses
/// OnAirAppFixture.GetStateAsync()/WaitForStateAsync() for those, and RequestAsync() only for the
/// four ops that really do reply with a correlated result: set/loadScript/getScriptText/listFonts.
/// </summary>
public class RemoteControlProtocolTests : IClassFixture<OnAirAppFixture>
{
    private readonly OnAirAppFixture _app;

    public RemoteControlProtocolTests(OnAirAppFixture app) => _app = app;

    [Fact]
    public async Task GetState_ReturnsExpectedShape()
    {
        var data = await _app.GetStateAsync();

        // Spot-check a representative field of each JSON type in RemoteState — a full field-by-
        // field check would just re-type the class and add no real signal.
        Assert.True(data.TryGetProperty("tpOpen", out var tpOpen) && tpOpen.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(data.TryGetProperty("fontSize", out var fontSize) && fontSize.GetInt32() > 0);
        Assert.True(data.TryGetProperty("opacity", out var opacity) && opacity.GetDouble() > 0);
        Assert.True(data.TryGetProperty("chatProvider", out var chatProvider) && chatProvider.ValueKind == JsonValueKind.String);
        Assert.True(data.TryGetProperty("scrollMode", out var scrollMode) &&
            scrollMode.GetString() is "Manual" or "Auto" or "Voice");
    }

    [Fact]
    public async Task SetFontSize_ThenGetState_ReflectsNewValue()
    {
        using (var setResult = await _app.RequestAsync("set", new Dictionary<string, object?> { ["field"] = "FontSize", ["value"] = 33 }))
        {
            Assert.True(setResult.RootElement.GetProperty("success").GetBoolean());
        }

        var data = await _app.GetStateAsync();
        Assert.Equal(33, data.GetProperty("fontSize").GetInt32());
    }

    [Fact]
    public async Task SetFontSize_OutOfRangeValue_ClampsRatherThanFailing()
    {
        // FontSizeSlider spans 10-64 (ControllerWindow.xaml.cs) — SetRemoteField clamps rather
        // than rejecting, so an over-range dial/MCP request never leaves the app in a broken state.
        using var setResult = await _app.RequestAsync("set", new Dictionary<string, object?> { ["field"] = "FontSize", ["value"] = 999 });
        Assert.True(setResult.RootElement.GetProperty("success").GetBoolean());

        var data = await _app.GetStateAsync();
        Assert.Equal(64, data.GetProperty("fontSize").GetInt32());
    }

    [Fact]
    public async Task LoadScript_ThenGetScriptText_RoundTripsExactContent()
    {
        var content = "# Chapter One\r\nSome **bold** and *italic* text.\r\n\r\n## Section A\r\nMore body text.";
        var tempScript = Path.Combine(Path.GetTempPath(), $"onair-integration-{Guid.NewGuid()}.txt");
        await File.WriteAllTextAsync(tempScript, content);
        try
        {
            using var loadResult = await _app.RequestAsync("loadScript", new Dictionary<string, object?> { ["path"] = tempScript });
            Assert.True(loadResult.RootElement.GetProperty("success").GetBoolean());

            using var textResult = await _app.RequestAsync("getScriptText");
            Assert.True(textResult.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(content, textResult.RootElement.GetProperty("data").GetString());
        }
        finally
        {
            File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task LoadScript_NonExistentPath_ReturnsFailureNotException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"onair-integration-missing-{Guid.NewGuid()}.txt");

        using var result = await _app.RequestAsync("loadScript", new Dictionary<string, object?> { ["path"] = missingPath });

        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.True(result.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task ListFonts_ReturnsNonEmptyList()
    {
        using var result = await _app.RequestAsync("listFonts");

        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        var fonts = result.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, fonts.ValueKind);
        Assert.True(fonts.GetArrayLength() > 0); // any real Windows machine has some fonts installed
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
            // Restore the original tpOpen state so this test has no lasting side effect on
            // whichever other test happens to run against the same shared app instance next.
            await _app.SendCommandAsync("ToggleOverlayVisibility");
            await _app.WaitForStateAsync(data => data.GetProperty("tpOpen").GetBoolean() == originalTpOpen, TimeSpan.FromSeconds(15));
        }
    }

    // ── Q&A monitoring + Copilot insights (Block 6) ───────────────────────────

    [Fact]
    public async Task GetState_IncludesQaMonitoringFields()
    {
        var data = await _app.GetStateAsync();

        // Fresh app, nothing recorded yet — spot-check the Block 6 fields exist with sane
        // "nothing has happened" defaults, same "representative field per type" spirit as
        // GetState_ReturnsExpectedShape above.
        Assert.True(data.TryGetProperty("lastQuestion", out var q) && q.ValueKind == JsonValueKind.String);
        Assert.True(data.TryGetProperty("lastAnswer", out var a) && a.ValueKind == JsonValueKind.String);
        Assert.True(data.TryGetProperty("qaTurnCount", out var count) && count.GetInt32() >= 0);
        Assert.True(data.TryGetProperty("pacingSummary", out var pacing) && pacing.ValueKind == JsonValueKind.String);
        Assert.True(data.TryGetProperty("followUpSuggestions", out var suggestions) && suggestions.ValueKind == JsonValueKind.Array);
        Assert.True(data.TryGetProperty("qaSessionActive", out var sessionActive) && sessionActive.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(data.TryGetProperty("insightText", out var insight) && insight.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task ShowInsight_ThenGetState_ReflectsInsightText()
    {
        try
        {
            using var showResult = await _app.RequestAsync("showInsight", new Dictionary<string, object?> { ["text"] = "Integration test insight" });
            Assert.True(showResult.RootElement.GetProperty("success").GetBoolean());

            var data = await _app.GetStateAsync();
            Assert.Equal("Integration test insight", data.GetProperty("insightText").GetString());
        }
        finally
        {
            await _app.RequestAsync("clearInsight");
        }
    }

    [Fact]
    public async Task ClearInsight_AfterShowInsight_EmptiesInsightText()
    {
        using (var showResult = await _app.RequestAsync("showInsight", new Dictionary<string, object?> { ["text"] = "Temporary insight" }))
            Assert.True(showResult.RootElement.GetProperty("success").GetBoolean());

        using var clearResult = await _app.RequestAsync("clearInsight");
        Assert.True(clearResult.RootElement.GetProperty("success").GetBoolean());

        var data = await _app.GetStateAsync();
        Assert.Equal("", data.GetProperty("insightText").GetString());
    }

    [Fact]
    public async Task ShowInsight_BlankText_ReturnsFailure()
    {
        using var result = await _app.RequestAsync("showInsight", new Dictionary<string, object?> { ["text"] = "" });

        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.True(result.RootElement.TryGetProperty("error", out _));
    }
}
