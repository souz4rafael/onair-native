using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnAirNative.Services;

namespace OnAirNative.ViewModels;

public partial class ScrollTabViewModel : ObservableObject
{
    private readonly ConfigService   _config;
    private readonly OverlayViewModel _overlay;

    [ObservableProperty] private string _loadedFileName = "No file loaded";
    [ObservableProperty] private int    _scrollStep;
    [ObservableProperty] private int    _scrollSpeed;
    [ObservableProperty] private int    _selectedScrollModeIndex; // 0=Manual, 1=Auto, 2=Voice
    [ObservableProperty] private int    _fontSize;
    [ObservableProperty] private double _opacity;

    public ScrollTabViewModel(ConfigService config, OverlayViewModel overlay)
    {
        _config  = config;
        _overlay = overlay;

        var a = config.Current.Appearance;
        ScrollStep    = a.ScrollStep;
        ScrollSpeed   = a.ScrollSpeed;
        FontSize      = a.FontSize;
        Opacity       = a.Opacity / 100.0;

        overlay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.LoadedFileName))
                LoadedFileName = overlay.LoadedFileName;
        };
    }

    [RelayCommand]
    public void ScrollUp() => _overlay.Scroll(-ScrollStep);

    [RelayCommand]
    public void ScrollDown() => _overlay.Scroll(ScrollStep);

    [RelayCommand]
    public async Task OpenFileAsync(Microsoft.UI.Xaml.Window ownerWindow)
        => await _overlay.OpenFilePickerAsync(ownerWindow);

    [RelayCommand]
    public void ResetScroll() => _overlay.Scroll(-(int)_overlay.ScrollOffset);

    public void SetFontColor(string hexColor)
    {
        _overlay.FontColor                  = hexColor;
        _config.Current.Appearance.FontColor = hexColor;
        _config.Save();
    }

    partial void OnScrollStepChanged(int value)
    {
        _config.Current.Appearance.ScrollStep = value;
        _config.Save();
    }

    partial void OnScrollSpeedChanged(int value)
    {
        _config.Current.Appearance.ScrollSpeed = value;
        _config.Save();
    }

    partial void OnSelectedScrollModeIndexChanged(int value)
    {
        _overlay.ScrollMode = value switch
        {
            1 => ScrollMode.Auto,
            2 => ScrollMode.Voice,
            _ => ScrollMode.Manual,
        };
    }

    partial void OnFontSizeChanged(int value)
    {
        _config.Current.Appearance.FontSize = value;
        _overlay.FontSize = value;
        _config.Save();
    }

    partial void OnOpacityChanged(double value)
    {
        _config.Current.Appearance.Opacity = (int)(value * 100);
        _overlay.Opacity = value;
        // Opacity applied to window by View via WindowService
        OpacityChanged?.Invoke(this, value);
        _config.Save();
    }

    public event EventHandler<double>? OpacityChanged;
}
