using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OnAirNative.Services;
using OnAirNative.ViewModels;

namespace OnAirNative.Views.Dialogs;

/// <summary>
/// ContentDialog for editing provider-specific credentials and model names.
/// Shows/hides field groups based on the currently selected provider.
/// </summary>
public sealed partial class ProviderConfigDialog : ContentDialog
{
    private readonly ConfigService  _config;
    private readonly AiTabViewModel _vm;

    public ProviderConfigDialog(ConfigService config, AiTabViewModel vm)
    {
        InitializeComponent();
        _config = config;
        _vm     = vm;

        PrimaryButtonClick += OnSave;
        LoadFields();
    }

    private void LoadFields()
    {
        var provider = _config.Current.Provider;
        ProviderNameText.Text = AiTabViewModel.ChatProviders[
            Array.IndexOf(AiTabViewModel.ProviderKeys, provider)];

        // Show only the relevant field group
        AzureFields.Visibility    = Visibility.Collapsed;
        OpenAiFields.Visibility   = Visibility.Collapsed;
        GroqFields.Visibility     = Visibility.Collapsed;
        AnthropicFields.Visibility= Visibility.Collapsed;
        GeminiFields.Visibility   = Visibility.Collapsed;
        MistralFields.Visibility  = Visibility.Collapsed;

        switch (provider)
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

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var provider = _config.Current.Provider;

        switch (provider)
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
