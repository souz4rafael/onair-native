using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    // Transcription provider selection (only shown when main provider doesn't support Whisper)
    [ObservableProperty] private int _selectedTranscriptionProviderIndex;

    [ObservableProperty] private string _systemPrompt;
    [ObservableProperty] private string _presentationContext;
    [ObservableProperty] private string _whisperModelPath;

    // Feedback for the local Whisper model load triggered by WhisperModelPath below —
    // "", "Loading model…", "✓ Model loaded", "⚠ File not found", or "⚠ Failed to load model".
    [ObservableProperty] private string _whisperModelStatus = "";

    [ObservableProperty] private string _connectionStatus = "";
    [ObservableProperty] private bool   _isTesting        = false;

    public static readonly string[] ChatProviders          = ["Azure OpenAI", "OpenAI", "Groq", "Anthropic", "Google Gemini", "Mistral"];
    public static readonly string[] ProviderKeys           = ["azure", "openai", "groq", "anthropic", "gemini", "mistral"];
    public static readonly string[] TranscriptionProviders = ["OpenAI (Whisper)", "Groq (Whisper)", "Azure (Whisper)"];
    public static readonly string[] TranscriptionKeys      = ["openai", "groq", "azure"];

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

        // Assigning WhisperModelPath below fires OnWhisperModelPathChanged, which is what
        // actually loads the model — so a path saved from a previous session gets loaded
        // on startup too, not just when the user edits it. Previously WhisperModelPath was
        // only ever persisted to config and never handed to WhisperService at all: the app
        // silently fell back to the cloud API forever, even with a valid local model path
        // configured (LoadModelAsync existed but nothing called it).
        WhisperModelPath = cfg.WhisperModelPath;
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

    partial void OnWhisperModelPathChanged(string value)
    {
        _config.Current.WhisperModelPath = value;
        _config.Save();
        _ = LoadWhisperModelDebouncedAsync(value);
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
        var ok = await _whisper.LoadModelAsync(path);
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

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        IsTesting        = true;
        ConnectionStatus = "Testing…";
        var result = await _ai.TestConnectionAsync(_config.Current.Provider, _config.Current);
        ConnectionStatus = result.Success ? $"✓ {result.Text}" : $"✗ {result.Error}";
        IsTesting        = false;
    }
}
