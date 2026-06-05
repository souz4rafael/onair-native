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

        ScrollTab = new ScrollTabViewModel(config, overlay);
        AiTab     = new AiTabViewModel(config, ai);
        AboutTab  = new AboutTabViewModel();
    }

    partial void OnControllerProtectedChanged(bool value)
    {
        _config.Current.ControllerProtected = value;
        ControllerProtectionChanged?.Invoke(this, value);
    }

    /// <summary>Raised when the Controller screen-share protection toggle changes.</summary>
    public event EventHandler<bool>? ControllerProtectionChanged;
}
