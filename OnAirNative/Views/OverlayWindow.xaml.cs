using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using OnAirNative.Models;
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

    // Script rendering: one entry per ScriptDocument.Blocks index, so ChapterInfo.BlockIndex
    // can look up the on-screen element to jump to. _paragraphTextBlocks/_headingTextBlocks
    // are the same elements split by kind, so FontSize/FontColor/FontFamily changes can be
    // reapplied in bulk without a full re-render (avoids rebuilding every element on every
    // slider tick while the user is dragging FontSize/Opacity, etc.).
    private readonly List<FrameworkElement> _blockElements       = new();
    private readonly List<TextBlock>        _paragraphTextBlocks = new();
    private readonly List<TextBlock>        _headingTextBlocks   = new();

    // How much bigger a heading's title text renders than the current body FontSize.
    private const double HeadingFontSizeBoost = 6;
    // LineHeight-to-FontSize ratio carried over from the original fixed 22/34 values.
    private const double LineHeightRatio = 34.0 / 22.0;

    public OverlayWindow()
    {
        InitializeComponent();

        ViewModel = new OverlayViewModel(
            App.Config, App.Audio, App.Whisper, App.AiChat);

        // Wire ViewModel events → Win32 calls
        ViewModel.ClickThroughChanged   += OnClickThroughChanged;
        ViewModel.JumpToBlockRequested  += (_, blockIndex) => JumpToBlock(blockIndex);
        // FollowUpSuggestions is an ObservableCollection, not a property — mirrors
        // ScrollTabViewModel.Chapters' own CollectionChanged pattern in ControllerWindow.
        ViewModel.FollowUpSuggestions.CollectionChanged += (_, _) => PopulateFollowUpSuggestions();
        ViewModel.PropertyChanged            += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(OverlayViewModel.CurrentMode):  OnCurrentModeChanged(ViewModel.CurrentMode); break;
                case nameof(OverlayViewModel.ScriptDocument): RenderScriptDocument(ViewModel.ScriptDocument); break;
                case nameof(OverlayViewModel.QaStatus):
                    QaStatusText.Text       = ViewModel.QaStatus;
                    QaStatusText.Visibility = string.IsNullOrEmpty(ViewModel.QaStatus) ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case nameof(OverlayViewModel.LivePreviewText):
                    LivePreviewTextBlock.Text       = string.IsNullOrEmpty(ViewModel.LivePreviewText) ? "" : $"Live preview: {ViewModel.LivePreviewText}";
                    LivePreviewTextBlock.Visibility = string.IsNullOrEmpty(ViewModel.LivePreviewText) ? Visibility.Collapsed : Visibility.Visible;
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
                    var colorBrush = ParseHexColor(ViewModel.FontColor);
                    foreach (var tb in _paragraphTextBlocks) tb.Foreground = colorBrush;
                    break;
                case nameof(OverlayViewModel.FontSize):
                    foreach (var tb in _paragraphTextBlocks)
                    {
                        tb.FontSize   = ViewModel.FontSize;
                        tb.LineHeight = ViewModel.FontSize * LineHeightRatio;
                    }
                    foreach (var tb in _headingTextBlocks) tb.FontSize = ViewModel.FontSize + HeadingFontSizeBoost;
                    break;
                case nameof(OverlayViewModel.FontFamily):
                    var family = new Microsoft.UI.Xaml.Media.FontFamily(ViewModel.FontFamily);
                    foreach (var tb in _paragraphTextBlocks) tb.FontFamily = family;
                    foreach (var tb in _headingTextBlocks)   tb.FontFamily = family;
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
                case nameof(OverlayViewModel.InsightText):
                    InsightTextBlock.Text   = ViewModel.InsightText;
                    InsightFooter.Visibility = string.IsNullOrEmpty(ViewModel.InsightText) ? Visibility.Collapsed : Visibility.Visible;
                    break;
            }
        };

        // Seed initial rendering (persisted config values, may differ from XAML defaults) —
        // ViewModel.ScriptDocument is already correctly parsed by the time the constructor
        // reaches here (its field initializer parses the placeholder text directly).
        RenderScriptDocument(ViewModel.ScriptDocument);

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
            "onAIr");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "overlay-init.log");

        try
        {
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} OnFirstActivated start\n");

            _hwnd = WindowService.GetHwnd(this);
            ViewModel.Hwnd = _hwnd;

            // Title bar is removed below, but the icon still shows in Task Manager,
            // Alt-Tab (if ever unhidden) and Snap Assist thumbnails.
            WindowService.SetWindowIcon(this);

            // Remove title bar — overlay is purely content
            WindowService.RemoveTitleBar(this);
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} title bar removed\n");

            // Restore saved size, but always position at the primary monitor's
            // top-left corner (0,0) rather than the last saved X/Y. A saved position
            // can point at a monitor that's no longer connected or a different
            // multi-monitor arrangement, which made the overlay appear to not show
            // up at all (it was rendering off-screen). Width/Height are still
            // restored since those aren't affected by monitor layout changes.
            var saved = App.Config.Current.OverlayWindow;
            WindowService.SetPosition(this, 0, 0);
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

    // Note: the overlay cannot host a WebView2 — WebView2 does not render inside
    // WS_EX_LAYERED windows, which is what makes the overlay transparent.

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
        QaScrollViewer.Visibility     = mode == OverlayMode.QA     ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Script rendering (blocks: paragraphs + chapter-heading dividers) ─────

    /// <summary>
    /// Rebuilds ScriptBlocksPanel from a freshly-parsed ScriptDocument — called whenever a new
    /// script loads (ViewModel.ScriptDocument changes). One child element per block:
    ///   - ParagraphBlock → a TextBlock with one Run per ScriptRun (Bold/Italic per-run), or a
    ///     single-space placeholder for a blank source line (preserves vertical spacing).
    ///   - HeadingBlock   → a thin accent-colored divider line + the chapter title, rendered
    ///     larger/bolder — this is what naturally shows "you've crossed into a new chapter" as
    ///     it scrolls through view, no separate state-tracking needed.
    /// Also rebuilds _blockElements/_paragraphTextBlocks/_headingTextBlocks so later
    /// FontSize/FontColor/FontFamily changes and chapter jumps (JumpToBlock) can find the
    /// right elements without re-parsing.
    /// </summary>
    private void RenderScriptDocument(ScriptDocument document)
    {
        ScriptBlocksPanel.Children.Clear();
        _blockElements.Clear();
        _paragraphTextBlocks.Clear();
        _headingTextBlocks.Clear();

        var fontFamily = new Microsoft.UI.Xaml.Media.FontFamily(ViewModel.FontFamily);
        var foreground = ParseHexColor(ViewModel.FontColor);

        foreach (var block in document.Blocks)
        {
            FrameworkElement element;

            switch (block)
            {
                case HeadingBlock heading:
                {
                    bool isTopLevel = heading.Level == 1;
                    var divider = new Border
                    {
                        Height     = isTopLevel ? 2 : 1,
                        Margin     = new Thickness(0, 18, 0, 6),
                        Opacity    = isTopLevel ? 0.9 : 0.6,
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 170, 255)),
                    };
                    var title = new TextBlock
                    {
                        Text        = heading.Title,
                        FontSize    = ViewModel.FontSize + HeadingFontSizeBoost,
                        FontFamily  = fontFamily,
                        FontWeight  = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground  = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 190, 255)),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    _headingTextBlocks.Add(title);

                    var stack = new StackPanel { Margin = new Thickness(16, 0, 16, 0) };
                    stack.Children.Add(divider);
                    stack.Children.Add(title);
                    element = stack;
                    break;
                }

                case ParagraphBlock paragraph:
                {
                    var tb = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        FontSize     = ViewModel.FontSize,
                        FontFamily   = fontFamily,
                        Foreground   = foreground,
                        LineHeight   = ViewModel.FontSize * LineHeightRatio,
                        Margin       = new Thickness(16, 0, 16, 0),
                        IsTextSelectionEnabled = false,
                    };

                    if (paragraph.Runs.Count == 0)
                    {
                        // Blank source line — a literal space keeps the line's height (and
                        // hence the original text's vertical rhythm) without visible text.
                        tb.Text = " ";
                    }
                    else
                    {
                        foreach (var run in paragraph.Runs)
                        {
                            var inlineRun = new Run { Text = run.Text };
                            if (run.Bold)   inlineRun.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                            if (run.Italic) inlineRun.FontStyle  = Windows.UI.Text.FontStyle.Italic;
                            tb.Inlines.Add(inlineRun);
                        }
                    }

                    _paragraphTextBlocks.Add(tb);
                    element = tb;
                    break;
                }

                default:
                    continue; // unknown block type — skip defensively, never crash rendering
            }

            ScriptBlocksPanel.Children.Add(element);
            _blockElements.Add(element);
        }
    }

    /// <summary>
    /// Scrolls the TP so the block at <paramref name="blockIndex"/> lands at the top of the
    /// viewport — the View-side half of OverlayViewModel.JumpToBlock (chapter navigation from
    /// the Controller). TransformToVisual against ScriptBlocksPanel (the ScrollViewer's
    /// content) gives the target element's Y position in the SAME coordinate space
    /// ScrollOffset/ScrollToVerticalOffset already operate in, so no separate scroll mechanism
    /// is needed — this reuses the exact one Auto/Voice/manual scroll already use.
    /// </summary>
    private void JumpToBlock(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= _blockElements.Count) return;

        var target    = _blockElements[blockIndex];
        var transform = target.TransformToVisual(ScriptBlocksPanel);
        var point     = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        ViewModel.ScrollOffset = Math.Max(0, point.Y);
    }

    /// <summary>Rebuilds the follow-up-suggestion text lines from
    /// OverlayViewModel.FollowUpSuggestions — plain, non-interactive TextBlocks (deliberately
    /// NOT buttons — see FollowUpSuggestions' own doc comment for why: the TP is frequently
    /// click-through/locked during live use, and these are questions for the PRESENTER to ask
    /// their client aloud, not something to click/activate). A small header line appears only
    /// when there's at least one suggestion to show.</summary>
    private void PopulateFollowUpSuggestions()
    {
        FollowUpSuggestionsPanel.Children.Clear();
        if (ViewModel.FollowUpSuggestions.Count == 0) return;

        FollowUpSuggestionsPanel.Children.Add(new TextBlock
        {
            Text       = "You could ask them:",
            FontSize   = 13,
            FontStyle  = Windows.UI.Text.FontStyle.Italic,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 144, 144, 160)), // matches QaQuestionText's muted tone
        });

        foreach (var suggestion in ViewModel.FollowUpSuggestions)
        {
            FollowUpSuggestionsPanel.Children.Add(new TextBlock
            {
                Text          = $"•  {suggestion}",
                FontSize      = 14,
                TextWrapping  = TextWrapping.Wrap,
                Foreground    = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 144, 144, 160)),
            });
        }
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
        // Position is intentionally not persisted — the overlay always reopens at
        // (0,0) (see OnFirstActivated) so a stale position from a disconnected
        // monitor or different multi-monitor layout can't leave it off-screen.
        // Only the size is remembered.
        var (_, _, w, h) = WindowService.GetGeometry(this);
        var s = App.Config.Current.OverlayWindow;
        s.Width = w; s.Height = h;
    }
}
