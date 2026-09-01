namespace OnAirNative.Models;

// ── Per-provider configurations ───────────────────────────────────────────────

public class AzureConfig
{
    public string Endpoint          { get; set; } = "";
    public string Key               { get; set; } = "";
    public string WhisperDeployment { get; set; } = "";
    public string ChatDeployment    { get; set; } = "";
}

public class OpenAiConfig
{
    public string Key          { get; set; } = "";
    public string WhisperModel { get; set; } = "whisper-1";
    public string ChatModel    { get; set; } = "gpt-4o";
}

public class GroqConfig
{
    public string Key          { get; set; } = "";
    public string WhisperModel { get; set; } = "whisper-large-v3";
    public string ChatModel    { get; set; } = "llama-3.3-70b-versatile";
}

public class AnthropicConfig
{
    public string Key       { get; set; } = "";
    public string ChatModel { get; set; } = "claude-3-5-haiku-20241022";
}

public class GeminiConfig
{
    public string Key       { get; set; } = "";
    public string ChatModel { get; set; } = "gemini-2.0-flash";
}

public class MistralConfig
{
    public string Key       { get; set; } = "";
    public string ChatModel { get; set; } = "mistral-small-latest";
}

// ── Appearance / UI preferences ───────────────────────────────────────────────

public class AppearanceConfig
{
    public int    Opacity           { get; set; } = 75;
    public int    FontSize          { get; set; } = 22;
    public string FontColor         { get; set; } = "#f0f0f0";
    public string FontFamily        { get; set; } = "Segoe UI";
    public int    ScrollStep        { get; set; } = 120;
    public int    ScrollSpeed       { get; set; } = 50;
    public int    VoiceScrollSpeed  { get; set; } = 50;   // independent from ScrollSpeed (Auto mode)
    public double VoiceRmsThreshold { get; set; } = 5.0;  // lowered from 15 — easier to trigger
}

// ── Persisted window geometry ─────────────────────────────────────────────────

public class WindowState
{
    public double X      { get; set; }
    public double Y      { get; set; }
    public double Width  { get; set; }
    public double Height { get; set; }
}

// ── Root config (maps 1-to-1 with config.json) ───────────────────────────────

public class AppConfig
{
    // Active providers
    public string Provider              { get; set; } = "azure";
    public string TranscriptionProvider { get; set; } = "openai";

    // Provider-specific credentials and models
    public AzureConfig    Azure    { get; set; } = new();
    public OpenAiConfig   OpenAi   { get; set; } = new();
    public GroqConfig     Groq     { get; set; } = new();
    public AnthropicConfig Anthropic { get; set; } = new();
    public GeminiConfig   Gemini   { get; set; } = new();
    public MistralConfig  Mistral  { get; set; } = new();

    // Audio capture
    public string AudioDeviceId          { get; set; } = "";
    public string AudioOutputDeviceId    { get; set; } = "";
    /// <summary>"microphone" | "system" | "both"</summary>
    public string AudioRecordingSource   { get; set; } = "microphone";

    // Whisper.net local model path (empty = use cloud API)
    public string WhisperModelPath { get; set; } = "";

    // AI prompt customisation
    public string SystemPrompt { get; set; } =
        "You are a helpful assistant supporting a sales or technical presentation. " +
        "The presenter received a question from a client and needs a concise answer they can read aloud. " +
        "Respond in the same language as the question. Keep your answer clear and under 4 sentences.";

    public string PresentationContext { get; set; } = "";

    // UI / appearance
    public AppearanceConfig Appearance { get; set; } = new();

    /// <summary>Controller window color theme: "System" | "Light" | "Dark".</summary>
    public string Theme { get; set; } = "System";

    // Persisted window positions
    public WindowState OverlayWindow     { get; set; } = new() { X = 80,  Y = 40,  Width = 720, Height = 300 };
    public WindowState ControllerWindow  { get; set; } = new() { X = 50,  Y = 80,  Width = 600, Height = 640 };

    // Content protection: hide overlay from screen share (default on)
    public bool OverlayProtected    { get; set; } = true;
    // Content protection: hide controller from screen share (default off)
    public bool ControllerProtected { get; set; } = false;

    /// <summary>Whether the Remote Control WebSocket server (RemoteControlService, loopback-only,
    /// port 47823) should be running — gates BOTH the Stream Deck plugin and the MCP server, since
    /// they're two clients of the exact same local server. Default on — matches the behavior
    /// before this became user-toggleable.</summary>
    public bool RemoteControlEnabled { get; set; } = true;

    /// <summary>MCP tool names (e.g. "onair_set_font_color") the user has explicitly disabled via
    /// Settings → REMOTE CONTROL → "MCP Tools &amp; Setup…". Empty by default (every tool enabled)
    /// so upgrading users see no behavior change. Read directly from config.json by the separate
    /// onAIr MCP server process (mcp-server/OnAirMcp — a different .exe, not this app) before
    /// executing each tool call; see mcp-server/OnAirTools.cs's ToolGate.</summary>
    public List<string> McpDisabledTools { get; set; } = [];
}
