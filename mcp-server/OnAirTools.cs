using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace OnAirMcp;

/// <summary>
/// The onAIr MCP tool surface — lets any MCP client (Claude Desktop, VS Code Copilot Chat, etc.)
/// control the onAIr teleprompter app via natural language. Every tool is a thin wrapper over
/// <see cref="OnAirClient"/>, which talks to onAIr's existing RemoteControlService
/// (OnAirNative/Services/RemoteControlService.cs) — the same loopback WebSocket server the
/// Stream Deck plugin uses. No new attack surface: same 127.0.0.1-only trust boundary.
///
/// Every tool is individually toggleable by the user (onAIr → Settings → REMOTE CONTROL →
/// "MCP Tools & Setup…") — enforced here via <see cref="ToolGate"/>, not just hidden from the
/// tool list, so a disabled tool always returns a clear message instead of silently doing
/// nothing or (worse) still working because some other MCP client cached an old tool list.
///
/// SECURITY: never add a tool or return value here that exposes provider API keys (Azure/
/// OpenAI/Groq/Anthropic/Gemini/Mistral) or any other raw config.json content beyond the
/// specific, already-public RemoteState fields — RemoteControlService itself never sends
/// credentials over the wire, and this client must not either.
/// </summary>
[McpServerToolType]
public static class OnAirTools
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>Wraps a tool body with the enable/disable gate check, then any
    /// <see cref="OnAirClient"/> failure (onAIr not running, connection dropped, request timed
    /// out) becomes a clear, readable error string for the MCP client/LLM instead of an
    /// unhandled exception.</summary>
    private static async Task<string> SafeAsync(string toolName, Func<Task<string>> action)
    {
        if (ToolGate.IsDisabled(toolName)) return ToolGate.DisabledMessage(toolName);
        try { return await action(); }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "onair_is_running"), Description("Checks whether onAIr is running and reachable right now. Call this first if unsure, before other onAIr tools.")]
    public static Task<string> OnairIsRunning() => SafeAsync("onair_is_running", async () =>
    {
        await OnAirClient.Instance.GetStateAsync();
        return "onAIr is running and reachable.";
    });

    [McpServerTool(Name = "onair_get_state"), Description("Gets the full current state of onAIr: whether the teleprompter (TP) is open, locked, or hidden from screen share; whether the Controller window is hidden from screen share; recording status; the active AI chat provider; Whisper transcription model status (local vs cloud); font size, color, and family; teleprompter opacity; scroll mode (Manual/Auto/Voice) and its speed/step settings; the loaded script's filename; AND Q&A monitoring fields — the most recent question/answer, a turn counter that increments each completed Q&A round (poll this to detect new activity), the pacing (words-per-minute) summary, follow-up suggestions, whether a Q&A session recording is active, and the current Copilot-insight footer text. Use this before deciding what action to take, to answer any status question, or to monitor for new Q&A activity.")]
    public static Task<string> OnairGetState() => SafeAsync("onair_get_state", async () =>
    {
        var state = await OnAirClient.Instance.GetStateAsync();
        return JsonSerializer.Serialize(state, PrettyJson);
    });

    [McpServerTool(Name = "onair_get_last_qa_turn"), Description("Gets the most recently completed Q&A round: the transcribed question, the AI's answer, a turn counter (increments once per completed round — remember the last value you saw and compare to detect a NEW turn without re-reading the same one), the pacing (words-per-minute) summary, and follow-up question suggestions (if enabled). Use this to monitor onAIr's Q&A activity in real time, e.g. polling every few seconds during a live presentation to react to new questions as they're answered.")]
    public static Task<string> OnairGetLastQaTurn() => SafeAsync("onair_get_last_qa_turn", async () =>
    {
        var state = await OnAirClient.Instance.GetStateAsync();
        return JsonSerializer.Serialize(new
        {
            state.QaTurnCount,
            state.LastQuestion,
            state.LastAnswer,
            state.PacingSummary,
            state.FollowUpSuggestions,
            state.QaSessionActive,
        }, PrettyJson);
    });

    [McpServerTool(Name = "onair_get_script_text"), Description("Gets the full text of the script currently loaded in onAIr's teleprompter.")]
    public static Task<string> OnairGetScriptText() => SafeAsync("onair_get_script_text", () => OnAirClient.Instance.GetScriptTextAsync());

    [McpServerTool(Name = "onair_list_fonts"), Description("Lists every font family installed on this PC. Use this to check a font name is valid before calling onair_set_font_family.")]
    public static Task<string> OnairListFonts() => SafeAsync("onair_list_fonts", async () =>
    {
        var fonts = await OnAirClient.Instance.ListFontsAsync();
        return JsonSerializer.Serialize(fonts, PrettyJson);
    });

    [McpServerTool(Name = "onair_toggle_tp"), Description("Opens the teleprompter (TP) overlay window if it's currently closed, or closes it if it's currently open. Toggles the current state — call onair_get_state first if you need to know which way it will flip.")]
    public static Task<string> OnairToggleTp() => SafeAsync("onair_toggle_tp", async () =>
    {
        await OnAirClient.Instance.SendCommandAsync("ToggleOverlayVisibility");
        return "Toggled the teleprompter open/closed.";
    });

    [McpServerTool(Name = "onair_toggle_lock"), Description("Locks the teleprompter (TP) window in place (click-through, can't be accidentally moved) if unlocked, or unlocks it (movable/resizable) if locked. Toggles the current state.")]
    public static Task<string> OnairToggleLock() => SafeAsync("onair_toggle_lock", async () =>
    {
        await OnAirClient.Instance.SendCommandAsync("ToggleMoveMode");
        return "Toggled the teleprompter lock.";
    });

    [McpServerTool(Name = "onair_toggle_hide_tp"), Description("Toggles whether the teleprompter (TP) window is hidden from screen sharing/recording — useful when going live on a shared screen. Toggles the current state.")]
    public static Task<string> OnairToggleHideTp() => SafeAsync("onair_toggle_hide_tp", async () =>
    {
        await OnAirClient.Instance.SendCommandAsync("ToggleOverlayCaptureProtection");
        return "Toggled teleprompter visibility in screen share.";
    });

    [McpServerTool(Name = "onair_toggle_hide_controller"), Description("Toggles whether the onAIr Controller window is hidden from screen sharing/recording. Toggles the current state.")]
    public static Task<string> OnairToggleHideController() => SafeAsync("onair_toggle_hide_controller", async () =>
    {
        await OnAirClient.Instance.SendCommandAsync("ToggleControllerCaptureProtection");
        return "Toggled Controller visibility in screen share.";
    });

    [McpServerTool(Name = "onair_toggle_recording"), Description("Starts audio recording/Q&A capture in onAIr if stopped, or stops it if currently recording. Toggles the current state.")]
    public static Task<string> OnairToggleRecording() => SafeAsync("onair_toggle_recording", async () =>
    {
        await OnAirClient.Instance.SendCommandAsync("ToggleRecording");
        return "Toggled recording.";
    });

    [McpServerTool(Name = "onair_load_script"), Description("Loads a script into onAIr's teleprompter from a local .txt file, given its full absolute path. The file must already exist on disk.")]
    public static Task<string> OnairLoadScript(
        [Description("Absolute path to an existing .txt file, e.g. C:\\Scripts\\demo.txt")] string path)
        => SafeAsync("onair_load_script", async () =>
        {
            var (success, error) = await OnAirClient.Instance.LoadScriptAsync(path);
            return success ? $"Loaded script: {path}" : $"Could not load script: {error}";
        });

    [McpServerTool(Name = "onair_set_scroll_mode"), Description("Sets the teleprompter's scroll mode: Manual (press buttons to scroll step by step), Auto (continuous automatic scroll at a fixed speed), or Voice (scrolls automatically based on detected speech).")]
    public static Task<string> OnairSetScrollMode(
        [Description("One of: Manual, Auto, Voice")] string mode)
        => SafeAsync("onair_set_scroll_mode", async () =>
        {
            var (success, error) = await OnAirClient.Instance.SetFieldAsync("ScrollMode", mode);
            return success ? $"Scroll mode set to {mode}." : $"Could not set scroll mode: {error}";
        });

    [McpServerTool(Name = "onair_set_scroll_speed"), Description("Sets the Auto scroll mode's continuous scroll speed. Only takes visible effect while scroll mode is Auto.")]
    public static Task<string> OnairSetScrollSpeed(
        [Description("Scroll speed value (higher = faster)")] int speed)
        => SafeAsync("onair_set_scroll_speed", async () =>
        {
            var (success, error) = await OnAirClient.Instance.SetFieldAsync("ScrollSpeed", speed);
            return success ? $"Auto-scroll speed set to {speed}." : $"Could not set scroll speed: {error}";
        });

    [McpServerTool(Name = "onair_set_voice_scroll_speed"), Description("Sets the Voice scroll mode's scroll speed. Only takes visible effect while scroll mode is Voice.")]
    public static Task<string> OnairSetVoiceScrollSpeed(
        [Description("Voice scroll speed value (higher = faster)")] int speed)
        => SafeAsync("onair_set_voice_scroll_speed", async () =>
        {
            var (success, error) = await OnAirClient.Instance.SetFieldAsync("VoiceScrollSpeed", speed);
            return success ? $"Voice scroll speed set to {speed}." : $"Could not set voice scroll speed: {error}";
        });

    [McpServerTool(Name = "onair_set_scroll_step"), Description("Sets the Manual scroll mode's step size in pixels per button press. Only takes visible effect while scroll mode is Manual.")]
    public static Task<string> OnairSetScrollStep(
        [Description("Scroll step in pixels")] int step)
        => SafeAsync("onair_set_scroll_step", async () =>
        {
            var (success, error) = await OnAirClient.Instance.SetFieldAsync("ScrollStep", step);
            return success ? $"Scroll step set to {step}px." : $"Could not set scroll step: {error}";
        });

    [McpServerTool(Name = "onair_set_font_size"), Description("Sets the script text's font size (in points) in the teleprompter.")]
    public static Task<string> OnairSetFontSize(
        [Description("Font size in points")] int size)
        => SafeAsync("onair_set_font_size", async () =>
        {
            var (success, error) = await OnAirClient.Instance.SetFieldAsync("FontSize", size);
            return success ? $"Font size set to {size}." : $"Could not set font size: {error}";
        });

    [McpServerTool(Name = "onair_set_font_color"), Description("Sets the script text's color in the teleprompter, as a hex color code.")]
    public static Task<string> OnairSetFontColor(
        [Description("Hex color code, e.g. #FF8800")] string hexColor)
        => SafeAsync("onair_set_font_color", async () =>
        {
            var (success, error) = await OnAirClient.Instance.SetFieldAsync("FontColor", hexColor);
            return success ? $"Font color set to {hexColor}." : $"Could not set font color: {error}";
        });

    [McpServerTool(Name = "onair_set_font_family"), Description("Sets the script text's font family in the teleprompter. Must be a font actually installed on this PC — call onair_list_fonts first if unsure of the exact name.")]
    public static Task<string> OnairSetFontFamily(
        [Description("Installed font family name, e.g. Consolas")] string fontFamily)
        => SafeAsync("onair_set_font_family", async () =>
        {
            var (success, error) = await OnAirClient.Instance.SetFieldAsync("FontFamily", fontFamily);
            return success ? $"Font family set to {fontFamily}." : $"Could not set font family: {error}";
        });

    [McpServerTool(Name = "onair_set_opacity"), Description("Sets the teleprompter overlay window's opacity as a percentage.")]
    public static Task<string> OnairSetOpacity(
        [Description("Opacity percentage, 0-100")] double opacityPercent)
        => SafeAsync("onair_set_opacity", async () =>
        {
            var (success, error) = await OnAirClient.Instance.SetFieldAsync("Opacity", opacityPercent);
            return success ? $"Opacity set to {opacityPercent}%." : $"Could not set opacity: {error}";
        });

    [McpServerTool(Name = "onair_set_voice_threshold"), Description("Sets the microphone sensitivity threshold Voice scroll mode uses to detect speech.")]
    public static Task<string> OnairSetVoiceThreshold(
        [Description("Voice sensitivity threshold value")] double threshold)
        => SafeAsync("onair_set_voice_threshold", async () =>
        {
            var (success, error) = await OnAirClient.Instance.SetFieldAsync("VoiceThreshold", threshold);
            return success ? $"Voice sensitivity threshold set to {threshold}." : $"Could not set voice threshold: {error}";
        });

    [McpServerTool(Name = "onair_recheck_whisper_model"), Description("Re-checks whether onAIr's local Whisper transcription model is currently loaded, refreshing the local-vs-cloud status shown in onAIr's AI tab.")]
    public static Task<string> OnairRecheckWhisperModel() => SafeAsync("onair_recheck_whisper_model", async () =>
    {
        await OnAirClient.Instance.SendCommandAsync("RecheckWhisperModel");
        return "Rechecked Whisper model status.";
    });

    [McpServerTool(Name = "onair_release_stealth_container"), Description("Releases/closes onAIr's App Stealth container window (used to hide onAIr's own windows from a specific screen-shared application).")]
    public static Task<string> OnairReleaseStealthContainer() => SafeAsync("onair_release_stealth_container", async () =>
    {
        await OnAirClient.Instance.SendCommandAsync("ReleaseStealthContainer");
        return "Released the App Stealth container.";
    });

    [McpServerTool(Name = "onair_show_insight"), Description("Shows a short Copilot-insight message in a small footer on onAIr's teleprompter (TP), visible in BOTH Script and Q&A modes. Use this to surface a private heads-up, coaching note, or piece of context to the presenter WITHOUT it being confused for the AI's own Q&A answer or the script content — e.g. \"Client mentioned budget concerns in the last call\" or \"This prospect's renewal is in 30 days\". Text longer than 280 characters is truncated. Call onair_clear_insight to remove it.")]
    public static Task<string> OnairShowInsight(
        [Description("The insight text to show, ideally one or two short sentences")] string text)
        => SafeAsync("onair_show_insight", async () =>
        {
            var (success, error) = await OnAirClient.Instance.ShowInsightAsync(text);
            return success ? "Insight shown on the teleprompter." : $"Could not show insight: {error}";
        });

    [McpServerTool(Name = "onair_clear_insight"), Description("Clears the Copilot-insight footer from onAIr's teleprompter (TP), if one is currently shown.")]
    public static Task<string> OnairClearInsight() => SafeAsync("onair_clear_insight", async () =>
    {
        var (success, error) = await OnAirClient.Instance.ClearInsightAsync();
        return success ? "Insight cleared." : $"Could not clear insight: {error}";
    });
}
