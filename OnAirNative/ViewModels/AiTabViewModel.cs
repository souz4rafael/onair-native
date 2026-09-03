using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using OnAirNative.Models;
using OnAirNative.Services;

namespace OnAirNative.ViewModels;

public partial class AiTabViewModel : ObservableObject
{
    private readonly ConfigService  _config;
    private readonly AiChatService  _ai;
    private readonly WhisperService _whisper;

    // Chat provider selection
    [ObservableProperty] private int _selectedChatProviderIndex;
    // Transcription provider selection — always what cloud transcription actually uses (see
    // WhisperService.ResolveProvider), fully independent of the Chat provider selection above.
    [ObservableProperty] private int _selectedTranscriptionProviderIndex;

    [ObservableProperty] private string _systemPrompt;
    [ObservableProperty] private string _presentationContext;
    [ObservableProperty] private string _whisperModelPath;

    // Max tokens requested per chat completion — was a hardcoded 400 in AiChatService, now
    // user-configurable (Slider in the PROMPTS card, 50-2000).
    [ObservableProperty] private int _maxTokens;

    // Running token-usage summary, refreshed whenever AiChatService.UsageChanged fires (i.e.
    // after every successful chat call, and after a manual reset). Text, not raw numbers,
    // since the Controller UI just needs to display it — no $ estimate (see AiChatService's
    // usage-tracking doc comment for why).
    [ObservableProperty] private string _usageSummary = "No calls yet this session.";

    // "Show follow-up question suggestions" toggle (Block 2) — off by default since it's an
    // extra billed API call per question, not something that should surprise a user's bill.
    [ObservableProperty] private bool _showFollowUpSuggestions;

    // Custom vocabulary/glossary (Block 3) — free text, injected into BOTH the Whisper
    // transcription prompt and the chat system prompt (see AppConfig.Glossary's own doc
    // comment). Blank by default, same opt-in pattern as PresentationContext.
    [ObservableProperty] private string _glossary = "";

    // Reference documents attached as a lightweight knowledge base (Block 3) — full paths,
    // display-only here (filenames shown in the UI; see ControllerWindow.PopulateKnowledgeBaseFiles).
    // An ObservableCollection so the Settings card can react to Add/Remove without a manual
    // refresh call, same pattern as OverlayViewModel.ConversationTurns/FollowUpSuggestions.
    public ObservableCollection<string> KnowledgeBaseFiles { get; } = [];

    // Feedback for the local Whisper model load triggered by WhisperModelPath below —
    // "", "Loading model…", "✓ Model loaded", "⚠ File not found", or "⚠ Failed to load model".
    [ObservableProperty] private string _whisperModelStatus = "";

    [ObservableProperty] private string _connectionStatus = "";
    [ObservableProperty] private bool   _isTesting        = false;

    // "Use local Whisper model" checkbox (Q&A tab) — the explicit, PERSISTED source of truth
    // for whether real transcriptions use local Whisper or a cloud provider (see
    // AppConfig.UseLocalWhisper / WhisperService.TranscribeAsync). Deliberately independent of
    // whether a model happens to be loaded right now — loading/unloading (Settings → WHISPER
    // MODEL) only controls availability, this controls actual use.
    [ObservableProperty] private bool _useLocalWhisper;

    public static readonly string[] ChatProviders          = ["Azure OpenAI", "OpenAI", "Groq", "Anthropic", "Google Gemini", "Mistral", "Local LM"];
    public static readonly string[] ProviderKeys           = ["azure", "openai", "groq", "anthropic", "gemini", "mistral", "local"];
    public static readonly string[] TranscriptionProviders = ["OpenAI (Whisper)", "Groq (Whisper)", "Azure (Whisper)", "Local LM"];
    // "local" here is the SAME key/config as chat's "local" (AppConfig.Local) — one unified
    // server config now covers both roles (ChatModel + WhisperModel fields), mirroring exactly
    // how openai/groq/azure already legitimately serve both roles from one account. This means
    // Chat="Local LM" + Transcription="Local LM" now correctly means "the same server", which is
    // why TestConnectionAsync's "is Whisper the same provider as Chat?" shortcut is allowed to
    // fire for "local" too (previously this was deliberately a different key, "localserver", to
    // PREVENT that shortcut from conflating two unrelated servers under the old split-config
    // design — no longer applicable now that it really is one server/config for both).
    public static readonly string[] TranscriptionKeys      = ["openai", "groq", "azure", "local"];

    public AiTabViewModel(ConfigService config, AiChatService ai, WhisperService whisper)
    {
        _config  = config;
        _ai      = ai;
        _whisper = whisper;

        var cfg = config.Current;
        SelectedChatProviderIndex          = Array.IndexOf(ProviderKeys, cfg.Provider);
        SelectedTranscriptionProviderIndex = Array.IndexOf(TranscriptionKeys, cfg.TranscriptionProvider);
        SystemPrompt        = cfg.SystemPrompt;
        PresentationContext = cfg.PresentationContext;
        MaxTokens           = cfg.MaxTokens;
        ShowFollowUpSuggestions = cfg.ShowFollowUpSuggestions;
        Glossary            = cfg.Glossary;
        foreach (var path in cfg.KnowledgeBaseFiles)
            KnowledgeBaseFiles.Add(path);

        // Assigning WhisperModelPath below fires OnWhisperModelPathChanged, which is what
        // actually loads the model — so a path saved from a previous session gets loaded
        // on startup too, not just when the user edits it. Previously WhisperModelPath was
        // only ever persisted to config and never handed to WhisperService at all: the app
        // silently fell back to the cloud API forever, even with a valid local model path
        // configured (LoadModelAsync existed but nothing called it).
        WhisperModelPath = cfg.WhisperModelPath;

        // Read the persisted preference LAST — assigning it fires OnUseLocalWhisperChanged,
        // which (if true) may also kick off a debounced load; putting this after
        // WhisperModelPath's own debounced load means the two naturally coalesce via the shared
        // CancellationTokenSource instead of racing two concurrent loads of the same file.
        UseLocalWhisper = cfg.UseLocalWhisper;

        // Live-refresh the usage summary after every chat call (and after a manual reset) —
        // AiChatService is a singleton (App.AiChat) that outlives this ViewModel, so no
        // unsubscribe is needed (this ViewModel itself lives for the Controller window's
        // whole lifetime, same as every other ViewModel in this app).
        _ai.UsageChanged += (_, _) => RefreshUsageSummary();
        RefreshUsageSummary();
    }

    partial void OnSelectedChatProviderIndexChanged(int value)
    {
        if (value >= 0 && value < ProviderKeys.Length)
        {
            _config.Current.Provider = ProviderKeys[value];
            _config.Save();
        }
    }

    partial void OnSelectedTranscriptionProviderIndexChanged(int value)
    {
        if (value >= 0 && value < TranscriptionKeys.Length)
        {
            _config.Current.TranscriptionProvider = TranscriptionKeys[value];
            _config.Save();
        }
    }

    partial void OnSystemPromptChanged(string value)
    {
        _config.Current.SystemPrompt = value;
        _config.Save();
    }

    partial void OnPresentationContextChanged(string value)
    {
        _config.Current.PresentationContext = value;
        _config.Save();
    }

    partial void OnGlossaryChanged(string value)
    {
        _config.Current.Glossary = value;
        _config.Save();
    }

    /// <summary>Adds a reference document path to the knowledge base — no-op if already present
    /// (the file picker lets a user select the same file twice across two separate Add clicks).
    /// Bound to the Settings → KNOWLEDGE BASE card's "+ Add file(s)…" picker result.</summary>
    [RelayCommand]
    public void AddKnowledgeBaseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || KnowledgeBaseFiles.Contains(path)) return;
        KnowledgeBaseFiles.Add(path);
        PersistKnowledgeBaseFiles();
    }

    /// <summary>Removes a reference document from the knowledge base — this only stops it being
    /// searched going forward; the file on disk is untouched.</summary>
    [RelayCommand]
    public void RemoveKnowledgeBaseFile(string path)
    {
        KnowledgeBaseFiles.Remove(path);
        PersistKnowledgeBaseFiles();
    }

    private void PersistKnowledgeBaseFiles()
    {
        _config.Current.KnowledgeBaseFiles = [.. KnowledgeBaseFiles];
        _config.Save();
    }

    partial void OnMaxTokensChanged(int value)
    {
        _config.Current.MaxTokens = value;
        _config.Save();
    }

    partial void OnShowFollowUpSuggestionsChanged(bool value)
    {
        _config.Current.ShowFollowUpSuggestions = value;
        _config.Save();
    }

    private void RefreshUsageSummary()
    {
        UsageSummary = _ai.TotalCalls == 0
            ? "No calls yet this session."
            : $"{_ai.TotalCalls} call{(_ai.TotalCalls == 1 ? "" : "s")} — " +
              $"{_ai.TotalPromptTokens:N0} prompt + {_ai.TotalCompletionTokens:N0} completion = " +
              $"{_ai.TotalPromptTokens + _ai.TotalCompletionTokens:N0} tokens total";
    }

    /// <summary>Zeroes the running token-usage counters — e.g. starting a fresh stream/show and
    /// wanting a clean count for it. Does not affect any provider's real usage/billing, only
    /// this in-memory session display.</summary>
    [RelayCommand]
    public void ResetUsage() => _ai.ResetUsage();

    partial void OnWhisperModelPathChanged(string value)
    {
        _config.Current.WhisperModelPath = value;
        _config.Save();
        _ = LoadWhisperModelDebouncedAsync(value);
    }

    partial void OnUseLocalWhisperChanged(bool value)
    {
        _config.Current.UseLocalWhisper = value;
        _config.Save();

        // Checking the box with a path configured but not yet loaded shouldn't silently defer
        // the failure to the next real recording attempt — kick off a load right away so any
        // problem (missing file, bad format) surfaces immediately, same as "Test connection"
        // already does. Debounced (not a direct LoadWhisperModelAsync call) so it naturally
        // coalesces with any load already in flight from WhisperModelPath's own debounce,
        // instead of racing two concurrent loads of the same file.
        if (value && !_whisper.IsLocalModelLoaded && !string.IsNullOrWhiteSpace(WhisperModelPath))
            _ = LoadWhisperModelDebouncedAsync(WhisperModelPath);
    }

    // Debounced so typing a path character-by-character (vs. picking one via Browse,
    // which sets the whole path at once) doesn't try to load a multi-hundred-MB model
    // file on every keystroke. Each call cancels any load still pending from a previous
    // keystroke; only the last one within the debounce window actually loads.
    private CancellationTokenSource? _modelLoadDebounceCts;

    private async Task LoadWhisperModelDebouncedAsync(string path)
    {
        _modelLoadDebounceCts?.Cancel();
        var cts = _modelLoadDebounceCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(600, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return; // superseded by a newer edit
        }

        await LoadWhisperModelAsync(path);
    }

    private async Task LoadWhisperModelAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _whisper.UnloadModel(); // clearing the path means "use cloud" — must actually unload,
            WhisperModelStatus = ""; // not just clear the status text (see UnloadModel's doc comment)
            return;
        }
        if (!File.Exists(path))
        {
            _whisper.UnloadModel(); // a since-deleted/moved file can't stay "loaded" either
            WhisperModelStatus = "⚠ File not found";
            return;
        }

        WhisperModelStatus = "Loading model…";
        var ok = await _whisper.LoadModelAsync(path, _config.Current.Glossary);
        WhisperModelStatus = ok ? "✓ Model loaded" : "⚠ Failed to load model";
    }

    /// <summary>
    /// Forces an immediate re-check of the Whisper model path — bypasses the debounce and
    /// re-attempts the load even if the path hasn't changed, so a model file that appeared
    /// after a previous failed attempt (or one the user is unsure actually loaded) gets a
    /// fresh verification without needing to retype the path. Bound to the WHISPER MODEL
    /// card's "🔄 Recheck" button, and to the Stream Deck plugin's AI Status tile press
    /// (via <see cref="OnAirNative.Services.HotkeyAction.RecheckWhisperModel"/>).
    /// </summary>
    [RelayCommand]
    public async Task RecheckWhisperModelAsync()
    {
        _modelLoadDebounceCts?.Cancel(); // supersede any pending debounced load
        await LoadWhisperModelAsync(WhisperModelPath);
        if (string.IsNullOrWhiteSpace(WhisperModelPath))
            WhisperModelStatus = "Using cloud API (no local model path set)";
    }

    /// <summary>
    /// Genuine toggle for the Settings → WHISPER MODEL card's Load/Unload button: unloads if a
    /// model is currently loaded, loads (from WhisperModelPath) if not. Deliberately a SEPARATE
    /// command from <see cref="RecheckWhisperModelAsync"/> — Recheck always re-attempts a load
    /// regardless of current state and is relied on by the Stream Deck plugin's AI Status tile
    /// and the MCP "onair_recheck_whisper_model" tool; repurposing it into a toggle would make
    /// those external triggers unload a perfectly working model instead of just re-verifying it.
    /// </summary>
    [RelayCommand]
    public async Task ToggleWhisperModelAsync()
    {
        if (_whisper.IsLocalModelLoaded)
        {
            _whisper.UnloadModel();
            WhisperModelStatus = "Unloaded (path kept — click Load to reload it)";
        }
        else
        {
            await LoadWhisperModelAsync(WhisperModelPath);
        }
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        IsTesting        = true;
        ConnectionStatus = "Testing…";

        var cfg          = _config.Current;
        var chatProvider = cfg.Provider;
        var chatResult   = await _ai.TestConnectionAsync(chatProvider, cfg);
        var chatLine     = $"Chat ({DisplayName(chatProvider)}): " +
                            (chatResult.Success ? $"✓ {chatResult.Text}" : $"✗ {chatResult.Error}");

        // Whisper transcription always uses whatever the Transcription dropdown is set to (see
        // WhisperService.ResolveProvider — fully independent of the Chat provider, even when
        // Chat happens to be a Whisper-capable provider too) — UNLESS "Use local Whisper model"
        // is checked, which is now the actual real-transcription switch too (AppConfig.
        // UseLocalWhisper / WhisperService.TranscribeAsync), not just a test-time override. Only
        // skip the redundant second cloud call when both dropdowns genuinely point at the same
        // provider. Previously this method only ever tested the Chat provider, so a broken
        // Whisper-only credential never surfaced here.
        string whisperLine;
        if (UseLocalWhisper)
        {
            if (string.IsNullOrWhiteSpace(WhisperModelPath))
            {
                whisperLine = "Whisper (local): ✗ No model file selected — pick one in Settings → WHISPER MODEL";
            }
            else if (_whisper.IsLocalModelLoaded)
            {
                whisperLine = "Whisper (local): ✓ Already loaded";
            }
            else
            {
                var ok = await _whisper.LoadModelAsync(WhisperModelPath, cfg.Glossary);
                WhisperModelStatus = ok ? "✓ Model loaded" : "⚠ Failed to load model";
                whisperLine = ok
                    ? "Whisper (local): ✓ Loaded successfully"
                    : "Whisper (local): ✗ Failed to load — check the file in Settings → WHISPER MODEL";
            }
        }
        else
        {
            // Not using local Whisper (per the checkbox) — test the cloud Transcription
            // provider, regardless of whether a local model happens to be loaded in memory.
            // A loaded-but-unused model must NOT make this test (or real transcriptions) skip
            // verifying the cloud path — that was the exact bug: loading a model elsewhere
            // silently making it "the one used" with no regard for this checkbox.
            var whisperProvider = WhisperService.ResolveProvider(cfg);
            if (whisperProvider == chatProvider)
            {
                whisperLine = $"Whisper: same as Chat ({DisplayName(whisperProvider)}) — covered above";
            }
            else
            {
                var whisperResult = await _ai.TestConnectionAsync(whisperProvider, cfg);
                whisperLine = $"Whisper ({DisplayName(whisperProvider)}): " +
                              (whisperResult.Success ? $"✓ {whisperResult.Text}" : $"✗ {whisperResult.Error}");
            }
        }

        ConnectionStatus = $"{chatLine}\n{whisperLine}";
        IsTesting         = false;
    }

    private static string DisplayName(string providerKey) =>
        ChatProviders[Array.IndexOf(ProviderKeys, providerKey)];
}
