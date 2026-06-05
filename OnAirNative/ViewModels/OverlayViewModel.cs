using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OnAirNative.Services;

namespace OnAirNative.ViewModels;

public enum OverlayMode { Script, QA }
public enum ScrollMode  { Manual, Auto, Voice }

/// <summary>
/// ViewModel for the Overlay window.
/// Owns the script text, mode, recording state, Q&A state, and scroll behaviour.
/// All hotkey handlers in App.xaml.cs delegate here.
/// </summary>
public partial class OverlayViewModel : ObservableObject
{
    private readonly ConfigService    _config;
    private readonly AudioService     _audio;
    private readonly WhisperService   _whisper;
    private readonly AiChatService    _ai;
    private readonly DispatcherQueue  _uiQueue;

    // Injected from the View after window creation
    public IntPtr Hwnd { get; set; }

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty] private OverlayMode _currentMode  = OverlayMode.Script;
    [ObservableProperty] private ScrollMode  _scrollMode   = ScrollMode.Manual;
    [ObservableProperty] private bool        _isMoveModeActive = true;
    [ObservableProperty] private bool        _isClickThrough   = false;

    [ObservableProperty] private string _scriptText     = "Load a script to begin.\n\nUse Ctrl+Alt+O or drag a .txt file here.";
    [ObservableProperty] private string _loadedFileName = "";
    [ObservableProperty] private double _scrollOffset   = 0;

    [ObservableProperty] private bool   _isRecording  = false;
    [ObservableProperty] private bool   _isBusy       = false;
    [ObservableProperty] private string _qaQuestion   = "";
    [ObservableProperty] private string _qaAnswer     = "";
    [ObservableProperty] private string _qaStatus     = "";

    [ObservableProperty] private double _opacity   = 0.75;
    [ObservableProperty] private int    _fontSize  = 22;
    [ObservableProperty] private string _fontColor = "#F0F0F0";

    // Voice-scroll indicators
    [ObservableProperty] private bool   _isVoiceActive = false;
    [ObservableProperty] private double _micLevel      = 0;  // 0-100, for UI feedback

    // Raised so the View can apply WS_EX_TRANSPARENT via WindowService
    public event EventHandler<bool>?   ClickThroughChanged;

    public OverlayViewModel(
        ConfigService config, AudioService audio,
        WhisperService whisper, AiChatService ai)
    {
        _config  = config;
        _audio   = audio;
        _whisper = whisper;
        _ai      = ai;

        // Capture UI thread dispatcher for cross-thread scroll calls (voice mode)
        _uiQueue = DispatcherQueue.GetForCurrentThread();

        var a = config.Current.Appearance;
        Opacity   = a.Opacity / 100.0;
        FontSize  = a.FontSize;
        FontColor = a.FontColor;
    }

    // ── Scroll mode changes ───────────────────────────────────────────────────

    partial void OnScrollModeChanged(ScrollMode value)
    {
        StopAutoScroll();
        _audio.StopVoiceMonitor();
        IsVoiceActive = false;

        switch (value)
        {
            case ScrollMode.Auto:  StartAutoScroll();  break;
            case ScrollMode.Voice: StartVoiceScroll(); break;
        }
    }

    // ── Auto-scroll (timer-based) ─────────────────────────────────────────────

    private DispatcherTimer? _autoTimer;

    private void StartAutoScroll()
    {
        _autoTimer = new DispatcherTimer
        {
            // Fixed 50ms tick (~20fps); amount per tick comes from ScrollSpeed config
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _autoTimer.Tick += AutoScrollTick;
        _autoTimer.Start();
    }

    private void StopAutoScroll()
    {
        if (_autoTimer is null) return;
        _autoTimer.Stop();
        _autoTimer.Tick -= AutoScrollTick;
        _autoTimer = null;
    }

    private void AutoScrollTick(object? sender, object e)
    {
        // speed 1-100 → 1–10 pixels per tick at 20fps = 20–200 px/s
        var pxPerTick = Math.Max(1, _config.Current.Appearance.ScrollSpeed / 10);
        Scroll(pxPerTick);
    }

    // ── Voice scroll (RMS microphone monitoring) ──────────────────────────────

    // Debounce: accumulate voice detections and scroll only every N callbacks
    private int _voiceCallbackCount;
    private const int VoiceScrollEvery = 3; // scroll once every 3 audio callbacks

    private void StartVoiceScroll()
    {
        _voiceCallbackCount = 0;
        // Pass the configured device ID so the correct mic is used
        _audio.StartVoiceMonitor(OnVoiceRms, _config.Current.AudioDeviceId);
    }

    private void OnVoiceRms(float rms)
    {
        var threshold = (float)_config.Current.Appearance.VoiceRmsThreshold;
        bool active   = rms > threshold;

        // Debounce: scroll once every VoiceScrollEvery callbacks while voice is detected
        if (active)
        {
            _voiceCallbackCount++;
            if (_voiceCallbackCount >= VoiceScrollEvery)
            {
                _voiceCallbackCount = 0;
                _uiQueue.TryEnqueue(() =>
                {
                    Scroll(Math.Max(1, _config.Current.Appearance.ScrollSpeed / 10));
                    IsVoiceActive = true;
                    MicLevel = Math.Round(rms, 1);
                });
            }
        }
        else
        {
            _voiceCallbackCount = 0;
            if (IsVoiceActive || MicLevel > 0)
                _uiQueue.TryEnqueue(() => { IsVoiceActive = false; MicLevel = Math.Round(rms, 1); });
        }
    }

    // ── Mode cycling (Ctrl+Alt+M) ─────────────────────────────────────────────

    public void CycleMode() => CurrentMode = CurrentMode switch
    {
        OverlayMode.Script => OverlayMode.QA,
        OverlayMode.QA     => OverlayMode.Script,
        _                  => OverlayMode.Script,
    };

    // ── Move mode (Ctrl+Alt+Home) ─────────────────────────────────────────────

    public void ToggleMoveMode() => SetMoveMode(!IsMoveModeActive);

    public void SetMoveMode(bool movable)
    {
        IsMoveModeActive = movable;
        IsClickThrough   = !movable;
        ClickThroughChanged?.Invoke(this, IsClickThrough);
    }

    // ── Script loading ────────────────────────────────────────────────────────

    public async Task LoadScriptAsync(string filePath)
    {
        try
        {
            ScriptText     = await File.ReadAllTextAsync(filePath);
            LoadedFileName = Path.GetFileName(filePath);
            ScrollOffset   = 0;
            CurrentMode    = OverlayMode.Script;
        }
        catch (Exception ex)
        {
            ScriptText = $"⚠ Could not load file:\n{ex.Message}";
        }
    }

    public async Task OpenFilePickerAsync(Window ownerWindow)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(ownerWindow));
        picker.FileTypeFilter.Add(".txt");
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSingleFileAsync();
        if (file is not null) await LoadScriptAsync(file.Path);
    }

    // ── Scroll ────────────────────────────────────────────────────────────────

    public void Scroll(int delta) =>
        ScrollOffset = Math.Max(0, ScrollOffset + delta);

    // ── Q&A / Recording (Ctrl+Alt+R) ─────────────────────────────────────────

    [RelayCommand]
    public async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            // Stop recording → transcribe → AI answer
            IsRecording = false;
            IsBusy      = true;
            QaStatus    = "Transcribing…";

            var wavData = await _audio.StopRecordingAsync();
            var tx      = await _whisper.TranscribeAsync(wavData, _config.Current);

            if (!tx.Success)
            {
                QaStatus = $"Transcription failed: {tx.Error}";
                IsBusy   = false;
                return;
            }

            QaQuestion = tx.Text;
            QaStatus   = "Getting answer…";

            var ans = await _ai.GetAnswerAsync(tx.Text, _config.Current);
            QaAnswer = ans.Success ? ans.Text : $"Error: {ans.Error}";
            QaStatus = ans.Success ? "" : "AI call failed";
            IsBusy   = false;
        }
        else
        {
            // Stop voice scroll if active (can't monitor + record simultaneously)
            if (ScrollMode == ScrollMode.Voice)
                _audio.StopVoiceMonitor();

            CurrentMode = OverlayMode.QA;
            QaQuestion  = "";
            QaAnswer    = "";
            QaStatus    = "Recording… (Ctrl+Alt+R to stop)";
            IsRecording = true;
            await _audio.StartRecordingAsync(_config.Current.AudioRecordingSource, _config.Current.AudioDeviceId);
        }
    }

    // ── Appearance sync from Controller ──────────────────────────────────────

    public void ApplyAppearance(int fontSize, double opacity, string fontColor)
    {
        FontSize  = fontSize;
        Opacity   = opacity;
        FontColor = fontColor;

        _config.Current.Appearance.FontSize  = fontSize;
        _config.Current.Appearance.Opacity   = (int)(opacity * 100);
        _config.Current.Appearance.FontColor = fontColor;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Cleanup()
    {
        StopAutoScroll();
        _audio.StopVoiceMonitor();
    }
}
