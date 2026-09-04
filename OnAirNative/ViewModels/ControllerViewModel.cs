using CommunityToolkit.Mvvm.ComponentModel;
using OnAirNative.Services;

namespace OnAirNative.ViewModels;

/// <summary>Root ViewModel for the Controller window — owns the tab sub-VMs.</summary>
public partial class ControllerViewModel : ObservableObject
{
    public ScrollTabViewModel    ScrollTab    { get; }
    public AiTabViewModel        AiTab        { get; }
    public InsightsTabViewModel  InsightsTab  { get; }
    public AboutTabViewModel     AboutTab     { get; }

    [ObservableProperty] private bool _controllerProtected;
    [ObservableProperty] private bool _overlayProtected;
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private bool _remoteControlEnabled = true;

    private readonly ConfigService    _config;
    private readonly OverlayViewModel _overlay;

    public ControllerViewModel(
        ConfigService config,
        OverlayViewModel overlay,
        AiChatService ai,
        WhisperService whisper,
        UpdateService update)
    {
        _config  = config;
        _overlay = overlay;

        ControllerProtected  = config.Current.ControllerProtected;
        OverlayProtected     = config.Current.OverlayProtected;
        Theme                = config.Current.Theme;
        RemoteControlEnabled = config.Current.RemoteControlEnabled;

        ScrollTab   = new ScrollTabViewModel(config, overlay);
        AiTab       = new AiTabViewModel(config, ai, whisper);
        InsightsTab = new InsightsTabViewModel(config);
        AboutTab    = new AboutTabViewModel(update);
    }

    partial void OnControllerProtectedChanged(bool value)
    {
        _config.Current.ControllerProtected = value;
        ControllerProtectionChanged?.Invoke(this, value);
    }

    partial void OnOverlayProtectedChanged(bool value)
    {
        _config.Current.OverlayProtected = value;
        OverlayProtectionChanged?.Invoke(this, value);
    }

    partial void OnThemeChanged(string value)
    {
        _config.Current.Theme = value;
        ThemeChanged?.Invoke(this, value);
    }

    partial void OnRemoteControlEnabledChanged(bool value)
    {
        _config.Current.RemoteControlEnabled = value;
        RemoteControlEnabledChanged?.Invoke(this, value);
    }

    /// <summary>Raised when the Controller screen-share protection toggle changes.</summary>
    public event EventHandler<bool>? ControllerProtectionChanged;

    /// <summary>
    /// Raised when the overlay screen-share protection toggle changes. When false,
    /// the teleprompter overlay is visible to viewers of a shared screen/recording.
    /// </summary>
    public event EventHandler<bool>? OverlayProtectionChanged;

    /// <summary>Raised when the app theme changes ("System" | "Light" | "Dark").</summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>Raised when the user toggles the Stream Deck remote control server on/off —
    /// the View wires this to App.StartRemoteControl()/StopRemoteControl().</summary>
    public event EventHandler<bool>? RemoteControlEnabledChanged;
}
