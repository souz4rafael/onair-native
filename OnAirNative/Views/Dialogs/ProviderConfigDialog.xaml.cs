using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OnAirNative.Models;
using OnAirNative.Services;
using OnAirNative.ViewModels;

namespace OnAirNative.Views.Dialogs;

/// <summary>
/// ContentDialog for editing one specific provider's credentials/models and testing its
/// connection — independent of whichever provider is currently SELECTED for chat/transcription
/// in the AI/Q&amp;A tab. Opened from Settings → AI PROVIDERS, one card per provider, each
/// passing its own <paramref name="providerKey"/> explicitly.
///
/// Previously this dialog read/wrote <c>_config.Current.Provider</c> (the chat dropdown's
/// selection) directly — a real bug: with e.g. Chat=Groq but Transcription=OpenAI, there was no
/// way to ever configure OpenAI's key without first switching the chat dropdown to OpenAI (an
/// unwanted side effect just to edit a credential). Now the provider to edit is fixed at
/// construction time and has nothing to do with either dropdown.
/// </summary>
public sealed partial class ProviderConfigDialog : ContentDialog
{
    private readonly ConfigService _config;
    private readonly AiChatService _ai;
    private readonly string        _providerKey;

    public ProviderConfigDialog(ConfigService config, AiChatService ai, string providerKey)
    {
        InitializeComponent();
        _config      = config;
        _ai          = ai;
        _providerKey = providerKey;

        PrimaryButtonClick += OnSave;
        LoadFields();
    }

    private void LoadFields()
    {
        ProviderNameText.Text = AiTabViewModel.ChatProviders[
            Array.IndexOf(AiTabViewModel.ProviderKeys, _providerKey)];

        // Show only the relevant field group
        AzureFields.Visibility    = Visibility.Collapsed;
        OpenAiFields.Visibility   = Visibility.Collapsed;
        GroqFields.Visibility     = Visibility.Collapsed;
        AnthropicFields.Visibility= Visibility.Collapsed;
        GeminiFields.Visibility   = Visibility.Collapsed;
        MistralFields.Visibility  = Visibility.Collapsed;

        switch (_providerKey)
        {
            case "azure":
                AzureFields.Visibility = Visibility.Visible;
                var az = _config.Current.Azure;
                AzureEndpoint.Text     = az.Endpoint;
                AzureKey.Password      = az.Key;
                AzureWhisperDep.Text   = az.WhisperDeployment;
                AzureChatDep.Text      = az.ChatDeployment;
                break;

            case "openai":
                OpenAiFields.Visibility = Visibility.Visible;
                var oa = _config.Current.OpenAi;
                OpenAiKey.Password       = oa.Key;
                OpenAiWhisperModel.Text  = oa.WhisperModel;
                OpenAiChatModel.Text     = oa.ChatModel;
                break;

            case "groq":
                GroqFields.Visibility = Visibility.Visible;
                var gr = _config.Current.Groq;
                GroqKey.Password       = gr.Key;
                GroqWhisperModel.Text  = gr.WhisperModel;
                GroqChatModel.Text     = gr.ChatModel;
                break;

            case "anthropic":
                AnthropicFields.Visibility = Visibility.Visible;
                var an = _config.Current.Anthropic;
                AnthropicKey.Password      = an.Key;
                AnthropicChatModel.Text    = an.ChatModel;
                break;

            case "gemini":
                GeminiFields.Visibility = Visibility.Visible;
                var ge = _config.Current.Gemini;
                GeminiKey.Password    = ge.Key;
                GeminiChatModel.Text  = ge.ChatModel;
                break;

            case "mistral":
                MistralFields.Visibility = Visibility.Visible;
                var mi = _config.Current.Mistral;
                MistralKey.Password    = mi.Key;
                MistralChatModel.Text  = mi.ChatModel;
                break;
        }
    }

    /// <summary>Builds a standalone AppConfig snapshot reflecting the CURRENTLY TYPED field
    /// values (not yet saved) for this dialog's provider only — used by the Test button so
    /// testing reflects what's on screen right now, not stale saved credentials. Deliberately a
    /// throwaway object, never assigned into <see cref="_config"/>, so Cancel truly discards
    /// unsaved edits (matches the dialog's existing Save/Cancel contract).</summary>
    private AppConfig BuildConfigFromCurrentFields()
    {
        var snapshot = new AppConfig();
        switch (_providerKey)
        {
            case "azure":
                snapshot.Azure = new AzureConfig
                {
                    Endpoint = AzureEndpoint.Text.Trim(),
                    Key      = AzureKey.Password,
                };
                break;
            case "openai":
                snapshot.OpenAi = new OpenAiConfig { Key = OpenAiKey.Password };
                break;
            case "groq":
                snapshot.Groq = new GroqConfig { Key = GroqKey.Password };
                break;
            case "anthropic":
                snapshot.Anthropic = new AnthropicConfig { Key = AnthropicKey.Password };
                break;
            case "gemini":
                snapshot.Gemini = new GeminiConfig { Key = GeminiKey.Password };
                break;
            case "mistral":
                snapshot.Mistral = new MistralConfig { Key = MistralKey.Password };
                break;
        }
        return snapshot;
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        TestConnectionButton.IsEnabled = false;
        TestStatusText.Text = "Testing…";
        try
        {
            var result = await _ai.TestConnectionAsync(_providerKey, BuildConfigFromCurrentFields());
            TestStatusText.Text = result.Success ? $"✓ {result.Text}" : $"✗ {result.Error}";
        }
        finally { TestConnectionButton.IsEnabled = true; }
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        switch (_providerKey)
        {
            case "azure":
                _config.Current.Azure.Endpoint          = AzureEndpoint.Text.Trim();
                _config.Current.Azure.Key               = AzureKey.Password;
                _config.Current.Azure.WhisperDeployment = AzureWhisperDep.Text.Trim();
                _config.Current.Azure.ChatDeployment    = AzureChatDep.Text.Trim();
                break;

            case "openai":
                _config.Current.OpenAi.Key          = OpenAiKey.Password;
                _config.Current.OpenAi.WhisperModel = OpenAiWhisperModel.Text.Trim();
                _config.Current.OpenAi.ChatModel    = OpenAiChatModel.Text.Trim();
                break;

            case "groq":
                _config.Current.Groq.Key          = GroqKey.Password;
                _config.Current.Groq.WhisperModel = GroqWhisperModel.Text.Trim();
                _config.Current.Groq.ChatModel    = GroqChatModel.Text.Trim();
                break;

            case "anthropic":
                _config.Current.Anthropic.Key       = AnthropicKey.Password;
                _config.Current.Anthropic.ChatModel = AnthropicChatModel.Text.Trim();
                break;

            case "gemini":
                _config.Current.Gemini.Key       = GeminiKey.Password;
                _config.Current.Gemini.ChatModel = GeminiChatModel.Text.Trim();
                break;

            case "mistral":
                _config.Current.Mistral.Key       = MistralKey.Password;
                _config.Current.Mistral.ChatModel = MistralChatModel.Text.Trim();
                break;
        }

        _config.Save();
    }
}
