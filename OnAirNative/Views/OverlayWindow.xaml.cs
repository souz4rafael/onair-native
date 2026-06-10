using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OnAirNative.Services;
using OnAirNative.ViewModels;
using OnAirNative.Win32;
using Windows.Graphics;

namespace OnAirNative.Views;

/// <summary>
/// The transparent always-on-top overlay window.
/// All Win32 manipulation (transparency, click-through, content protection)
/// happens here via WindowService; business logic lives in OverlayViewModel.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    public OverlayViewModel  ViewModel   { get; }
    public ControllerWindow? Controller  { get; set; }

    private IntPtr     _hwnd;

    public OverlayWindow()
    {
        InitializeComponent();

        ViewModel = new OverlayViewModel(
            App.Config, App.Audio, App.Whisper, App.AiChat);

        // Wire ViewModel events → Win32 calls
        ViewModel.ClickThroughChanged += OnClickThroughChanged;
        // Browser navigation is handled by BrowserWindow (via App.xaml.cs)
        ViewModel.PropertyChanged            += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(OverlayViewModel.CurrentMode):  OnCurrentModeChanged(ViewModel.CurrentMode); break;
                case nameof(OverlayViewModel.ScriptText):   ScriptTextBlock.Text   = ViewModel.ScriptText; break;
                case nameof(OverlayViewModel.QaStatus):
                    QaStatusText.Text       = ViewModel.QaStatus;
                    QaStatusText.Visibility = string.IsNullOrEmpty(ViewModel.QaStatus) ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case nameof(OverlayViewModel.QaQuestion):
                    QaQuestionText.Text       = ViewModel.QaQuestion;
                    QaQuestionText.Visibility = string.IsNullOrEmpty(ViewModel.QaQuestion) ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case nameof(OverlayViewModel.QaAnswer):
                    QaAnswerText.Text       = ViewModel.QaAnswer;
                    QaAnswerText.Visibility = string.IsNullOrEmpty(ViewModel.QaAnswer) ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case nameof(OverlayViewModel.IsBusy):
                    QaBusy.IsActive    = ViewModel.IsBusy;
                    QaBusy.Visibility  = ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(OverlayViewModel.ScrollOffset):
                    ScriptScrollViewer.ScrollToVerticalOffset(ViewModel.ScrollOffset);
                    break;
                case nameof(OverlayViewModel.FontColor):
                    ScriptTextBlock.Foreground = ParseHexColor(ViewModel.FontColor);
                    break;
                case nameof(OverlayViewModel.IsVoiceActive):
                    if (ViewModel.ScrollMode == ViewModels.ScrollMode.Voice)
                    {
                        MoveBadgeText.Text = ViewModel.IsVoiceActive ? "🎙 SCROLL" : "🎙 LISTEN";
                    }
                    break;
                case nameof(OverlayViewModel.ScrollMode):
                    if (ViewModel.ScrollMode != ViewModels.ScrollMode.Voice)
                        MoveBadgeText.Text = ViewModel.IsClickThrough ? "LOCK" : "MOVE";
                    else
                        MoveBadgeText.Text = "🎙 LISTEN";
                    break;
                case nameof(OverlayViewModel.IsRecording):
                    // Recording state shown in Controller — no button in overlay
                    break;
            }
        };

        // Seed initial text
        ScriptTextBlock.Text = ViewModel.ScriptText;

        Activated += OnFirstActivated;
    }

    // ── Window setup (runs once after first Activate) ─────────────────────────

    private bool _setupDone;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_setupDone) return;
        _setupDone = true;

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "onAIr Native");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "overlay-init.log");

        try
        {
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} OnFirstActivated start\n");

            _hwnd = WindowService.GetHwnd(this);
            ViewModel.Hwnd = _hwnd;

            // Remove title bar — overlay is purely content
            WindowService.RemoveTitleBar(this);
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} title bar removed\n");

            // Restore saved geometry
            var saved = App.Config.Current.OverlayWindow;
            WindowService.SetPosition(this, saved.X, saved.Y);
            WindowService.SetSize(this, saved.Width, saved.Height);

            // Win32: always-on-top, no-taskbar-icon, transparent background
            WindowService.SetAlwaysOnTop(_hwnd, true);
            WindowService.MakeTransparent(_hwnd);
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} transparent set\n");

            // Start in Script mode
            OnCurrentModeChanged(OverlayMode.Script);

            // Apply persisted opacity
            var opacityByte = (byte)(App.Config.Current.Appearance.Opacity * 255 / 100);
            WindowService.SetOpacity(_hwnd, opacityByte);

            // Content protection: hide overlay from screen captures by default
            WindowService.SetContentProtection(_hwnd, App.Config.Current.OverlayProtected);

            // Start in Move Mode (interactive so user can position it on first launch)
            SetClickThrough(false);

            // Make the header bar draggable — register after layout is measured
            HeaderBar.SizeChanged += OnHeaderSizeChanged;

            // Hide from Alt+Tab (it's controlled via tray / controller)
            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
                exStyle | NativeMethods.WS_EX_TOOLWINDOW);

            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} OnFirstActivated done\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} EXCEPTION: {ex}\n");
        }
    }

    // ── WebView2 removed from overlay — browser uses separate BrowserWindow ──────
    // (WebView2 cannot render in WS_EX_LAYERED windows, which the overlay uses
    // for transparency. See BrowserWindow.xaml.cs for the implementation.)

    // ── Click-through / Move mode ─────────────────────────────────────────────

    private void OnClickThroughChanged(object? sender, bool clickThrough) =>
        SetClickThrough(clickThrough);

    private void SetClickThrough(bool enable)
    {
        WindowService.SetClickThrough(_hwnd, enable);
        // Badge is now a read-only status label
        MoveBadgeText.Text       = enable ? "🔒 LOCKED" : "🔓 UNLOCKED";
        MoveBadge.Background     = enable
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(180, 80, 40, 40))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(180, 30, 30, 60));
        MoveBadgeText.Foreground = enable
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 150, 150))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(220, 170, 170, 210));
    }

    // ── Mode label + panel visibility ─────────────────────────────────────────

    private void OnCurrentModeChanged(OverlayMode mode)
    {
        CurrentModeLabel.Text = mode == OverlayMode.QA ? "● Q&A" : "● Script";
        UpdatePanelVisibility(mode);
    }

    private void UpdatePanelVisibility(OverlayMode mode)
    {
        ScriptScrollViewer.Visibility = mode == OverlayMode.Script ? Visibility.Visible : Visibility.Collapsed;
        QaPanel.Visibility            = mode == OverlayMode.QA     ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Hex color helper ─────────────────────────────────────────────────────

    private static SolidColorBrush ParseHexColor(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6) hex = "FF" + hex;
            byte a = Convert.ToByte(hex[0..2], 16);
            byte r = Convert.ToByte(hex[2..4], 16);
            byte g = Convert.ToByte(hex[4..6], 16);
            byte b = Convert.ToByte(hex[6..8], 16);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
        }
        catch { return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 240, 240)); }
    }

    // ── Recording button removed — record lives in Controller's Q&A tab ─────

    // ── Header drag region (makes the header bar act as a title bar for dragging) ──

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateDragRegion();

    private void UpdateDragRegion()
    {
        var scale = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd))
                        is { } aw ? (double)aw.Size.Width / RootGrid.ActualWidth : 1.0;

        var dragRects = new Windows.Graphics.RectInt32[]
        {
            new()
            {
                X      = 0,
                Y      = 0,
                Width  = (int)(HeaderBar.ActualWidth  * scale),
                Height = (int)(HeaderBar.ActualHeight * scale),
            },
        };
        AppWindow.TitleBar.SetDragRectangles(dragRects);
    }

    // ── Move badge tap = toggle move mode ────────────────────────────────────
    // (now read-only — toggle moved to Controller footer)
    // ✕ close button also removed — overlay controlled entirely from Controller/tray

    // ── Local key handling ─────────────────────────────────────────────────────
    // Note: Window in WinUI 3 does not have OnKeyDown virtual method.
    // Global hotkeys are handled by HotkeyService (RegisterHotKey) — no override needed here.

    // ── Save geometry on close ────────────────────────────────────────────────

    public void SaveGeometry()
    {
        var (x, y, w, h) = WindowService.GetGeometry(this);
        var s = App.Config.Current.OverlayWindow;
        s.X = x; s.Y = y; s.Width = w; s.Height = h;
    }
}
