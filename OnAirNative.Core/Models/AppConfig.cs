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

/// <summary>Local/self-hosted LLM — any server exposing an OpenAI-compatible
/// /v1/chat/completions endpoint (Ollama, LM Studio, llama.cpp's llama-server, LocalAI, etc.),
/// on this machine OR another one on the local network. ONE config serves BOTH chat and
/// transcription (mirrors exactly how OpenAiConfig/GroqConfig already carry both a ChatModel and
/// a WhisperModel under one shared Key/base) — set <see cref="ChatModel"/>,
/// <see cref="WhisperModel"/>, or both, depending on what your server actually supports. Ollama
/// itself only does chat (no transcription); a server like LocalAI implements both
/// /v1/chat/completions AND /v1/audio/transcriptions from the same base URL, so leaving
/// WhisperModel blank simply means this provider is never offered/used for transcription.
///
/// Key is OPTIONAL: Ollama's own docs use a dummy value ("required but unused"), and many local
/// setups have no auth at all — see AiChatService's empty-key handling (skips the Authorization
/// header entirely rather than sending a malformed empty Bearer token).</summary>
public class LocalConfig
{
    /// <summary>Base URL up to and including "/v1" — "/chat/completions" or
    /// "/audio/transcriptions" is appended automatically depending on which role is being called
    /// (mirrors how AzureConfig.Endpoint gets its own path suffix appended). Defaults to Ollama's
    /// own default port; change the host to a LAN IP to reach a server running on another
    /// machine (e.g. Ollama needs OLLAMA_HOST=0.0.0.0:... set on that machine first — see
    /// README's "Local LLM" chapter). NOTE: this fixed-suffix convention means a server whose
    /// transcription endpoint uses a non-standard path (e.g. whisper.cpp's own bundled server,
    /// which uses "/inference" rather than "/audio/transcriptions") isn't supported here — this
    /// targets the more standardized OpenAI-compatible server shape (Ollama, LM Studio, LocalAI,
    /// llama-server) by design.</summary>
    public string BaseUrl      { get; set; } = "http://localhost:11434/v1";
    public string ChatModel    { get; set; } = "";
    /// <summary>Leave blank if this server doesn't support transcription (e.g. plain Ollama) —
    /// an empty value simply means "Local LM" is never a real option in the Transcription
    /// provider dropdown's actual behavior, even though it can still be selected there.</summary>
    public string WhisperModel { get; set; } = "";
    public string Key          { get; set; } = "";
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

/// <summary>Appearance settings for the "AI Insights" window/tab — deliberately independent from
/// <see cref="AppearanceConfig"/> (the TP's own settings) so the presenter can style each
/// container differently (e.g. a larger, high-contrast Insights window on a second monitor while
/// the TP stays compact). No scroll-related fields here — Insights content never scrolls via
/// step/speed controls, only its own ScrollViewer.</summary>
public class InsightAppearanceConfig
{
    public int    Opacity    { get; set; } = 85;
    public int    FontSize   { get; set; } = 16;
    public string FontColor  { get; set; } = "#f0f0f0";
    public string FontFamily { get; set; } = "Segoe UI";
}

/// <summary>Settings for the Web Remote — a second, LAN-reachable HttpListener
/// (WebRemoteService, wildcard-bound, port 47824) that serves a small static control page so a
/// phone/tablet/other PC on the same network can control onAIr, same as the Stream Deck plugin
/// does locally. Deliberately separate from <see cref="AppConfig.RemoteControlEnabled"/>'s
/// loopback-only server (port 47823) — that one's trust model is "any process running as this
/// Windows user"; this one is reachable from other devices, so it additionally requires a PIN
/// and a one-time Windows URL-ACL grant (see WebRemoteService for details). Off by default —
/// unlike the loopback server, this genuinely opens a network-facing listener.</summary>
public class WebRemoteConfig
{
    public bool   Enabled { get; set; } = false;
    /// <summary>6-digit numeric PIN required on every WebSocket connection attempt (as a
    /// <c>?pin=</c> query parameter). Generated lazily on first enable if blank. Regenerating it
    /// (Settings → WEB REMOTE → Regenerate) instantly invalidates every previously paired
    /// device — there is no server-side session list, the live PIN value IS the credential.</summary>
    public string Pin { get; set; } = "";
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
    /// <summary>Provider key "local" (both Chat AND Transcription dropdowns) — self-hosted
    /// OpenAI-compatible server (Ollama, LM Studio, llama-server, LocalAI). One config, two
    /// separate model-name fields — see LocalConfig's own doc comment.</summary>
    public LocalConfig    Local    { get; set; } = new();

    // Audio capture
    public string AudioDeviceId          { get; set; } = "";
    public string AudioOutputDeviceId    { get; set; } = "";
    /// <summary>"microphone" | "system" | "both"</summary>
    public string AudioRecordingSource   { get; set; } = "microphone";

    // Whisper.net local model path (empty = use cloud API)
    public string WhisperModelPath { get; set; } = "";

    // Explicit user choice of local vs. cloud Whisper for actual transcriptions — deliberately
    // NOT auto-derived from "is a model currently loaded", so loading a model in Settings (or
    // it auto-loading at startup) never silently switches real transcriptions to local without
    // the user asking for that via the Q&A tab's "Use local Whisper model" checkbox.
    public bool UseLocalWhisper { get; set; } = false;

    // AI prompt customisation
    public string SystemPrompt { get; set; } =
        "You are a helpful assistant supporting a sales or technical presentation. " +
        "The presenter received a question from a client and needs a concise answer they can read aloud. " +
        "Respond in the same language as the question. Keep your answer clear and under 4 sentences.";

    public string PresentationContext { get; set; } = "";

    /// <summary>Max tokens requested per chat completion — was a hardcoded 400 in AiChatService
    /// before this became user-configurable. 400 kept as the default so upgrading users see no
    /// behavior change.</summary>
    public int MaxTokens { get; set; } = 400;

    /// <summary>Whether to fetch 2-3 follow-up question suggestions after each successful Q&amp;A
    /// answer (a separate, minimal AI call — no system prompt/presentation context/history — so
    /// it never competes with or pollutes the main answer's context). Off by default: it's an
    /// extra billed API call per question, opt-in rather than a surprise cost increase.</summary>
    public bool ShowFollowUpSuggestions { get; set; } = false;

    /// <summary>Whether the AI Insights window shows its Pacing section. A pure display toggle —
    /// pacing is always computed regardless (see OverlayViewModel.PacingSummary's doc comment).
    /// On by default since this section already always showed before the toggle existed; this
    /// only adds the ability to opt out.</summary>
    public bool ShowPacingInInsights { get; set; } = true;

    /// <summary>Whether the AI Insights window shows its Token Usage section. A pure display
    /// toggle — usage is always tracked regardless. On by default for the same reason as
    /// <see cref="ShowPacingInInsights"/>.</summary>
    public bool ShowTokenUsageInInsights { get; set; } = true;

    /// <summary>Whether the AI Insights window shows its Questions (follow-up suggestions)
    /// section. A pure display toggle, independent of <see cref="ShowFollowUpSuggestions"/>
    /// (which controls whether suggestions are generated at all) — mirrors
    /// <see cref="ShowPacingInInsights"/> exactly. On by default so a fresh install shows all
    /// four AI Insights sections consistently (each with its own "nothing yet" placeholder)
    /// until the presenter opts out of one.</summary>
    public bool ShowFollowUpsInInsights { get; set; } = true;

    /// <summary>Whether the AI Insights window shows its External AI Insights section (the free
    /// text pushed via onair_show_insight / an external MCP agent, or the Web Remote's manual
    /// push box). A pure display toggle — the text itself is always received/stored regardless
    /// (see OverlayViewModel.InsightText). On by default, same reasoning as
    /// <see cref="ShowPacingInInsights"/>.</summary>
    public bool ShowExternalInsightsInInsights { get; set; } = true;

    /// <summary>Free-text custom vocabulary/glossary — product names, jargon, acronyms, spellings
    /// the presenter wants transcription and chat answers to get right (e.g. "Contoso, Northwind
    /// Traders, SKU-4471"). Injected as-is into BOTH: (1) the Whisper transcription "prompt" bias
    /// — a real, documented Whisper API parameter that nudges recognition toward specific
    /// vocabulary/spelling — and (2) the chat system prompt, as a labeled "Glossary" section (see
    /// AiChatService.BuildSystemText). Blank by default — completely inert until the user opts
    /// in, same as PresentationContext.</summary>
    public string Glossary { get; set; } = "";

    /// <summary>Absolute paths to small reference documents (.txt/.md only) the presenter has
    /// attached as a lightweight knowledge base — product spec sheets, FAQs, pricing, etc. See
    /// KnowledgeBaseService for the search approach (deliberately keyword/TF-IDF relevance
    /// scoring, NOT an embeddings/vector-DB pipeline — see that class's own doc comment for why).
    /// Empty by default — completely inert until the user attaches at least one file.</summary>
    public List<string> KnowledgeBaseFiles { get; set; } = new();

    // UI / appearance
    public AppearanceConfig Appearance { get; set; } = new();

    /// <summary>Independent appearance settings for the "AI Insights" tab/window — see
    /// <see cref="InsightAppearanceConfig"/>'s own doc comment for why this is separate from
    /// <see cref="Appearance"/>.</summary>
    public InsightAppearanceConfig InsightAppearance { get; set; } = new();

    /// <summary>Controller window color theme: "System" | "Light" | "Dark".</summary>
    public string Theme { get; set; } = "System";

    // Persisted window positions
    public WindowState OverlayWindow     { get; set; } = new() { X = 80,  Y = 40,  Width = 720, Height = 300 };
    public WindowState ControllerWindow  { get; set; } = new() { X = 50,  Y = 80,  Width = 600, Height = 640 };
    // The "AI Insights" window — a separate, freely resizable panel (independent from the TP)
    // that shows the same Copilot-insight text an external MCP agent pushes. Only Width/Height
    // are ever restored (see InsightWindow.OnFirstActivated for why X/Y are not); X/Y here just
    // give it a sensible first-run default distinct from the TP's own (also-fixed) position.
    public WindowState InsightWindow     { get; set; } = new() { X = 820, Y = 40,  Width = 380, Height = 280 };

    // Content protection: hide overlay from screen share (default on)
    public bool OverlayProtected    { get; set; } = true;
    // Content protection: hide controller from screen share (default off)
    public bool ControllerProtected { get; set; } = false;
    // Content protection: hide the AI Insights window from screen share (default off — it's a
    // private aid the presenter reads from, same default posture as the Controller).
    public bool InsightWindowProtected { get; set; } = false;

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

    /// <summary>Web Remote server settings — see <see cref="WebRemoteConfig"/>'s own doc comment.
    /// Off by default.</summary>
    public WebRemoteConfig WebRemote { get; set; } = new();
}
