using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OnAirNative.Services;
using OnAirNative.ViewModels;
using OnAirNative.Win32;

namespace OnAirNative.Views;

/// <summary>
/// The "AI Insights" window — a second always-on-top transparent overlay, independent from the
/// TP (<see cref="OverlayWindow"/>), so both can be shown/hidden/resized/moved separately and be
/// open at the same time. Deliberately owns NO state of its own: it reads
/// <see cref="OverlayViewModel.InsightText"/> from the SAME <see cref="OverlayViewModel"/>
/// instance the TP uses (passed in via the constructor), so
/// <see cref="App.ShowInsightRemote"/>/<see cref="App.ClearInsightRemote"/> (and therefore
/// onair_show_insight/onair_clear_insight over MCP) keep working completely unchanged — only the
/// display surface changed, not the write path.
/// </summary>
public sealed partial class InsightWindow : Window
{
    private readonly OverlayViewModel _sharedViewModel;
    private IntPtr _hwnd;
    private bool   _isLocked;
    private string _fontColorHex = "#F0F0F0";

    private const string PlaceholderText = "No insights yet";

    // Fixed default position, distinct from the TP's own fixed (0,0) default (see
    // OverlayWindow.OnFirstActivated) so the two windows don't stack directly on top of each
    // other the first time both are opened. Like the TP, position is intentionally never
    // persisted/restored (see SaveGeometry) — only size is.
    private const double DefaultX = 820;
    private const double DefaultY = 40;

    public InsightWindow(OverlayViewModel sharedViewModel)
    {
        InitializeComponent();
        _sharedViewModel = sharedViewModel;

        _sharedViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.InsightText))
                UpdateInsightText();
        };
        UpdateInsightText();

        Activated += OnFirstActivated;
    }

    private void UpdateInsightText()
    {
        var text = _sharedViewModel.InsightText;
        InsightTextBlock.Text       = string.IsNullOrEmpty(text) ? PlaceholderText : text;
        InsightTextBlock.Foreground = string.IsNullOrEmpty(text)
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 112, 112, 138))
            : ParseHexColor(_fontColorHex);
    }

    // ── Appearance (independent from the TP — see InsightsTabViewModel/InsightAppearanceConfig) ─

    /// <summary>Applies the "AI Insights" tab's font size setting.</summary>
    public void SetFontSize(int size) => InsightTextBlock.FontSize = size;

    /// <summary>Applies the "AI Insights" tab's font family setting.</summary>
    public void SetFontFamily(string family) =>
        InsightTextBlock.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(family);

    /// <summary>Applies the "AI Insights" tab's font color setting — remembered so the next
    /// <see cref="UpdateInsightText"/> call (e.g. a new pushed insight) keeps using it, while the
    /// muted placeholder-gray shown when there's no insight yet stays fixed regardless.</summary>
    public void SetFontColor(string hexColor)
    {
        _fontColorHex = hexColor;
        UpdateInsightText();
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush ParseHexColor(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6) hex = "FF" + hex;
            byte a = Convert.ToByte(hex[0..2], 16);
            byte r = Convert.ToByte(hex[2..4], 16);
            byte g = Convert.ToByte(hex[4..6], 16);
            byte b = Convert.ToByte(hex[6..8], 16);
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
        }
        catch { return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 240, 240)); }
    }

    // ── Window setup (runs once after first Activate) ─────────────────────────

    private bool _setupDone;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_setupDone) return;
        _setupDone = true;

        try
        {
            _hwnd = WindowService.GetHwnd(this);

            // Title bar is removed below, but the icon still shows in Task Manager,
            // Alt-Tab (if ever unhidden) and Snap Assist thumbnails.
            WindowService.SetWindowIcon(this);

            // Remove title bar — this window is purely content, same as the TP.
            WindowService.RemoveTitleBar(this);

            // Restore saved size, but always position at the fixed default above rather than
            // the last saved X/Y — same off-screen-after-a-monitor-change reasoning as the TP.
            var saved = App.Config.Current.InsightWindow;
            WindowService.SetPosition(this, DefaultX, DefaultY);
            WindowService.SetSize(this, saved.Width, saved.Height);

            // Win32: always-on-top, no-taskbar-icon, transparent background
            WindowService.SetAlwaysOnTop(_hwnd, true);
            WindowService.MakeTransparent(_hwnd);

            // Independent appearance (see InsightsTabViewModel/InsightAppearanceConfig) — its own
            // font size/family/color/opacity, separate from the TP's.
            var insightAppearance = App.Config.Current.InsightAppearance;
            SetFontSize(insightAppearance.FontSize);
            SetFontFamily(insightAppearance.FontFamily);
            SetFontColor(insightAppearance.FontColor);
            var opacityByte = (byte)(insightAppearance.Opacity * 255 / 100);
            WindowService.SetOpacity(_hwnd, opacityByte);

            // Content protection: hidden from screen captures only if the user opted in
            WindowService.SetContentProtection(_hwnd, App.Config.Current.InsightWindowProtected);

            // Start in Move Mode (interactive so the user can position/resize it on first show)
            SetLocked(false);

            // Make the header bar draggable — register after layout is measured
            HeaderBar.SizeChanged += (_, _) => UpdateDragRegion();

            // Hide from Alt+Tab (it's controlled via the Controller only, same as the TP)
            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
                exStyle | NativeMethods.WS_EX_TOOLWINDOW);
        }
        catch { /* best-effort setup, mirrors OverlayWindow's own try/catch */ }
    }

    // ── Lock / unlock (click-through vs interactive/movable/resizable) ────────

    public bool IsLocked => _isLocked;

    public void SetLocked(bool locked)
    {
        _isLocked = locked;
        WindowService.SetClickThrough(_hwnd, locked);
        MoveBadgeText.Text       = locked ? "🔒 LOCKED" : "🔓 UNLOCKED";
        MoveBadge.Background     = locked
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(180, 80, 40, 40))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(180, 30, 30, 60));
        MoveBadgeText.Foreground = locked
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 150, 150))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(220, 170, 170, 210));
    }

    // ── Screen-share protection ────────────────────────────────────────────────

    public void SetContentProtection(bool protect) =>
        WindowService.SetContentProtection(_hwnd, protect);

    // ── Header drag region ─────────────────────────────────────────────────────

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

    // ── Save geometry on close ─────────────────────────────────────────────────

    public void SaveGeometry()
    {
        // Position is intentionally not persisted — this window always reopens at the fixed
        // default (see OnFirstActivated) so a stale position from a disconnected monitor or
        // different multi-monitor layout can't leave it off-screen. Only the size is remembered.
        var (_, _, w, h) = WindowService.GetGeometry(this);
        var s = App.Config.Current.InsightWindow;
        s.Width = w; s.Height = h;
    }
}
