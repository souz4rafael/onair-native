using CommunityToolkit.Mvvm.ComponentModel;
using OnAirNative.Services;

namespace OnAirNative.ViewModels;

/// <summary>Root ViewModel for the Controller window — owns the tab sub-VMs.</summary>
public partial class ControllerViewModel : ObservableObject
{
    public ScrollTabViewModel  ScrollTab { get; }
    public AiTabViewModel      AiTab     { get; }
    public AboutTabViewModel   AboutTab  { get; }

    [ObservableProperty] private bool _controllerProtected;
    [ObservableProperty] private bool _overlayProtected;

    private readonly ConfigService    _config;
    private readonly OverlayViewModel _overlay;

    public ControllerViewModel(
        ConfigService config,
        OverlayViewModel overlay,
        AiChatService ai)
    {
        _config  = config;
        _overlay = overlay;

        ControllerProtected = config.Current.ControllerProtected;
        OverlayProtected    = config.Current.OverlayProtected;

        ScrollTab = new ScrollTabViewModel(config, overlay);
        AiTab     = new AiTabViewModel(config, ai);
        AboutTab  = new AboutTabViewModel();
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

    /// <summary>Raised when the Controller screen-share protection toggle changes.</summary>
    public event EventHandler<bool>? ControllerProtectionChanged;

    /// <summary>
    /// Raised when the overlay screen-share protection toggle changes. When false,
    /// the teleprompter overlay is visible to viewers of a shared screen/recording.
    /// </summary>
    public event EventHandler<bool>? OverlayProtectionChanged;
}
