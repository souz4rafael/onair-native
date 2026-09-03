using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnAirNative.Models;
using OnAirNative.Services;

namespace OnAirNative.ViewModels;

public partial class ScrollTabViewModel : ObservableObject
{
    private readonly ConfigService   _config;
    private readonly OverlayViewModel _overlay;

    [ObservableProperty] private string _loadedFileName = "No file loaded";
    [ObservableProperty] private int    _scrollStep;
    [ObservableProperty] private int    _scrollSpeed;
    [ObservableProperty] private int    _voiceScrollSpeed;
    [ObservableProperty] private int    _selectedScrollModeIndex; // 0=Manual, 1=Auto, 2=Voice
    [ObservableProperty] private int    _fontSize;
    [ObservableProperty] private string _fontFamily = "Segoe UI";
    [ObservableProperty] private double _opacity;

    // Chapter navigation — mirrors overlay.ScriptDocument.Chapters (same "mirror the Overlay
    // ViewModel for Controller display" pattern as LoadedFileName below). An
    // ObservableCollection rather than an [ObservableProperty] list because the Controller's
    // code-behind rebuilds the clickable chapter list off CollectionChanged, not a single
    // property-changed notification — see ControllerWindow.PopulateChapters.
    public ObservableCollection<ChapterInfo> Chapters { get; } = new();

    public ScrollTabViewModel(ConfigService config, OverlayViewModel overlay)
    {
        _config  = config;
        _overlay = overlay;

        var a = config.Current.Appearance;
        ScrollStep       = a.ScrollStep;
        ScrollSpeed      = a.ScrollSpeed;
        VoiceScrollSpeed = a.VoiceScrollSpeed;
        FontSize         = a.FontSize;
        FontFamily       = a.FontFamily;
        Opacity          = a.Opacity / 100.0;

        overlay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.LoadedFileName))
                LoadedFileName = overlay.LoadedFileName;
            else if (e.PropertyName == nameof(OverlayViewModel.ScriptDocument))
            {
                Chapters.Clear();
                foreach (var chapter in overlay.ScriptDocument.Chapters) Chapters.Add(chapter);
            }
        };
    }

    [RelayCommand]
    public void ScrollUp() => _overlay.Scroll(-ScrollStep);

    [RelayCommand]
    public void ScrollDown() => _overlay.Scroll(ScrollStep);

    [RelayCommand]
    public void JumpToChapter(ChapterInfo chapter) => _overlay.JumpToBlock(chapter.BlockIndex);

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

    partial void OnVoiceScrollSpeedChanged(int value)
    {
        _config.Current.Appearance.VoiceScrollSpeed = value;
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

    partial void OnFontFamilyChanged(string value)
    {
        _config.Current.Appearance.FontFamily = value;
        _overlay.FontFamily = value;
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
