using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OnAirNative.Services;
using OnAirNative.ViewModels;
using OnAirNative.Win32;

namespace OnAirNative.Views;

/// <summary>
/// The "AI Insights" window — a second always-on-top transparent overlay, independent from the
/// TP (<see cref="OverlayWindow"/>), so both can be shown/hidden/resized/moved separately and be
/// open at the same time. This is the SINGLE place all AI-generated meta-info is displayed: the
/// Copilot-insight text an external MCP agent pushes, the pacing (words-per-minute) coach, the
/// running token-usage summary, and follow-up question suggestions — the TP and the Controller/
/// Web Remote "AI Insights" tabs no longer render any of these live values (they keep only
/// controls: open/lock/hide, appearance, on/off toggles, the usage-reset button).
/// Reads <see cref="OverlayViewModel.InsightText"/>/<see cref="OverlayViewModel.PacingSummary"/>/
/// <see cref="OverlayViewModel.FollowUpSuggestions"/> from the SAME <see cref="OverlayViewModel"/>
/// instance the TP uses (passed in via the constructor), so
/// <see cref="App.ShowInsightRemote"/>/<see cref="App.ClearInsightRemote"/> (and therefore
/// onair_show_insight/onair_clear_insight over MCP) keep working completely unchanged — only the
/// display surface changed, not the write path. Token usage is read directly from the shared
/// <see cref="AiChatService"/> singleton (rather than via AiTabViewModel/ControllerViewModel)
/// purely for constructor-ordering reasons: App.xaml.cs constructs this window BEFORE the
/// Controller's ViewModel exists (ControllerViewModel is only created in
/// ControllerWindow.InitViewModel, on first Activate), while AiChatService (App.AiChat) is
/// already fully constructed at that point.
/// </summary>
public sealed partial class InsightWindow : Window
{
    private readonly OverlayViewModel _sharedViewModel;
    private readonly AiChatService    _aiChat;
    private IntPtr _hwnd;
    private bool   _isLocked;
    private string _fontColorHex = "#F0F0F0";

    private const string PlaceholderText = "No external AI insights yet";

    // Fixed default position, distinct from the TP's own fixed (0,0) default (see
    // OverlayWindow.OnFirstActivated) so the two windows don't stack directly on top of each
    // other the first time both are opened. Like the TP, position is intentionally never
    // persisted/restored (see SaveGeometry) — only size is.
    private const double DefaultX = 820;
    private const double DefaultY = 40;

    public InsightWindow(OverlayViewModel sharedViewModel, AiChatService aiChat)
    {
        InitializeComponent();
        _sharedViewModel = sharedViewModel;
        _aiChat          = aiChat;

        _sharedViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.InsightText))
                UpdateInsightText();
            else if (e.PropertyName == nameof(OverlayViewModel.PacingSummary))
                UpdatePacingSummary();
            else if (e.PropertyName == nameof(OverlayViewModel.ShowPacingInInsights))
                UpdatePacingVisibility();
            else if (e.PropertyName == nameof(OverlayViewModel.ShowTokenUsageInInsights))
                UpdateTokenUsageVisibility();
            else if (e.PropertyName == nameof(OverlayViewModel.ShowFollowUpsInInsights))
                UpdateFollowUpVisibility();
            else if (e.PropertyName == nameof(OverlayViewModel.ShowExternalInsightsInInsights))
                UpdateExternalInsightVisibility();
        };
        // FollowUpSuggestions is an ObservableCollection, not a property — same pattern as
        // OverlayWindow's own (now-removed) subscription used.
        _sharedViewModel.FollowUpSuggestions.CollectionChanged += (_, _) => PopulateFollowUpSuggestions();
        UpdateInsightText();
        UpdatePacingSummary();
        UpdatePacingVisibility();
        UpdateTokenUsageVisibility();
        UpdateFollowUpVisibility();
        UpdateExternalInsightVisibility();
        PopulateFollowUpSuggestions();

        // Token usage — refreshed after every chat call (and after a manual reset), same trigger
        // AiTabViewModel.RefreshUsageSummary reacts to; this mirrors that method's exact string
        // format so both stay consistent even though they're now two independent computations.
        _aiChat.UsageChanged += (_, _) => UpdateUsageSummary();
        UpdateUsageSummary();

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

    private void UpdatePacingSummary() => PacingSummaryTextBlock.Text = _sharedViewModel.PacingSummary;

    /// <summary>Shows/hides the footer's Pacing section per OverlayViewModel.
    /// ShowPacingInInsights — a pure display toggle, pacing itself is always computed
    /// regardless (see that property's doc comment).</summary>
    private void UpdatePacingVisibility() =>
        PacingSectionPanel.Visibility = _sharedViewModel.ShowPacingInInsights ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows/hides the footer's Token Usage section per OverlayViewModel.
    /// ShowTokenUsageInInsights — a pure display toggle, usage itself is always tracked
    /// regardless.</summary>
    private void UpdateTokenUsageVisibility() =>
        TokenUsageSectionPanel.Visibility = _sharedViewModel.ShowTokenUsageInInsights ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows/hides the top Questions (follow-up suggestions) section per
    /// OverlayViewModel.ShowFollowUpsInInsights — a pure display toggle, independent of
    /// AppConfig.ShowFollowUpSuggestions (which instead controls whether suggestions are
    /// generated at all).</summary>
    private void UpdateFollowUpVisibility() =>
        FollowUpSectionPanel.Visibility = _sharedViewModel.ShowFollowUpsInInsights ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows/hides the External AI Insights section per OverlayViewModel.
    /// ShowExternalInsightsInInsights — a pure display toggle, the pushed text itself is always
    /// received/stored regardless (see <see cref="UpdateInsightText"/>).</summary>
    private void UpdateExternalInsightVisibility() =>
        ExternalInsightSectionPanel.Visibility = _sharedViewModel.ShowExternalInsightsInInsights ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Mirrors AiTabViewModel.RefreshUsageSummary's exact text format (see that method's
    /// doc comment) — kept as a separate computation from the same AiChatService counters rather
    /// than sharing AiTabViewModel directly, for the constructor-ordering reason explained in
    /// this class's own doc comment.</summary>
    private void UpdateUsageSummary()
    {
        UsageSummaryTextBlock.Text = _aiChat.TotalCalls == 0
            ? "No calls yet this session."
            : $"{_aiChat.TotalCalls} call{(_aiChat.TotalCalls == 1 ? "" : "s")} — " +
              $"{_aiChat.TotalPromptTokens:N0} prompt + {_aiChat.TotalCompletionTokens:N0} completion = " +
              $"{_aiChat.TotalPromptTokens + _aiChat.TotalCompletionTokens:N0} tokens total";
    }

    /// <summary>Rebuilds the follow-up-suggestion text lines from
    /// OverlayViewModel.FollowUpSuggestions — moved here near-verbatim from OverlayWindow.xaml.cs
    /// (formerly the TP's own PopulateFollowUpSuggestions). Plain, non-interactive TextBlocks
    /// (deliberately NOT buttons — see FollowUpSuggestions' own doc comment for why). Always
    /// renders at least a muted placeholder line when empty — the static "QUESTIONS" header lives
    /// in XAML now (FollowUpSectionPanel), so this always-something behavior matches Pacing/Token
    /// Usage's own "no data yet" placeholders instead of collapsing to nothing.</summary>
    private void PopulateFollowUpSuggestions()
    {
        FollowUpSuggestionsPanel.Children.Clear();

        if (_sharedViewModel.FollowUpSuggestions.Count == 0)
        {
            FollowUpSuggestionsPanel.Children.Add(new TextBlock
            {
                Text       = "No suggestions yet.",
                FontSize   = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 144, 144, 160)),
            });
            return;
        }

        foreach (var suggestion in _sharedViewModel.FollowUpSuggestions)
        {
            FollowUpSuggestionsPanel.Children.Add(new TextBlock
            {
                Text         = $"•  {suggestion}",
                FontSize     = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground   = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 144, 144, 160)),
            });
        }
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
