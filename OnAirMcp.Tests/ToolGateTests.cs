using System.Text.Json;
using OnAirMcp;

namespace OnAirMcp.Tests;

/// <summary>
/// Verifies ToolGate.IsDisabled against isolated temp config files only — never the real
/// %LocalAppData%\onAIr\config.json (same isolation discipline as OnAirNative.Tests'
/// ConfigServiceTests, using the configPathOverride parameter added specifically for tests).
/// </summary>
public class ToolGateTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), "OnAirMcpTests_" + Guid.NewGuid() + ".json");

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    private void WriteConfig(object config) =>
        File.WriteAllText(_tempFile, JsonSerializer.Serialize(config));

    [Fact]
    public void IsDisabled_FileDoesNotExist_FailsOpenReturnsFalse()
    {
        Assert.False(ToolGate.IsDisabled("onair_set_font_color", _tempFile));
    }

    [Fact]
    public void IsDisabled_ToolNotInDisabledList_ReturnsFalse()
    {
        WriteConfig(new { mcpDisabledTools = new[] { "onair_load_script" } });

        Assert.False(ToolGate.IsDisabled("onair_set_font_color", _tempFile));
    }

    [Fact]
    public void IsDisabled_ToolInDisabledList_ReturnsTrue()
    {
        WriteConfig(new { mcpDisabledTools = new[] { "onair_set_font_color", "onair_load_script" } });

        Assert.True(ToolGate.IsDisabled("onair_set_font_color", _tempFile));
    }

    [Fact]
    public void IsDisabled_NullDisabledToolsList_ReturnsFalse()
    {
        WriteConfig(new { provider = "azure" }); // no mcpDisabledTools field at all

        Assert.False(ToolGate.IsDisabled("onair_set_font_color", _tempFile));
    }

    [Fact]
    public void IsDisabled_EmptyDisabledToolsList_ReturnsFalse()
    {
        WriteConfig(new { mcpDisabledTools = Array.Empty<string>() });

        Assert.False(ToolGate.IsDisabled("onair_set_font_color", _tempFile));
    }

    [Fact]
    public void IsDisabled_MalformedJson_FailsOpenReturnsFalseRatherThanThrowing()
    {
        File.WriteAllText(_tempFile, "{ this is not valid json ][");

        Assert.False(ToolGate.IsDisabled("onair_set_font_color", _tempFile));
    }

    [Fact]
    public void IsDisabled_CaseSensitiveToolName_DoesNotMatchDifferentCasing()
    {
        // Tool names are exact identifiers (e.g. "onair_set_font_color") — confirms no
        // accidental case-insensitive matching was introduced via the JSON deserialization.
        WriteConfig(new { mcpDisabledTools = new[] { "onair_set_font_color" } });

        Assert.False(ToolGate.IsDisabled("ONAIR_SET_FONT_COLOR", _tempFile));
    }

    [Fact]
    public void DisabledMessage_MentionsToolNameAndSettingsLocation()
    {
        var message = ToolGate.DisabledMessage("onair_set_font_color");

        Assert.Contains("onair_set_font_color", message);
        Assert.Contains("Settings", message);
        Assert.Contains("MCP Tools & Setup", message);
    }
}
