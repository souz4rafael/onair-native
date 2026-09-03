using System.Text.Json;

namespace OnAirMcp;

/// <summary>
/// Reads which MCP tools the user has disabled directly from onAIr's own config.json
/// (%LocalAppData%\onAIr\config.json — same file, same path logic as
/// OnAirNative/Services/ConfigService.cs) — re-read fresh on every check rather than cached, so
/// a toggle flipped in onAIr's Settings dialog takes effect on this MCP server's very next tool
/// call, without needing to restart the long-lived stdio process (which may stay alive across
/// many chat turns once registered with an MCP client).
///
/// Deliberately reads only the one field it needs (McpDisabledTools) via a minimal local record,
/// rather than referencing OnAirNative's actual AppConfig type — mirrors the same lightweight
/// "duplicate just the shape you need" pattern already used for RemoteState in this project and
/// for the Stream Deck plugin's onair-client.ts. This class never touches (or even deserializes)
/// any of the encrypted provider API key fields elsewhere in config.json.
/// </summary>
public static class ToolGate
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "onAIr", "config.json");

    private sealed record MinimalConfig(List<string>? McpDisabledTools);

    /// <summary>True if the named tool (e.g. "onair_set_font_color") is currently disabled.
    /// Fails open (returns false / "enabled") on any read/parse error — a malformed or briefly
    /// mid-write config.json must never accidentally lock every tool out.</summary>
    /// <param name="configPathOverride">Overrides which config.json is read — used by
    /// OnAirMcp.Tests to point at an isolated temp file instead of the real
    /// %LocalAppData%\onAIr\config.json (never touch the developer's own real settings from a
    /// test run).</param>
    public static bool IsDisabled(string toolName, string? configPathOverride = null)
    {
        try
        {
            var path = configPathOverride ?? ConfigPath;
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<MinimalConfig>(json, JsonOptions);
            return config?.McpDisabledTools?.Contains(toolName) ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Standard "blocked" message returned by a disabled tool — consistent wording so
    /// the LLM/user immediately understands this was a deliberate settings choice, not a bug.</summary>
    public static string DisabledMessage(string toolName) =>
        $"Error: The '{toolName}' tool has been disabled in onAIr → Settings → REMOTE CONTROL → " +
        "MCP Tools & Setup. Enable it there to use this tool.";
}
