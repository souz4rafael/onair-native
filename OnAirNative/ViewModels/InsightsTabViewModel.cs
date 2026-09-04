using CommunityToolkit.Mvvm.ComponentModel;
using OnAirNative.Services;

namespace OnAirNative.ViewModels;

/// <summary>ViewModel for the "AI Insights" Controller tab — owns the independent appearance
/// settings for the floating <c>InsightWindow</c> (font size/family/opacity; font color goes
/// through <see cref="SetFontColor"/>, same asymmetry as <see cref="ScrollTabViewModel"/>).
/// Deliberately does NOT own the Pacing/Usage/Recap/Follow-up read-outs shown on this tab — those
/// are mirrored live from <see cref="OverlayViewModel"/>/<see cref="AiTabViewModel"/> directly by
/// ControllerWindow's code-behind, exactly like the Q&amp;A tab did before this tab existed.</summary>
public partial class InsightsTabViewModel : ObservableObject
{
    private readonly ConfigService _config;

    [ObservableProperty] private int    _fontSize;
    [ObservableProperty] private string _fontFamily = "Segoe UI";
    [ObservableProperty] private double _opacity;

    public InsightsTabViewModel(ConfigService config)
    {
        _config = config;

        var a = config.Current.InsightAppearance;
        FontSize   = a.FontSize;
        FontFamily = a.FontFamily;
        Opacity    = a.Opacity / 100.0;
    }

    /// <summary>Sets the Insights window's font color — a plain method rather than an
    /// [ObservableProperty], mirroring <see cref="ScrollTabViewModel.SetFontColor"/>: preset
    /// swatch buttons and the custom-hex box both call this directly instead of round-tripping
    /// through a bound property.</summary>
    public void SetFontColor(string hexColor)
    {
        _config.Current.InsightAppearance.FontColor = hexColor;
        FontColorChanged?.Invoke(this, hexColor);
        _config.Save();
    }

    partial void OnFontSizeChanged(int value)
    {
        _config.Current.InsightAppearance.FontSize = value;
        FontSizeChanged?.Invoke(this, value);
        _config.Save();
    }

    partial void OnFontFamilyChanged(string value)
    {
        _config.Current.InsightAppearance.FontFamily = value;
        FontFamilyChanged?.Invoke(this, value);
        _config.Save();
    }

    partial void OnOpacityChanged(double value)
    {
        _config.Current.InsightAppearance.Opacity = (int)(value * 100);
        // Opacity applied to the window by the View via WindowService, same as ScrollTabViewModel.
        OpacityChanged?.Invoke(this, value);
        _config.Save();
    }

    public event EventHandler<double>? OpacityChanged;
    public event EventHandler<int>?    FontSizeChanged;
    public event EventHandler<string>? FontFamilyChanged;
    public event EventHandler<string>? FontColorChanged;
}
