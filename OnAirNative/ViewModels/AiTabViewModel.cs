using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnAirNative.Models;
using OnAirNative.Services;

namespace OnAirNative.ViewModels;

public partial class AiTabViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly AiChatService _ai;

    // Chat provider selection
    [ObservableProperty] private int _selectedChatProviderIndex;
    // Transcription provider selection (only shown when main provider doesn't support Whisper)
    [ObservableProperty] private int _selectedTranscriptionProviderIndex;

    [ObservableProperty] private string _systemPrompt;
    [ObservableProperty] private string _presentationContext;
    [ObservableProperty] private string _whisperModelPath;

    [ObservableProperty] private string _connectionStatus = "";
    [ObservableProperty] private bool   _isTesting        = false;

    public static readonly string[] ChatProviders          = ["Azure OpenAI", "OpenAI", "Groq", "Anthropic", "Google Gemini", "Mistral"];
    public static readonly string[] ProviderKeys           = ["azure", "openai", "groq", "anthropic", "gemini", "mistral"];
    public static readonly string[] TranscriptionProviders = ["OpenAI (Whisper)", "Groq (Whisper)", "Azure (Whisper)"];
    public static readonly string[] TranscriptionKeys      = ["openai", "groq", "azure"];

    public AiTabViewModel(ConfigService config, AiChatService ai)
    {
        _config = config;
        _ai     = ai;

        var cfg = config.Current;
        SelectedChatProviderIndex          = Array.IndexOf(ProviderKeys, cfg.Provider);
        SelectedTranscriptionProviderIndex = Array.IndexOf(TranscriptionKeys, cfg.TranscriptionProvider);
        SystemPrompt        = cfg.SystemPrompt;
        PresentationContext = cfg.PresentationContext;
        WhisperModelPath    = cfg.WhisperModelPath;
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

    /// <summary>Returns the active provider config as a dynamic object for the config dialog.</summary>
    public object GetActiveProviderConfig() => _config.Current.Provider switch
    {
        "azure"    => _config.Current.Azure,
        "openai"   => _config.Current.OpenAi,
        "groq"     => _config.Current.Groq,
        "anthropic"=> _config.Current.Anthropic,
        "gemini"   => _config.Current.Gemini,
        "mistral"  => _config.Current.Mistral,
        _          => _config.Current.Azure,
    };
}
