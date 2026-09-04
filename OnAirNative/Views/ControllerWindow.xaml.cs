using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OnAirNative.Models;
using OnAirNative.Services;
using OnAirNative.ViewModels;
using OnAirNative.Views.Dialogs;
using OnAirNative.Win32;
using Windows.ApplicationModel.DataTransfer;

namespace OnAirNative.Views;

/// <summary>
/// Controller window — the presenter's control panel.
/// Contains 6 tabs (Script, Q&amp;A, AI Insights, App Stealth, Settings, About) plus the footer
/// overlay/visibility and screen-share protection toggles.
/// </summary>
public sealed partial class ControllerWindow : Window
{
    public ControllerViewModel ViewModel { get; private set; } = null!;
    public OverlayWindow?      Overlay   { get; set; }
    public InsightWindow?      Insights  { get; set; }

    private IntPtr _hwnd;
    // Guard flag: true while PopulateStaticUi is running.
    // Prevents slider ValueChanged handlers from writing to config during initialization.
    private bool _populatingUi;
    // Auto-check for updates only once per session, the first time the About tab is opened.
    private bool _updateCheckedThisSession;

    public ControllerWindow()
    {
        InitializeComponent();

        // Apply the saved theme before the first paint so there's no light→dark flash.
        ApplyTheme(App.Config.Current.Theme);

        Activated += OnFirstActivated;
        Closed    += OnClosed;
    }

    /// <summary>Applies "System" | "Light" | "Dark" to the window's visual tree (and any
    /// ContentDialogs anchored to it, since they share the same XamlRoot).</summary>
    private void ApplyTheme(string theme)
    {
        var elementTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark"  => ElementTheme.Dark,
            _       => ElementTheme.Default,
        };

        if (Content is FrameworkElement root)
            root.RequestedTheme = elementTheme;
    }

    private bool _setupDone;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_setupDone) return;
        _setupDone = true;

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "onAIr");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "controller-init.log");

        try
        {
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} OnFirstActivated start\n");

            _hwnd = WindowService.GetHwnd(this);
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} _hwnd={_hwnd}\n");

            // Taskbar / title bar / Alt-Tab icon — not picked up automatically
            WindowService.SetWindowIcon(this);

            // InitViewModel requires _hwnd to be valid (used by ControllerProtectionChanged handler)
            InitViewModel();
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} InitViewModel done\n");

            // Restore geometry, clamped to primary screen bounds (in logical pixels)
            var s = App.Config.Current.ControllerWindow;
            var screenPhysW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            var screenPhysH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            // Get scale factor for this window
            uint dpi = NativeMethods.GetDpiForWindowPublic(_hwnd);
            double scale = dpi / 96.0;
            double logScreenW = screenPhysW / scale;
            double logScreenH = screenPhysH / scale;
            var clampedX = Math.Max(0, Math.Min(s.X, logScreenW - 200));
            var clampedY = Math.Max(0, Math.Min(s.Y, logScreenH - 200));
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} screen={logScreenW:F0}x{logScreenH:F0} logical, pos=({clampedX},{clampedY})\n");

            WindowService.SetPosition(this, clampedX, clampedY);
            WindowService.SetSize(this, s.Width, s.Height);

            // Set default scroll mode selection (IsChecked in XAML causes bug in WinUI 3 2.1.x)
            ScrollManualRadio.IsChecked = true;

            // TP (overlay window) starts hidden — sync the toggle button
            OverlayToggle.IsChecked = false;
            SyncOverlayToggle(false);

            // TP starts unlocked (move mode = interactive)
            LockToggle.IsChecked = false;
            SyncLockToggle(false);

            // Screen-share protection state
            WindowService.SetContentProtection(_hwnd, App.Config.Current.ControllerProtected);
            ProtectToggle.IsChecked = App.Config.Current.ControllerProtected;

            // TP screen-share protection state (applied by OverlayWindow itself on
            // its own first-activate; here we just sync the footer toggle to match).
            OverlayProtectToggle.IsChecked = App.Config.Current.OverlayProtected;
            SyncOverlayProtectToggle(App.Config.Current.OverlayProtected);

            // AI Insights window starts hidden — sync the toggle button
            InsightsToggle.IsChecked = false;
            SyncInsightsToggle(false);

            // AI Insights window starts unlocked (interactive so it can be positioned/resized)
            InsightsLockToggle.IsChecked = false;
            SyncInsightsLockToggle(false);

            // AI Insights screen-share protection state (applied by InsightWindow itself on
            // its own first-activate; here we just sync the footer toggle to match).
            InsightsProtectToggle.IsChecked = App.Config.Current.InsightWindowProtected;
            SyncInsightsProtectToggle(App.Config.Current.InsightWindowProtected);

            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} OnFirstActivated done\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} EXCEPTION: {ex}\n");
        }
    }

    /// <summary>Called from ControllerWindow.OnFirstActivated (after _hwnd is valid).</summary>
    public void InitViewModel()
    {
        ViewModel = new ControllerViewModel(App.Config, Overlay!.ViewModel, App.AiChat, App.Whisper, App.Update);
        ViewModel.ControllerProtectionChanged += (_, protect) =>
            WindowService.SetContentProtection(_hwnd, protect);
        ViewModel.OverlayProtectionChanged += (_, protect) =>
        {
            if (Overlay is not null)
                WindowService.SetContentProtection(WindowService.GetHwnd(Overlay), protect);
        };
        ViewModel.ThemeChanged += (_, theme) => ApplyTheme(theme);
        ViewModel.RemoteControlEnabledChanged += (_, enabled) =>
        {
            if (enabled) App.Instance.StartRemoteControl();
            else App.Instance.StopRemoteControl();
            UpdateRemoteControlStatusText();
        };
        ViewModel.WebRemoteEnabledChanged += (_, enabled) =>
        {
            if (enabled) App.Instance.StartWebRemote();
            else App.Instance.StopWebRemote();
            UpdateWebRemoteStatusText();
        };

        // Local Whisper model load status (Loading…/Model loaded/File not found/Failed) → UI,
        // plus the Load/Unload button's label — both change together (WhisperModelStatus only
        // ever changes inside LoadWhisperModelAsync/ToggleWhisperModelAsync, exactly the same
        // moments IsLocalModelLoaded can flip), so one hook keeps both in sync.
        ViewModel.AiTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.AiTabViewModel.WhisperModelStatus))
            {
                WhisperModelStatusText.Text = ViewModel.AiTab.WhisperModelStatus;
                WhisperLoadUnloadBtn.Content = App.Whisper.IsLocalModelLoaded ? "Unload" : "Load";
            }
        };

        // Update check/install status → About tab UI
        ViewModel.AboutTab.PropertyChanged += OnAboutTabPropertyChanged;
        // Installer has been launched (elevated, via UAC) — close so it can overwrite our files
        ViewModel.AboutTab.InstallerLaunched += (_, _) => Close();

        ViewModel.ScrollTab.OpacityChanged += (_, opacity) =>
        {
            var b = (byte)(opacity * 255);
            if (Overlay is not null)
                WindowService.SetOpacity(WindowService.GetHwnd(Overlay), b);
        };

        // AI Insights tab — independent appearance for the floating InsightWindow (see
        // InsightsTabViewModel/InsightAppearanceConfig). Mirrors the ScrollTab.OpacityChanged
        // wiring above, just targeting the Insights window instead of the TP.
        ViewModel.InsightsTab.OpacityChanged += (_, opacity) =>
        {
            var b = (byte)(opacity * 255);
            if (Insights is not null)
                WindowService.SetOpacity(WindowService.GetHwnd(Insights), b);
        };
        ViewModel.InsightsTab.FontSizeChanged   += (_, size)   => Insights?.SetFontSize(size);
        ViewModel.InsightsTab.FontFamilyChanged += (_, family) => Insights?.SetFontFamily(family);
        ViewModel.InsightsTab.FontColorChanged  += (_, color)  => Insights?.SetFontColor(color);

        // Keep FileNameText live when a new script is loaded
        ViewModel.ScrollTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScrollTabViewModel.LoadedFileName))
                FileNameText.Text = ViewModel.ScrollTab.LoadedFileName;
        };

        // Rebuild the clickable chapter list whenever a newly loaded script's headings change
        // (ScrollTabViewModel.Chapters is an ObservableCollection, not a property — see its
        // doc comment — so we react to CollectionChanged rather than PropertyChanged here).
        ViewModel.ScrollTab.Chapters.CollectionChanged += (_, _) => PopulateChapters();

        // Same pattern for the Settings → KNOWLEDGE BASE file list.
        ViewModel.AiTab.KnowledgeBaseFiles.CollectionChanged += (_, _) => PopulateKnowledgeBaseFiles();

        // Sync LockToggle when move mode changes (e.g., via Ctrl+Alt+Home hotkey).
        // Setting IsChecked (not just calling SyncLockToggle) keeps the button's actual
        // toggle state in sync with its label — otherwise a hotkey-driven change would
        // update the text but leave IsChecked stale, so the next manual click would
        // silently re-affirm the current state instead of flipping it as the label implies.
        Overlay.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OnAirNative.ViewModels.OverlayViewModel.IsMoveModeActive))
                LockToggle.IsChecked = !Overlay.ViewModel.IsMoveModeActive;
            else if (e.PropertyName == nameof(OnAirNative.ViewModels.OverlayViewModel.ConversationTurnCount))
                UpdateConversationTurnCountText();
            else if (e.PropertyName == nameof(OnAirNative.ViewModels.OverlayViewModel.QaSessionStatusText))
                UpdateQaSessionStatusText();
            // Block 6: push an immediate state broadcast (rather than waiting up to 2s for the
            // safety-net timer) the instant any of these change — a monitoring MCP agent polling
            // QaTurnCount, or a live WS observer waiting on the insight footer or the Stream
            // Deck's pacing-status tile, should see the new value with as little latency as the
            // WS protocol allows.
            else if (e.PropertyName == nameof(OnAirNative.ViewModels.OverlayViewModel.QaTurnCount) ||
                     e.PropertyName == nameof(OnAirNative.ViewModels.OverlayViewModel.InsightText) ||
                     e.PropertyName == nameof(OnAirNative.ViewModels.OverlayViewModel.PacingLevel))
                App.RemoteControl?.NotifyStateMayHaveChanged();
        };

        PopulateStaticUi();
    }

    private void PopulateStaticUi()
    {
        _populatingUi = true;
        try
        {
            // Populate AI provider combos
            ChatProviderCombo.ItemsSource       = AiTabViewModel.ChatProviders;
            TranscriptionCombo.ItemsSource      = AiTabViewModel.TranscriptionProviders;
            ChatProviderCombo.SelectedIndex     = ViewModel.AiTab.SelectedChatProviderIndex;
            TranscriptionCombo.SelectedIndex    = ViewModel.AiTab.SelectedTranscriptionProviderIndex;
            UseLocalWhisperCheckBox.IsChecked   = ViewModel.AiTab.UseLocalWhisper;

            SystemPromptBox.Text        = ViewModel.AiTab.SystemPrompt;
            PresentationContextBox.Text = ViewModel.AiTab.PresentationContext;
            GlossaryBox.Text            = ViewModel.AiTab.Glossary;
            WhisperModelBox.Text        = ViewModel.AiTab.WhisperModelPath;
            WhisperModelStatusText.Text = ViewModel.AiTab.WhisperModelStatus;
            WhisperLoadUnloadBtn.Content = App.Whisper.IsLocalModelLoaded ? "Unload" : "Load";
            PopulateKnowledgeBaseFiles();

            MaxTokensSlider.Minimum = 50;
            MaxTokensSlider.Maximum = 2000;
            MaxTokensSlider.Value   = ViewModel.AiTab.MaxTokens;
            ShowFollowUpSuggestionsCheckBox.IsChecked = ViewModel.AiTab.ShowFollowUpSuggestions;
            UpdateConversationTurnCountText();
            UpdateQaSessionStatusText();

            // Scroll tab — set ranges THEN values.
            // Must be inside _populatingUi guard: setting Minimum causes WinUI 3 to clamp
            // the current value (0 → min), firing ValueChanged which would overwrite the config.
            ScrollStepSlider.Minimum       = 20;
            ScrollStepSlider.Maximum       = 400;
            ScrollSpeedSlider.Minimum      = 1;
            ScrollSpeedSlider.Maximum      = 100;
            VoiceScrollSpeedSlider.Minimum = 1;
            VoiceScrollSpeedSlider.Maximum = 100;
            FontSizeSlider.Minimum         = 10;
            FontSizeSlider.Maximum         = 64;
            OpacitySlider.Minimum          = 10;
            OpacitySlider.Maximum          = 100;

            ScrollStepSlider.Value       = ViewModel.ScrollTab.ScrollStep;
            ScrollSpeedSlider.Value      = ViewModel.ScrollTab.ScrollSpeed;
            VoiceScrollSpeedSlider.Value = ViewModel.ScrollTab.VoiceScrollSpeed;
            FontSizeSlider.Value         = ViewModel.ScrollTab.FontSize;
            OpacitySlider.Value          = ViewModel.ScrollTab.Opacity * 100;

            PopulateFontFamilies();

            FileNameText.Text = ViewModel.ScrollTab.LoadedFileName;
            PopulateChapters();
            CustomColorBox.Text = App.Config.Current.Appearance.FontColor;

            // AI Insights tab — appearance controls for the floating InsightWindow, independent
            // from the TP's own font/opacity sliders above (see InsightAppearanceConfig).
            InsightFontSizeSlider.Minimum = 10;
            InsightFontSizeSlider.Maximum = 64;
            InsightFontSizeSlider.Value   = ViewModel.InsightsTab.FontSize;
            InsightOpacitySlider.Minimum  = 10;
            InsightOpacitySlider.Maximum  = 100;
            InsightOpacitySlider.Value    = ViewModel.InsightsTab.Opacity * 100;
            PopulateInsightFontFamilies();
            InsightCustomColorBox.Text = App.Config.Current.InsightAppearance.FontColor;

            // AI Insights tab — pure display toggles for the floating InsightWindow's footer
            // (Pacing/Token Usage) and top section (Follow-up Suggestions). Values are always
            // computed regardless — these only control whether the InsightWindow shows them.
            ShowPacingCheckBox.IsChecked      = Overlay?.ViewModel.ShowPacingInInsights ?? true;
            ShowTokenUsageCheckBox.IsChecked  = Overlay?.ViewModel.ShowTokenUsageInInsights ?? true;
            ShowFollowUpsInInsightsCheckBox.IsChecked = Overlay?.ViewModel.ShowFollowUpsInInsights ?? true;
            ShowExternalInsightsCheckBox.IsChecked    = Overlay?.ViewModel.ShowExternalInsightsInInsights ?? true;

            // About
            VersionText.Text = $"v{ViewModel.AboutTab.Version}";
            AuthorNameText.Text = ViewModel.AboutTab.AuthorName;
            AuthorCreditText.Text = $"· {ViewModel.AboutTab.AuthorCredit}";
            LinkedInLink.NavigateUri = new Uri(ViewModel.AboutTab.LinkedInUrl);
            UpdateStatusTextBlock.Text  = "";
            UpdateDownloadPanel.Visibility = Visibility.Collapsed;
            UpdateProgressBar.Visibility   = Visibility.Collapsed;

            // Select first tab — TabScript starts unchecked (no IsChecked="True" in XAML
            // anymore, deliberately) so this false→true transition reliably fires
            // MainTab_Checked here, at a safe point where ViewModel/Overlay are ready —
            // exactly mirroring what NavView.SelectedItem = NavView.MenuItems[0] used to do.
            TabScript.IsChecked = true;

            // Settings tab — audio devices + voice threshold
            PopulateAudioDevices();
            PopulateProviderStatuses();
            AudioSourceCombo.SelectedIndex = App.Config.Current.AudioRecordingSource switch
            {
                "system" => 1,
                "both"   => 2,
                _        => 0,
            };
            VoiceThresholdSlider.Minimum = 1;
            VoiceThresholdSlider.Maximum = 50;
            VoiceThresholdSlider.Value = App.Config.Current.Appearance.VoiceRmsThreshold;
            VoiceThresholdValue.Text   = $"Current: {App.Config.Current.Appearance.VoiceRmsThreshold:F1}";

            // Settings tab — theme
            ThemeSystemRadio.IsChecked = App.Config.Current.Theme == "System";
            ThemeLightRadio.IsChecked  = App.Config.Current.Theme == "Light";
            ThemeDarkRadio.IsChecked   = App.Config.Current.Theme == "Dark";

            // Settings tab — Stream Deck remote control
            RemoteControlToggle.IsOn = ViewModel.RemoteControlEnabled;
            UpdateRemoteControlStatusText();

            // Settings tab — Web Remote
            WebRemoteToggle.IsOn = ViewModel.WebRemoteEnabled;
            UpdateWebRemoteStatusText();
        }
        finally
        {
            _populatingUi = false;

            // Wire Q&A status from overlay → Controller (must be AFTER _populatingUi=false
            // so UI reads don't get blocked, and only fire on real user events)
            Overlay!.ViewModel.PropertyChanged += OnOverlayQaPropertyChanged;
        }
    }

    private void OnOverlayQaPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModels.OverlayViewModel.MicLevel):
                    MicLevelBar.Value  = Overlay?.ViewModel.MicLevel ?? 0;
                    MicLevelText.Text  = $"{Overlay?.ViewModel.MicLevel:F1}";
                    break;
                case nameof(ViewModels.OverlayViewModel.QaStatus):
                ControllerQaStatus.Text = Overlay?.ViewModel.QaStatus ?? "";
                break;
            case nameof(ViewModels.OverlayViewModel.IsRecording):
                var recording = Overlay?.ViewModel.IsRecording ?? false;
                ControllerRecordBtn.Content    = recording ? "■ Stop" : "● Record";
                ControllerRecordBtn.Background = recording
                    ? new SolidColorBrush(Microsoft.UI.Colors.Crimson)
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 74, 144, 217));
                break;
        }
    }

    // ── About tab — update check/install status ──────────────────────────────

    private void OnAboutTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AboutTabViewModel.UpdateStatusText):
                UpdateStatusTextBlock.Text = ViewModel.AboutTab.UpdateStatusText;
                break;
            case nameof(AboutTabViewModel.IsUpdateAvailable):
                UpdateDownloadPanel.Visibility = ViewModel.AboutTab.IsUpdateAvailable
                    ? Visibility.Visible : Visibility.Collapsed;
                DownloadUpdateBtn.Content = $"⬇ Download & Install v{ViewModel.AboutTab.LatestVersion}";
                break;
            case nameof(AboutTabViewModel.IsCheckingForUpdate):
                CheckUpdatesBtn.IsEnabled = !ViewModel.AboutTab.IsCheckingForUpdate;
                break;
            case nameof(AboutTabViewModel.IsDownloadingUpdate):
                UpdateProgressBar.Visibility = ViewModel.AboutTab.IsDownloadingUpdate
                    ? Visibility.Visible : Visibility.Collapsed;
                DownloadUpdateBtn.IsEnabled = !ViewModel.AboutTab.IsDownloadingUpdate;
                CheckUpdatesBtn.IsEnabled   = !ViewModel.AboutTab.IsDownloadingUpdate;
                break;
            case nameof(AboutTabViewModel.DownloadProgress):
                UpdateProgressBar.Value = ViewModel.AboutTab.DownloadProgress;
                break;
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.AboutTab.CheckForUpdatesCommand.ExecuteAsync(null);

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot          = Content.XamlRoot,
            Title             = "Update onAIr?",
            Content           = $"A new version (v{ViewModel.AboutTab.LatestVersion}) is available.\n\n" +
                                 "onAIr will close so the installer can run. " +
                                 "Windows may ask for administrator permission.",
            PrimaryButtonText = "Update now",
            CloseButtonText   = "Not now",
            DefaultButton     = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.AboutTab.DownloadAndInstallCommand.ExecuteAsync(null);
    }

    // ── Pill tab bar switching ────────────────────────────────────────────────

    /// <summary>Tag/RadioButton/label triples for the 5 main tabs — single source of truth for
    /// <see cref="UpdateTabPillLayout"/> so adding/removing a tab only means editing this array.</summary>
    private (string Tag, RadioButton Button, TextBlock Label)[] MainTabs => new[]
    {
        ("script",   TabScript,   TabScriptText),
        ("qa",       TabQa,       TabQaText),
        ("insights", TabInsights, TabInsightsText),
        ("stealth",  TabStealth,  TabStealthText),
        ("settings", TabSettings, TabSettingsText),
        ("about",    TabAbout,    TabAboutText),
    };

    /// <summary>Selected segment shows icon+text; the other 4 collapse to icon-only. Columns
    /// are all Auto in XAML (see TabPillGrid) so simply toggling the label's Visibility is
    /// enough — WinUI re-measures Auto columns automatically on any content change, no manual
    /// column-width bookkeeping needed. Paired with the pill Grid's
    /// <c>RepositionThemeTransition</c> in XAML so the resulting reflow slides smoothly.</summary>
    private void UpdateTabPillLayout(string? selectedTag)
    {
        foreach (var (tag, _, label) in MainTabs)
            label.Visibility = tag == selectedTag ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MainTab_Checked(object sender, RoutedEventArgs e)
    {
        var tag = (sender as RadioButton)?.Tag?.ToString();
        UpdateTabPillLayout(tag);

        ScrollPanel.Visibility   = tag == "script"   ? Visibility.Visible : Visibility.Collapsed;
        AiPanel.Visibility       = tag == "qa"       ? Visibility.Visible : Visibility.Collapsed;
        InsightsPanel.Visibility = tag == "insights" ? Visibility.Visible : Visibility.Collapsed;
        StealthPanel.Visibility  = tag == "stealth"  ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility    = tag == "about"    ? Visibility.Visible : Visibility.Collapsed;

        // Auto-refresh window list when App Stealth tab is opened
        if (tag == "stealth" && WindowListCombo.Items.Count == 0)
            PopulateWindowList();

        // Auto-refresh device list when Settings tab is opened
        if (tag == "settings" && WindowListCombo.Items.Count == 0)
            PopulateAudioDevices();

        // Stop the mic level test if navigating away from Settings — avoid an orphaned
        // capture running while the user is on some other tab.
        if (tag != "settings") StopMicTest();

        // Auto-check for updates the first time the About tab is opened this session
        if (tag == "about" && !_updateCheckedThisSession)
        {
            _updateCheckedThisSession = true;
            _ = ViewModel.AboutTab.CheckForUpdatesCommand.ExecuteAsync(null);
        }

        // Sync overlay mode to match selected Controller tab
        if (Overlay is not null && tag is "script" or "qa")
        {
            Overlay.ViewModel.CurrentMode = tag == "qa"
                ? ViewModels.OverlayMode.QA
                : ViewModels.OverlayMode.Script;
        }
    }

    // ── Scroll tab handlers ───────────────────────────────────────────────────

    private void LoadFile_Click(object sender, RoutedEventArgs e) =>
        _ = ViewModel.ScrollTab.OpenFileCommand.ExecuteAsync(this);

    /// <summary>Rebuilds the clickable CHAPTERS card from ScrollTabViewModel.Chapters — plain
    /// Buttons built in code-behind (same "manually populate a StackPanel" pattern as e.g.
    /// McpToolsDialog's tools checklist) rather than an ItemsControl/data template, matching
    /// how the rest of this codebase builds small dynamic lists. Hides the whole card when the
    /// loaded script has no headings, so a plain script doesn't show an empty panel.</summary>
    private void PopulateChapters()
    {
        ChaptersListPanel.Children.Clear();
        var chapters = ViewModel.ScrollTab.Chapters;
        ChaptersCard.Visibility = chapters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var chapter in chapters)
        {
            var button = new Button
            {
                Content    = chapter.Title,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                // Indent Level-2 (##) chapters under Level-1 (#) ones
                Padding    = new Thickness(chapter.Level == 1 ? 10 : 24, 6, 10, 6),
                FontWeight = chapter.Level == 1
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
            };
            button.Click += (_, _) => ViewModel.ScrollTab.JumpToChapterCommand.Execute(chapter);
            ChaptersListPanel.Children.Add(button);
        }
    }

    /// <summary>Rebuilds the Settings → KNOWLEDGE BASE file list from AiTabViewModel.
    /// KnowledgeBaseFiles — same "manually populate a StackPanel in code-behind" pattern as
    /// PopulateChapters above. Unlike CHAPTERS, this card never collapses entirely when empty:
    /// it's a persistent setup card (holds the "+ Add file(s)…" button, the only way to attach
    /// files), not a contextual display that only makes sense once content exists — so an empty
    /// state shows an explanatory message instead of hiding the card.</summary>
    private void PopulateKnowledgeBaseFiles()
    {
        KnowledgeBaseFilesPanel.Children.Clear();
        var files = ViewModel.AiTab.KnowledgeBaseFiles;
        KnowledgeBaseEmptyText.Visibility = files.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var path in files)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = Path.GetFileName(path),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTipService.SetToolTip(nameText, path);
            Grid.SetColumn(nameText, 0);

            var removeBtn = new Button
            {
                Content = "✕",
                FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4),
                Margin  = new Thickness(8, 0, 0, 0),
            };
            removeBtn.Click += (_, _) => ViewModel.AiTab.RemoveKnowledgeBaseFileCommand.Execute(path);
            Grid.SetColumn(removeBtn, 1);

            row.Children.Add(nameText);
            row.Children.Add(removeBtn);
            KnowledgeBaseFilesPanel.Children.Add(row);
        }
    }

    private void AddKnowledgeBaseFile_Click(object sender, RoutedEventArgs e)
    {
        // Win32FileDialog instead of Windows.Storage.Pickers.FileOpenPicker — see its own doc
        // comment (the WinRT picker hangs forever, no dialog ever appearing, in this unpackaged
        // app under some session types).
        var paths = Win32.Win32FileDialog.PickMultipleFiles(_hwnd, "Reference documents", "txt", "md");
        foreach (var path in paths)
            ViewModel.AiTab.AddKnowledgeBaseFileCommand.Execute(path);
    }

    private void ScrollUp_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ScrollTab.ScrollUpCommand.Execute(null);

    private void ScrollDown_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ScrollTab.ScrollDownCommand.Execute(null);

    private void ScrollModeChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingUi) return;
        if (sender is not RadioButton rb) return;
        var tag = rb.Tag?.ToString();

        // Exactly one mode-specific control is visible at a time — Manual's fixed
        // step-per-press doesn't apply to Auto's continuous speed, and Voice now has
        // its own independent speed (see VoiceScrollSpeed), so showing all three at
        // once was confusing and implied they were all simultaneously in effect.
        StepLabel.Visibility             = tag == "Manual" ? Visibility.Visible : Visibility.Collapsed;
        ScrollStepSlider.Visibility      = tag == "Manual" ? Visibility.Visible : Visibility.Collapsed;
        SpeedLabel.Visibility            = tag == "Auto"   ? Visibility.Visible : Visibility.Collapsed;
        ScrollSpeedSlider.Visibility     = tag == "Auto"   ? Visibility.Visible : Visibility.Collapsed;
        VoiceSpeedLabel.Visibility       = tag == "Voice"  ? Visibility.Visible : Visibility.Collapsed;
        VoiceScrollSpeedSlider.Visibility = tag == "Voice" ? Visibility.Visible : Visibility.Collapsed;
        VoiceLevelPanel.Visibility       = tag == "Voice"  ? Visibility.Visible : Visibility.Collapsed;

        if (ViewModel is null) return;
        ViewModel.ScrollTab.SelectedScrollModeIndex = tag switch
        {
            "Auto"  => 1,
            "Voice" => 2,
            _       => 0,
        };
    }

    /// <summary>Shared Checked handler for the Settings tab's System/Light/Dark radio group.</summary>
    private void ThemeRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingUi) return;
        if (sender is not RadioButton rb) return;
        ViewModel.Theme = rb.Tag?.ToString() ?? "System";
    }

    // ── Settings tab — Stream Deck remote control ─────────────────────────────

    private void RemoteControlToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.RemoteControlEnabled = RemoteControlToggle.IsOn;
    }

    /// <summary>Reflects the live server status — checks App.RemoteControl directly rather than
    /// just the config flag, since a bind failure (port already in use) means the toggle can be
    /// "on" in config while the server itself isn't actually running. Public because LaunchCore
    /// starts/skips the server AFTER this window's InitViewModel/PopulateStaticUi already ran
    /// once (too early to know the real outcome) and needs to trigger a refresh afterward.</summary>
    public void RefreshRemoteControlStatusText() => UpdateRemoteControlStatusText();

    private void UpdateRemoteControlStatusText()
    {
        RemoteControlStatusText.Text = App.RemoteControl is not null
            ? $"● Running on port {RemoteControlService.Port}"
            : (ViewModel.RemoteControlEnabled ? "⚠ Enabled but not running (port busy?)" : "○ Stopped");
    }

    // ── Settings tab — Web Remote ──────────────────────────────────────────────

    private void WebRemoteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.WebRemoteEnabled = WebRemoteToggle.IsOn;
    }

    /// <summary>Public wrapper for the same reason as <see cref="RefreshRemoteControlStatusText"/>
    /// — LaunchCore decides whether the server actually started AFTER this window's
    /// InitViewModel/PopulateStaticUi already ran once.</summary>
    public void RefreshWebRemoteStatusText() => UpdateWebRemoteStatusText();

    private void UpdateWebRemoteStatusText()
    {
        WebRemoteStatusText.Text = App.WebRemote is not null
            ? $"● Running on port {WebRemoteService.Port}"
            : App.WebRemoteNeedsElevation
                ? "⚠ Needs network access — click \"Grant Network Access\" below"
                : (ViewModel.WebRemoteEnabled ? "⚠ Enabled but not running" : "○ Stopped");

        WebRemotePinText.Text = string.IsNullOrEmpty(App.Config.Current.WebRemote.Pin)
            ? "——————"
            : App.Config.Current.WebRemote.Pin;

        GrantWebRemoteAccessBtn.Visibility = App.WebRemoteNeedsElevation
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Runs the elevated one-time URL-ACL grant (shows a UAC prompt) off the UI thread,
    /// then retries starting the server and refreshes the status text either way.</summary>
    private async void GrantWebRemoteAccess_Click(object sender, RoutedEventArgs e)
    {
        WebRemoteCopyStatusText.Text = "Requesting network access… (check for a UAC prompt)";
        var granted = await Task.Run(() => App.Instance.TryGrantWebRemoteAccessAndStart());
        UpdateWebRemoteStatusText();
        WebRemoteCopyStatusText.Text = granted
            ? "✓ Network access granted — Web Remote is now running."
            : "⚠ Access not granted (UAC declined, or the request failed).";
    }

    /// <summary>Generates a new PIN and persists it — every previously connected device must
    /// re-enter the new one, since the server checks it live on every connection attempt.</summary>
    private void RegenerateWebRemotePin_Click(object sender, RoutedEventArgs e)
    {
        App.Config.Current.WebRemote.Pin = WebRemoteService.GeneratePin();
        App.Config.Save();
        UpdateWebRemoteStatusText();
        WebRemoteCopyStatusText.Text = "✓ New PIN generated — previously connected devices must re-enter it.";
    }

    /// <summary>Copies a ready-to-open URL (LAN IP + PIN) to the clipboard — pasting it into a
    /// message, or opening it directly on another device, connects instantly (app.js reads the
    /// `?pin=` query param and strips it from the address bar on first load).</summary>
    private void CopyWebRemoteLink_Click(object sender, RoutedEventArgs e)
    {
        var addresses = WebRemoteService.GetLanAddresses();
        if (addresses.Count == 0)
        {
            WebRemoteCopyStatusText.Text = "⚠ No LAN address found — make sure Wi-Fi/Ethernet is connected.";
            return;
        }

        var pin = App.Config.Current.WebRemote.Pin;
        var url = $"http://{addresses[0]}:{WebRemoteService.Port}/?pin={pin}";

        var package = new DataPackage();
        package.SetText(url);
        Clipboard.SetContent(package);

        WebRemoteCopyStatusText.Text = $"✓ Copied {url} to clipboard";
    }

    /// <summary>Installs the bundled onAIr Remote Stream Deck plugin by launching the packaged
    /// .streamDeckPlugin file — identical to a user double-clicking it in Explorer, which hands
    /// off to the Stream Deck app's own install flow (registered as that file extension's
    /// handler). Requires the Stream Deck app to be installed.</summary>
    private void InstallStreamDeckPlugin_Click(object sender, RoutedEventArgs e)
    {
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "Assets", "onair-remote.streamDeckPlugin");
        if (!File.Exists(pluginPath))
        {
            InstallPluginStatusText.Text = "⚠ Plugin file not found in this install.";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pluginPath) { UseShellExecute = true });
            InstallPluginStatusText.Text = "Handed off to Stream Deck — follow its install prompt.";
        }
        catch (Exception ex)
        {
            InstallPluginStatusText.Text = $"⚠ Couldn't launch installer: {ex.Message} (is Stream Deck installed?)";
        }
    }

    /// <summary>Opens the MCP Tools &amp; Setup dialog — usage instructions, a ready-to-copy
    /// client config snippet, and a per-tool enable/disable checklist.</summary>
    private async void McpToolsSetup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new McpToolsDialog(App.Config)
        {
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            dialog.SaveToolsState();
    }

    private void ScrollStepSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.ScrollTab.ScrollStep = (int)e.NewValue;
    }

    private void ScrollSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.ScrollTab.ScrollSpeed = (int)e.NewValue;
    }

    private void VoiceScrollSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.ScrollTab.VoiceScrollSpeed = (int)e.NewValue;
    }

    /// <summary>
    /// Nudges Auto-scroll speed by <see cref="ScrollSpeedStep"/> points, called from the
    /// Ctrl+Alt+./, global hotkeys. <paramref name="direction"/> is +1 to increase, -1 to decrease.
    /// </summary>
    public void AdjustScrollSpeed(int direction)
    {
        var newSpeed = (int)Math.Clamp(
            ViewModel.ScrollTab.ScrollSpeed + direction * ScrollSpeedStep,
            ScrollSpeedSlider.Minimum, ScrollSpeedSlider.Maximum);

        _populatingUi = true;
        try
        {
            ViewModel.ScrollTab.ScrollSpeed = newSpeed;
            ScrollSpeedSlider.Value = newSpeed;
        }
        finally { _populatingUi = false; }
    }

    private const int ScrollSpeedStep = 5;

    private void FontSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.ScrollTab.FontSize = (int)e.NewValue;
    }

    /// <summary>
    /// Nudges script font size by <see cref="FontSizeStep"/> points, called from the
    /// Ctrl+Alt+=/- global hotkeys. <paramref name="direction"/> is +1 to increase, -1 to decrease.
    /// </summary>
    public void AdjustFontSize(int direction)
    {
        var newSize = (int)Math.Clamp(
            ViewModel.ScrollTab.FontSize + direction * FontSizeStep,
            FontSizeSlider.Minimum, FontSizeSlider.Maximum);

        _populatingUi = true;
        try
        {
            ViewModel.ScrollTab.FontSize = newSize;
            FontSizeSlider.Value = newSize;
        }
        finally { _populatingUi = false; }
    }

    private const int FontSizeStep = 2;

    private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.ScrollTab.Opacity = e.NewValue / 100.0;
    }

    /// <summary>
    /// Nudges overlay opacity by <see cref="OpacityStepPercent"/> points, called from the
    /// Ctrl+Alt+]/[ global hotkeys. <paramref name="direction"/> is +1 to increase, -1 to decrease.
    /// </summary>
    public void AdjustOpacity(int direction)
    {
        var newOpacityPercent = Math.Clamp(
            ViewModel.ScrollTab.Opacity * 100 + direction * OpacityStepPercent,
            OpacitySlider.Minimum, OpacitySlider.Maximum);

        _populatingUi = true;
        try
        {
            ViewModel.ScrollTab.Opacity = newOpacityPercent / 100.0; // saves config + updates overlay via OpacityChanged
            OpacitySlider.Value = newOpacityPercent;                 // keep the visible slider in sync
        }
        finally { _populatingUi = false; }
    }

    private const double OpacityStepPercent = 5;

    /// <summary>
    /// Nudges Voice mode's scroll speed by <see cref="VoiceScrollSpeedStep"/> points, called
    /// from the Ctrl+Alt+Up/Down global hotkeys. <paramref name="direction"/> is +1 to increase, -1 to decrease.
    /// </summary>
    public void AdjustVoiceScrollSpeed(int direction)
    {
        var newSpeed = (int)Math.Clamp(
            ViewModel.ScrollTab.VoiceScrollSpeed + direction * VoiceScrollSpeedStep,
            VoiceScrollSpeedSlider.Minimum, VoiceScrollSpeedSlider.Maximum);

        _populatingUi = true;
        try
        {
            ViewModel.ScrollTab.VoiceScrollSpeed = newSpeed;
            VoiceScrollSpeedSlider.Value = newSpeed;
        }
        finally { _populatingUi = false; }
    }

    private const int VoiceScrollSpeedStep = 5;

    /// <summary>
    /// Nudges Manual mode's scroll step (px) by <see cref="ScrollStepStep"/>, called from the
    /// Ctrl+Alt+Right/Left global hotkeys. <paramref name="direction"/> is +1 to increase, -1 to decrease.
    /// </summary>
    public void AdjustScrollStep(int direction)
    {
        var newStep = (int)Math.Clamp(
            ViewModel.ScrollTab.ScrollStep + direction * ScrollStepStep,
            ScrollStepSlider.Minimum, ScrollStepSlider.Maximum);

        _populatingUi = true;
        try
        {
            ViewModel.ScrollTab.ScrollStep = newStep;
            ScrollStepSlider.Value = newStep;
        }
        finally { _populatingUi = false; }
    }

    private const int ScrollStepStep = 20;

    /// <summary>
    /// Nudges Voice mode's scroll sensitivity threshold by <see cref="VoiceThresholdStep"/>, called
    /// from the Ctrl+Alt+'/; global hotkeys. <paramref name="direction"/> is +1 to increase, -1 to decrease.
    /// Writes straight to config (mirrors <see cref="VoiceThresholdSlider_ValueChanged"/>) since this
    /// setting isn't backed by a ViewModel property.
    /// </summary>
    public void AdjustVoiceThreshold(int direction)
    {
        var newThreshold = Math.Clamp(
            App.Config.Current.Appearance.VoiceRmsThreshold + direction * VoiceThresholdStep,
            VoiceThresholdSlider.Minimum, VoiceThresholdSlider.Maximum);

        _populatingUi = true;
        try
        {
            App.Config.Current.Appearance.VoiceRmsThreshold = newThreshold;
            VoiceThresholdValue.Text = $"Current: {newThreshold:F1}";
            VoiceThresholdSlider.Value = newThreshold;
            App.Config.Save();
        }
        finally { _populatingUi = false; }
    }

    private const double VoiceThresholdStep = 2.0;

    /// <summary>
    /// Builds a snapshot of remotely-interesting state — consumed by <see cref="RemoteControlService"/>
    /// to push to the Stream Deck plugin (button state feedback + dial value displays).
    /// </summary>
    public RemoteState GetRemoteState() => new()
    {
        TpOpen                  = OverlayToggle?.IsChecked ?? false,
        TpLocked                = LockToggle?.IsChecked ?? false,
        TpHiddenInShare         = OverlayProtectToggle?.IsChecked ?? false,
        ControllerHiddenInShare = ProtectToggle?.IsChecked ?? false,
        Recording               = Overlay?.ViewModel.IsRecording ?? false,
        ChatProvider            = AiTabViewModel.ChatProviders.ElementAtOrDefault(ViewModel.AiTab.SelectedChatProviderIndex) ?? "",
        WhisperLocalLoaded      = App.Whisper.IsLocalModelLoaded,
        WhisperModelStatus      = ViewModel.AiTab.WhisperModelStatus,
        Opacity                 = ViewModel.ScrollTab.Opacity * 100.0,
        FontSize                = ViewModel.ScrollTab.FontSize,
        ScrollSpeed             = ViewModel.ScrollTab.ScrollSpeed,
        VoiceScrollSpeed        = ViewModel.ScrollTab.VoiceScrollSpeed,
        ScrollStep              = ViewModel.ScrollTab.ScrollStep,
        VoiceThreshold          = App.Config.Current.Appearance.VoiceRmsThreshold,
        ScrollMode              = (Overlay?.ViewModel.ScrollMode ?? ViewModels.ScrollMode.Manual).ToString(),
        FontFamily              = ViewModel.ScrollTab.FontFamily,
        LoadedScriptName        = ViewModel.ScrollTab.LoadedFileName,
        LastQuestion            = Overlay?.ViewModel.QaQuestion ?? "",
        LastAnswer              = Overlay?.ViewModel.QaAnswer ?? "",
        QaTurnCount             = Overlay?.ViewModel.QaTurnCount ?? 0,
        PacingSummary           = Overlay?.ViewModel.PacingSummary ?? "",
        PacingLevel             = Overlay?.ViewModel.PacingLevel ?? "None",
        FollowUpSuggestions     = Overlay?.ViewModel.FollowUpSuggestions.ToList() ?? [],
        QaSessionActive         = Overlay?.ViewModel.IsQaSessionActive ?? false,
        InsightText             = Overlay?.ViewModel.InsightText ?? "",
        InsightsOpen            = InsightsToggle?.IsChecked ?? false,
        InsightsLocked          = InsightsLockToggle?.IsChecked ?? false,
        InsightsHiddenInShare   = InsightsProtectToggle?.IsChecked ?? false,
        InsightFontSize         = ViewModel.InsightsTab.FontSize,
        InsightOpacity          = ViewModel.InsightsTab.Opacity * 100.0,
        InsightFontFamily       = ViewModel.InsightsTab.FontFamily,
        ShowFollowUpSuggestions = ViewModel.AiTab.ShowFollowUpSuggestions,
        ConversationTurnCount   = Overlay?.ViewModel.ConversationTurnCount ?? 0,
        ConversationHistory     = Overlay?.ViewModel.ConversationTurns
                                      .Select(t => new ConversationTurnState { Question = t.UserText, Answer = t.AssistantText })
                                      .ToList() ?? [],
        UsageSummary            = ViewModel.AiTab.UsageSummary,
        StealthEmbedded         = _embedService.IsEmbedding,
        StealthEmbedTitle       = _embedService.TargetTitle,
        ShowPacingInInsights     = Overlay?.ViewModel.ShowPacingInInsights ?? true,
        ShowTokenUsageInInsights = Overlay?.ViewModel.ShowTokenUsageInInsights ?? true,
        ShowFollowUpsInInsights        = Overlay?.ViewModel.ShowFollowUpsInInsights ?? true,
        ShowExternalInsightsInInsights = Overlay?.ViewModel.ShowExternalInsightsInInsights ?? true,
    };

    /// <summary>Applies an absolute setter value by field name — the MCP server's write path.
    /// Each case mirrors the SAME validation/clamp the corresponding UI control already applies
    /// (reads the live slider Minimum/Maximum, the same hex regex, the same installed-fonts
    /// check) so a value accepted here is guaranteed consistent with what the Appearance/
    /// Settings UI would have allowed. Returns (false, reason) instead of throwing so
    /// <see cref="RemoteControlService"/> can report a clear error back to the MCP caller.</summary>
    public (bool Success, string? Error) SetRemoteField(string field, System.Text.Json.JsonElement value)
    {
        try
        {
            switch (field)
            {
                case "FontSize":
                {
                    var v = (int)Math.Clamp(value.GetInt32(), (int)FontSizeSlider.Minimum, (int)FontSizeSlider.Maximum);
                    _populatingUi = true;
                    try { ViewModel.ScrollTab.FontSize = v; FontSizeSlider.Value = v; }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "Opacity":
                {
                    var pct = Math.Clamp(value.GetDouble(), OpacitySlider.Minimum, OpacitySlider.Maximum);
                    _populatingUi = true;
                    try { ViewModel.ScrollTab.Opacity = pct / 100.0; OpacitySlider.Value = pct; }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "ScrollSpeed":
                {
                    var v = (int)Math.Clamp(value.GetInt32(), (int)ScrollSpeedSlider.Minimum, (int)ScrollSpeedSlider.Maximum);
                    _populatingUi = true;
                    try { ViewModel.ScrollTab.ScrollSpeed = v; ScrollSpeedSlider.Value = v; }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "VoiceScrollSpeed":
                {
                    var v = (int)Math.Clamp(value.GetInt32(), (int)VoiceScrollSpeedSlider.Minimum, (int)VoiceScrollSpeedSlider.Maximum);
                    _populatingUi = true;
                    try { ViewModel.ScrollTab.VoiceScrollSpeed = v; VoiceScrollSpeedSlider.Value = v; }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "ScrollStep":
                {
                    var v = (int)Math.Clamp(value.GetInt32(), (int)ScrollStepSlider.Minimum, (int)ScrollStepSlider.Maximum);
                    _populatingUi = true;
                    try { ViewModel.ScrollTab.ScrollStep = v; ScrollStepSlider.Value = v; }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "VoiceThreshold":
                {
                    var v = Math.Clamp(value.GetDouble(), VoiceThresholdSlider.Minimum, VoiceThresholdSlider.Maximum);
                    _populatingUi = true;
                    try
                    {
                        App.Config.Current.Appearance.VoiceRmsThreshold = v;
                        VoiceThresholdValue.Text = $"Current: {v:F1}";
                        VoiceThresholdSlider.Value = v;
                        App.Config.Save();
                    }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "FontColor":
                {
                    var hex = value.GetString() ?? "";
                    if (!hex.StartsWith('#')) hex = "#" + hex;
                    if (!HexColorPattern.IsMatch(hex))
                        return (false, "Enter a valid hex color, e.g. #FF8800");
                    ViewModel.ScrollTab.SetFontColor(hex);
                    CustomColorBox.Text = hex;
                    return (true, null);
                }
                case "FontFamily":
                {
                    var family = value.GetString() ?? "";
                    var installed = GetInstalledFontFamilies();
                    if (!installed.Contains(family))
                        return (false, $"Font family not installed: {family}");
                    _populatingUi = true;
                    try
                    {
                        ViewModel.ScrollTab.FontFamily = family;
                        FontFamilyCombo.SelectedItem = family;
                    }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "ScrollMode":
                {
                    var mode = value.GetString() ?? "";
                    // Flip the physical RadioButton (like a real click) so the mode-specific
                    // control visibility (ScrollModeChanged) and SelectedScrollModeIndex stay
                    // in sync too — not just the underlying OverlayViewModel.ScrollMode.
                    switch (mode)
                    {
                        case "Manual": ScrollManualRadio.IsChecked = true; break;
                        case "Auto":   ScrollAutoRadio.IsChecked   = true; break;
                        case "Voice":  ScrollVoiceRadio.IsChecked  = true; break;
                        default: return (false, "ScrollMode must be Manual, Auto, or Voice");
                    }
                    return (true, null);
                }
                case "InsightFontSize":
                {
                    var v = (int)Math.Clamp(value.GetInt32(), (int)InsightFontSizeSlider.Minimum, (int)InsightFontSizeSlider.Maximum);
                    _populatingUi = true;
                    try { ViewModel.InsightsTab.FontSize = v; InsightFontSizeSlider.Value = v; }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "InsightOpacity":
                {
                    var pct = Math.Clamp(value.GetDouble(), InsightOpacitySlider.Minimum, InsightOpacitySlider.Maximum);
                    _populatingUi = true;
                    try { ViewModel.InsightsTab.Opacity = pct / 100.0; InsightOpacitySlider.Value = pct; }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "InsightFontColor":
                {
                    var hex = value.GetString() ?? "";
                    if (!hex.StartsWith('#')) hex = "#" + hex;
                    if (!HexColorPattern.IsMatch(hex))
                        return (false, "Enter a valid hex color, e.g. #FF8800");
                    ViewModel.InsightsTab.SetFontColor(hex);
                    InsightCustomColorBox.Text = hex;
                    return (true, null);
                }
                case "InsightFontFamily":
                {
                    var family = value.GetString() ?? "";
                    var installed = GetInstalledFontFamilies();
                    if (!installed.Contains(family))
                        return (false, $"Font family not installed: {family}");
                    _populatingUi = true;
                    try
                    {
                        ViewModel.InsightsTab.FontFamily = family;
                        InsightFontFamilyCombo.SelectedItem = family;
                    }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "ShowFollowUpSuggestions":
                {
                    var v = value.GetBoolean();
                    _populatingUi = true;
                    try
                    {
                        ViewModel.AiTab.ShowFollowUpSuggestions = v;
                        ShowFollowUpSuggestionsCheckBox.IsChecked = v;
                    }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "ShowPacingInInsights":
                {
                    var v = value.GetBoolean();
                    _populatingUi = true;
                    try
                    {
                        if (Overlay is not null) Overlay.ViewModel.ShowPacingInInsights = v;
                        ShowPacingCheckBox.IsChecked = v;
                    }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "ShowTokenUsageInInsights":
                {
                    var v = value.GetBoolean();
                    _populatingUi = true;
                    try
                    {
                        if (Overlay is not null) Overlay.ViewModel.ShowTokenUsageInInsights = v;
                        ShowTokenUsageCheckBox.IsChecked = v;
                    }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "ShowFollowUpsInInsights":
                {
                    var v = value.GetBoolean();
                    _populatingUi = true;
                    try
                    {
                        if (Overlay is not null) Overlay.ViewModel.ShowFollowUpsInInsights = v;
                        ShowFollowUpsInInsightsCheckBox.IsChecked = v;
                    }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                case "ShowExternalInsightsInInsights":
                {
                    var v = value.GetBoolean();
                    _populatingUi = true;
                    try
                    {
                        if (Overlay is not null) Overlay.ViewModel.ShowExternalInsightsInInsights = v;
                        ShowExternalInsightsCheckBox.IsChecked = v;
                    }
                    finally { _populatingUi = false; }
                    return (true, null);
                }
                default:
                    return (false, $"Unknown field: {field}");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── AI tab handlers ───────────────────────────────────────────────────────

    private void ChatProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.AiTab.SelectedChatProviderIndex = ChatProviderCombo.SelectedIndex;
    }

    private void TranscriptionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.AiTab.SelectedTranscriptionProviderIndex = TranscriptionCombo.SelectedIndex;
    }

    private void UseLocalWhisperCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.AiTab.UseLocalWhisper = UseLocalWhisperCheckBox.IsChecked == true;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.AiTab.TestConnectionCommand.ExecuteAsync(null);
        ConnectionStatusText.Text = ViewModel.AiTab.ConnectionStatus;
    }

    private void SystemPromptBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.AiTab.SystemPrompt = SystemPromptBox.Text;

    private void PresentationContextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.AiTab.PresentationContext = PresentationContextBox.Text;

    private void GlossaryBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.AiTab.Glossary = GlossaryBox.Text;

    private void MaxTokensSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.AiTab.MaxTokens = (int)e.NewValue;
    }

    private void ShowFollowUpSuggestionsCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.AiTab.ShowFollowUpSuggestions = ShowFollowUpSuggestionsCheckBox.IsChecked == true;
    }

    /// <summary>Pure display toggles for the AI Insights overlay's footer — pacing/usage are
    /// always computed regardless (see OverlayViewModel.ShowPacingInInsights's doc comment), so
    /// these only control whether InsightWindow shows them.</summary>
    private void ShowPacingCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_populatingUi) return;
        if (Overlay is not null) Overlay.ViewModel.ShowPacingInInsights = ShowPacingCheckBox.IsChecked == true;
    }

    private void ShowTokenUsageCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_populatingUi) return;
        if (Overlay is not null) Overlay.ViewModel.ShowTokenUsageInInsights = ShowTokenUsageCheckBox.IsChecked == true;
    }

    /// <summary>Pure display toggle for the Questions (follow-up suggestions) section —
    /// independent of ShowFollowUpSuggestionsCheckBox_Toggled above (which instead controls
    /// whether suggestions are generated at all).</summary>
    private void ShowFollowUpsInInsightsCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_populatingUi) return;
        if (Overlay is not null) Overlay.ViewModel.ShowFollowUpsInInsights = ShowFollowUpsInInsightsCheckBox.IsChecked == true;
    }

    private void ShowExternalInsightsCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_populatingUi) return;
        if (Overlay is not null) Overlay.ViewModel.ShowExternalInsightsInInsights = ShowExternalInsightsCheckBox.IsChecked == true;
    }

    private void ResetUsage_Click(object sender, RoutedEventArgs e) =>
        ViewModel.AiTab.ResetUsageCommand.Execute(null);

    private void ClearConversation_Click(object sender, RoutedEventArgs e) =>
        Overlay?.ViewModel.ClearConversationCommand.Execute(null);

    /// <summary>Shows the remembered Q&amp;A turns (OverlayViewModel.ConversationTurns) in a
    /// popup — replaces the old always-visible "Q&amp;A RECAP" card (which only ever showed the
    /// LAST turn) with an on-demand view of the FULL remembered history (up to
    /// OverlayViewModel.MaxConversationTurns turns) the AI actually uses as context for
    /// follow-up questions.</summary>
    private async void ViewConversationMemory_Click(object sender, RoutedEventArgs e)
    {
        var turns = Overlay?.ViewModel.ConversationTurns;
        var listPanel = new StackPanel { Spacing = 12 };

        if (turns is null || turns.Count == 0)
        {
            listPanel.Children.Add(new TextBlock
            {
                Text         = "No conversation turns remembered yet.",
                Opacity      = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var turn in turns)
            {
                var turnPanel = new StackPanel { Spacing = 4 };
                turnPanel.Children.Add(new TextBlock
                {
                    Text         = $"Q: {turn.UserText}",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight   = Microsoft.UI.Text.FontWeights.SemiBold,
                });
                turnPanel.Children.Add(new TextBlock
                {
                    Text         = $"A: {turn.AssistantText}",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity      = 0.85,
                });
                listPanel.Children.Add(turnPanel);
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot        = Content.XamlRoot,
            Title           = "Conversation memory",
            Content         = new ScrollViewer
            {
                Content                       = listPanel,
                MaxHeight                      = 420,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            CloseButtonText = "Close",
            DefaultButton   = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    private void UpdateConversationTurnCountText()
    {
        var count = Overlay?.ViewModel.ConversationTurnCount ?? 0;
        ConversationTurnCountText.Text = count == 0
            ? "No turns remembered yet."
            : $"{count} turn{(count == 1 ? "" : "s")} remembered.";
    }

    /// <summary>Starts a brand-new Q&amp;A session (always a fresh file — see
    /// OverlayViewModel.StartNewQaSession's doc comment) using whatever optional label is
    /// currently typed in QaSessionLabelBox, then clears that box for the next time.</summary>
    private void StartNewQaSession_Click(object sender, RoutedEventArgs e)
    {
        var label = string.IsNullOrWhiteSpace(QaSessionLabelBox.Text) ? null : QaSessionLabelBox.Text;
        Overlay?.ViewModel.StartNewQaSessionCommand.Execute(label);
        QaSessionLabelBox.Text = "";
        UpdateQaSessionStatusText();
    }

    /// <summary>Ends the active Q&amp;A session without starting a new one — the only other way
    /// to stop recording besides the user closing the whole app.</summary>
    private void CloseQaSession_Click(object sender, RoutedEventArgs e)
    {
        Overlay?.ViewModel.CloseQaSessionCommand.Execute(null);
        UpdateQaSessionStatusText();
    }

    /// <summary>Opens the Q&amp;A sessions folder in Explorer — deliberately no in-app file
    /// browser/viewer (per explicit user decision). Creates the folder first if no session has
    /// ever been started yet, so this never fails with a "path not found" error.</summary>
    private void OpenQaSessionsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Overlay is null) return;
        var path = Overlay.ViewModel.QaSessionsFolderPath;
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void UpdateQaSessionStatusText()
    {
        QaSessionStatusText.Text = Overlay?.ViewModel.QaSessionStatusText ?? "No active session.";
        CloseQaSessionBtn.IsEnabled = Overlay?.ViewModel.IsQaSessionActive ?? false;
    }

    private void WhisperModelBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.AiTab.WhisperModelPath = WhisperModelBox.Text;

    private void BrowseWhisperModel_Click(object sender, RoutedEventArgs e)
    {
        // Win32FileDialog instead of Windows.Storage.Pickers.FileOpenPicker — see its own doc
        // comment (the WinRT picker hangs forever, no dialog ever appearing, in this unpackaged
        // app under some session types).
        var path = Win32.Win32FileDialog.PickSingleFile(_hwnd, "Whisper model files", "bin", "gguf");
        if (path is not null)
        {
            WhisperModelBox.Text = path;
            ViewModel.AiTab.WhisperModelPath = path;
        }
    }

    private void RecheckWhisperModel_Click(object sender, RoutedEventArgs e) =>
        ViewModel.AiTab.RecheckWhisperModelCommand.Execute(null);

    /// <summary>Settings → WHISPER MODEL card's Load/Unload button — a genuine toggle (see
    /// AiTabViewModel.ToggleWhisperModelAsync's doc comment for why this is a separate command
    /// from Recheck, which the Stream Deck plugin and MCP tool rely on unchanged).</summary>
    private void WhisperLoadUnloadBtn_Click(object sender, RoutedEventArgs e) =>
        ViewModel.AiTab.ToggleWhisperModelCommand.Execute(null);

    /// <summary>Forces a Whisper local/cloud recheck — used by the Stream Deck plugin's
    /// AI Status tile press (via the RecheckWhisperModel remote action).</summary>
    public void RecheckWhisperModel() =>
        ViewModel.AiTab.RecheckWhisperModelCommand.Execute(null);

    /// <summary>Resets the session token usage counter — used by the Web Remote/Stream Deck/MCP
    /// "ResetUsage" remote action, mirroring the Q&amp;A tab's own "Reset usage counter" button.</summary>
    public void ResetUsage() =>
        ViewModel.AiTab.ResetUsageCommand.Execute(null);

    // ── About tab ─────────────────────────────────────────────────────────────

    private void GitHubLink_Click(object sender, RoutedEventArgs e) =>
        ViewModel.AboutTab.OpenSourceRepoCommand.Execute(null);

    // ── TP visibility (Open TP) ─────────────────────────────────────────────

    private void OverlayToggle_Checked(object sender, RoutedEventArgs e)
    {
        SyncOverlayToggle(true);
        if (Overlay is not null) WindowService.ShowWindow(Overlay);
    }

    private void OverlayToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        SyncOverlayToggle(false);
        if (Overlay is not null) WindowService.HideWindow(Overlay);
    }

    /// <summary>Syncs the "Open TP" toggle's checked state. The label itself is static
    /// ("📦 Open TP") — only the pressed/checked visual reflects whether the TP is
    /// currently open. Public because the tray menu shows/hides the TP directly via
    /// WindowService without going through this button's click.</summary>
    public void SyncOverlayToggle(bool visible)
    {
        if (OverlayToggle is null) return;
        OverlayToggle.IsChecked = visible;
    }

    /// <summary>Toggles the TP open/hidden — used by the Ctrl+Alt+V global hotkey.</summary>
    public void ToggleOverlayVisibility() =>
        OverlayToggle.IsChecked = !(OverlayToggle.IsChecked ?? false);

    // ── Lock / Unlock TP ──────────────────────────────────────────────────────

    private void LockToggle_Checked(object sender, RoutedEventArgs e)
    {
        SyncLockToggle(true);
        Overlay?.ViewModel.SetMoveMode(false); // locked = click-through
    }

    private void LockToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        SyncLockToggle(false);
        Overlay?.ViewModel.SetMoveMode(true);  // unlocked = interactive
    }

    /// <summary>Syncs the "Lock TP" toggle's checked state (static label; only the
    /// pressed/checked visual reflects the current lock state).</summary>
    public void SyncLockToggle(bool locked)
    {
        if (LockToggle is null) return;
        LockToggle.IsChecked = locked;
    }

    // ── Save / Reset settings ─────────────────────────────────────────────────

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        App.Config.Save();
        SaveStatusText.Text = "✓ Saved";
        var t = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { SaveStatusText.Text = ""; t.Stop(); };
        t.Start();
    }

    /// <summary>Applies a preset swatch's color immediately (same as before) and also mirrors
    /// its hex value into the editable CustomColorBox, so the user can nudge it further from
    /// there instead of only seeing a static readout.</summary>
    private void FontColor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string hex) return;
        ViewModel.ScrollTab.SetFontColor(hex);
        CustomColorBox.Text = hex;
    }

    private static readonly System.Text.RegularExpressions.Regex HexColorPattern =
        new(@"^#[0-9A-Fa-f]{6}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void ApplyCustomColor_Click(object sender, RoutedEventArgs e) => ApplyCustomColor();

    private void CustomColorBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) ApplyCustomColor();
    }

    /// <summary>Validates and applies the user-typed hex color, via the same SetFontColor path
    /// used by the preset swatches. Accepts "#RRGGBB" (with or without the leading '#').</summary>
    private void ApplyCustomColor()
    {
        var text = CustomColorBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;

        if (!HexColorPattern.IsMatch(text))
        {
            CustomColorErrorText.Text       = "Enter a valid hex color, e.g. #FF8800";
            CustomColorErrorText.Visibility = Visibility.Visible;
            return;
        }

        CustomColorErrorText.Visibility = Visibility.Collapsed;
        ViewModel.ScrollTab.SetFontColor(text);
        CustomColorBox.Text = text; // normalizes a leading '#' the user may have omitted
    }

    private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingUi) return;
        if (FontFamilyCombo.SelectedItem is string family)
            ViewModel.ScrollTab.FontFamily = family;
    }

    // ── AI Insights tab — appearance (independent from the TP's own Appearance card above) ──

    private void InsightFontSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.InsightsTab.FontSize = (int)e.NewValue;
    }

    private void InsightOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_populatingUi) return;
        ViewModel.InsightsTab.Opacity = e.NewValue / 100.0;
    }

    /// <summary>Applies a preset swatch's color immediately and mirrors its hex value into the
    /// editable InsightCustomColorBox — same pattern as the TP's own FontColor_Click.</summary>
    private void InsightFontColor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string hex) return;
        ViewModel.InsightsTab.SetFontColor(hex);
        InsightCustomColorBox.Text = hex;
    }

    private void InsightApplyCustomColor_Click(object sender, RoutedEventArgs e) => ApplyInsightCustomColor();

    private void InsightCustomColorBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) ApplyInsightCustomColor();
    }

    /// <summary>Validates and applies the user-typed hex color for the AI Insights window, via
    /// the same SetFontColor path used by the preset swatches. Accepts "#RRGGBB" (with or
    /// without the leading '#').</summary>
    private void ApplyInsightCustomColor()
    {
        var text = InsightCustomColorBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;

        if (!HexColorPattern.IsMatch(text))
        {
            InsightCustomColorErrorText.Text       = "Enter a valid hex color, e.g. #FF8800";
            InsightCustomColorErrorText.Visibility = Visibility.Visible;
            return;
        }

        InsightCustomColorErrorText.Visibility = Visibility.Collapsed;
        ViewModel.InsightsTab.SetFontColor(text);
        InsightCustomColorBox.Text = text; // normalizes a leading '#' the user may have omitted
    }

    private void InsightFontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingUi) return;
        if (InsightFontFamilyCombo.SelectedItem is string family)
            ViewModel.InsightsTab.FontFamily = family;
    }

    /// <summary>Same enumeration as <see cref="PopulateFontFamilies"/> (installed fonts via
    /// <see cref="GetInstalledFontFamilies"/>), targeting the AI Insights tab's own combo/config
    /// instead of the TP's.</summary>
    private void PopulateInsightFontFamilies()
    {
        var installed = GetInstalledFontFamilies();
        InsightFontFamilyCombo.ItemsSource = installed;

        var current = ViewModel.InsightsTab.FontFamily;
        InsightFontFamilyCombo.SelectedItem = installed.Contains(current) ? current : installed.FirstOrDefault();
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ScrollTab.ScrollStep       = 120;
        ViewModel.ScrollTab.ScrollSpeed      = 50;
        ViewModel.ScrollTab.VoiceScrollSpeed = 50;
        ViewModel.ScrollTab.FontSize         = 22;
        ViewModel.ScrollTab.Opacity          = 0.75;
        ScrollStepSlider.Value       = 120;
        ScrollSpeedSlider.Value      = 50;
        VoiceScrollSpeedSlider.Value = 50;
        FontSizeSlider.Value         = 22;
        OpacitySlider.Value          = 75;
        App.Config.Save();
        SaveStatusText.Text = "↺ Defaults restored";
        var t = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { SaveStatusText.Text = ""; t.Stop(); };
        t.Start();
    }

    // ── Footer: Hide Controller (screen-share protection) ─────────────────────

    private void ProtectToggle_Checked(object sender, RoutedEventArgs e) =>
        ViewModel.ControllerProtected = true;

    private void ProtectToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ViewModel.ControllerProtected = false;

    /// <summary>Toggles "Hide Controller" — used by the Ctrl+Alt+H global hotkey.</summary>
    public void ToggleControllerCaptureProtection() =>
        ProtectToggle.IsChecked = !(ProtectToggle.IsChecked ?? false);

    // ── Footer: Hide TP (screen-share protection) ─────────────────────────────
    // Lets the presenter reveal the TP to viewers of a shared screen/recording
    // (default is hidden, same as before this toggle existed).

    private void OverlayProtectToggle_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.OverlayProtected = true;
        SyncOverlayProtectToggle(true);
    }

    private void OverlayProtectToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        ViewModel.OverlayProtected = false;
        SyncOverlayProtectToggle(false);
    }

    /// <summary>Syncs the "Hide TP" toggle's checked state (static label; only the
    /// pressed/checked visual reflects whether the TP is currently hidden from capture).</summary>
    private void SyncOverlayProtectToggle(bool protect)
    {
        if (OverlayProtectToggle is null) return;
        OverlayProtectToggle.IsChecked = protect;
    }

    /// <summary>Toggles "Hide TP" (visible/hidden in share) — used by the Ctrl+Alt+S global hotkey.</summary>
    public void ToggleOverlayCaptureProtection() =>
        OverlayProtectToggle.IsChecked = !(OverlayProtectToggle.IsChecked ?? false);

    // ── Footer: AI Insights window visibility (Open Insights) ────────────────

    private void InsightsToggle_Checked(object sender, RoutedEventArgs e)
    {
        SyncInsightsToggle(true);
        if (Insights is not null) WindowService.ShowWindow(Insights);
    }

    private void InsightsToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        SyncInsightsToggle(false);
        if (Insights is not null) WindowService.HideWindow(Insights);
    }

    /// <summary>Syncs the "Open Insights" toggle's checked state without re-triggering
    /// Checked/Unchecked (mirrors SyncOverlayToggle).</summary>
    public void SyncInsightsToggle(bool visible)
    {
        if (InsightsToggle is null) return;
        InsightsToggle.IsChecked = visible;
    }

    /// <summary>Toggles the AI Insights window open/hidden — used by the Ctrl+Alt+I global hotkey.</summary>
    public void ToggleInsightsVisibility() =>
        InsightsToggle.IsChecked = !(InsightsToggle.IsChecked ?? false);

    // ── Footer: Lock / unlock the AI Insights window ──────────────────────────

    private void InsightsLockToggle_Checked(object sender, RoutedEventArgs e)
    {
        SyncInsightsLockToggle(true);
        Insights?.SetLocked(true);
    }

    private void InsightsLockToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        SyncInsightsLockToggle(false);
        Insights?.SetLocked(false);
    }

    /// <summary>Syncs the "Lock Insights" toggle's checked state (mirrors SyncLockToggle).</summary>
    public void SyncInsightsLockToggle(bool locked)
    {
        if (InsightsLockToggle is null) return;
        InsightsLockToggle.IsChecked = locked;
    }

    /// <summary>Toggles "Lock Insights" — used by the Ctrl+Alt+L global hotkey.</summary>
    public void ToggleInsightsLock() =>
        InsightsLockToggle.IsChecked = !(InsightsLockToggle.IsChecked ?? false);

    // ── Footer: Hide Insights (screen-share protection) ───────────────────────

    private void InsightsProtectToggle_Checked(object sender, RoutedEventArgs e)
    {
        App.Config.Current.InsightWindowProtected = true;
        Insights?.SetContentProtection(true);
        SyncInsightsProtectToggle(true);
    }

    private void InsightsProtectToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        App.Config.Current.InsightWindowProtected = false;
        Insights?.SetContentProtection(false);
        SyncInsightsProtectToggle(false);
    }

    /// <summary>Syncs the "Hide Insights" toggle's checked state (mirrors SyncOverlayProtectToggle).</summary>
    private void SyncInsightsProtectToggle(bool protect)
    {
        if (InsightsProtectToggle is null) return;
        InsightsProtectToggle.IsChecked = protect;
    }

    /// <summary>Toggles "Hide Insights" (visible/hidden in share) — used by the Ctrl+Alt+P global hotkey.</summary>
    public void ToggleInsightsCaptureProtection() =>
        InsightsProtectToggle.IsChecked = !(InsightsProtectToggle.IsChecked ?? false);

    // ── Q&A — Record button (moved from overlay) ──────────────────────────────

    private void ControllerRecordBtn_Click(object sender, RoutedEventArgs e) =>
        _ = Overlay?.ViewModel.ToggleRecordingAsync();

    // ── Appearance tab — font family ───────────────────────────────────────────

    /// <summary>Enumerates every font family actually installed on this PC (not a fixed
    /// hardcoded list) via System.Drawing.Text.InstalledFontCollection — the standard Windows
    /// API for this. Shared by the Appearance picker (<see cref="PopulateFontFamilies"/>), the
    /// remote "FontFamily" setter's validation (<see cref="SetRemoteField"/>), and the MCP
    /// server's list-fonts tool (via <see cref="App.ListFontsRemote"/>) — one source of truth.</summary>
    public static List<string> GetInstalledFontFamilies() =>
        new System.Drawing.Text.InstalledFontCollection()
            .Families.Select(f => f.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Populates the font family picker from fonts actually installed on this PC
    /// (not a fixed hardcoded list) via System.Drawing.Text.InstalledFontCollection — the
    /// standard Windows API for enumerating installed font families.</summary>
    private void PopulateFontFamilies()
    {
        var installed = GetInstalledFontFamilies();
        FontFamilyCombo.ItemsSource = installed;

        var current = ViewModel.ScrollTab.FontFamily;
        FontFamilyCombo.SelectedItem = installed.Contains(current) ? current : installed.FirstOrDefault();
    }

    // ── Settings tab — audio devices ──────────────────────────────────────────

    private void PopulateAudioDevices()
    {
        var inputs  = AudioService.GetInputDevices();
        var outputs = AudioService.GetOutputDevices();

        InputDeviceCombo.ItemsSource   = inputs;
        InputDeviceCombo.DisplayMemberPath = "Name";
        OutputDeviceCombo.ItemsSource  = outputs;
        OutputDeviceCombo.DisplayMemberPath = "Name";

        // Restore saved selection
        var savedInput  = App.Config.Current.AudioDeviceId;
        var savedOutput = App.Config.Current.AudioOutputDeviceId;

        var inputIdx  = inputs.ToList().FindIndex(d => d.Id == savedInput);
        var outputIdx = outputs.ToList().FindIndex(d => d.Id == savedOutput);

        InputDeviceCombo.SelectedIndex  = inputIdx  >= 0 ? inputIdx  : -1;
        OutputDeviceCombo.SelectedIndex = outputIdx >= 0 ? outputIdx : -1;
    }

    // ── AI providers (Settings tab) ─────────────────────────────────────────────
    // 6 independent cards — configuring/testing provider X here never depends on which
    // provider is currently SELECTED for chat/transcription in the Q&A tab (that tab only
    // picks which one to USE). See ProviderConfigDialog for the actual credential editor.

    private void PopulateProviderStatuses()
    {
        AzureProviderStatus.Text     = ProviderStatusText(!string.IsNullOrEmpty(App.Config.Current.Azure.Endpoint) && !string.IsNullOrEmpty(App.Config.Current.Azure.Key));
        OpenAiProviderStatus.Text    = ProviderStatusText(!string.IsNullOrEmpty(App.Config.Current.OpenAi.Key));
        GroqProviderStatus.Text      = ProviderStatusText(!string.IsNullOrEmpty(App.Config.Current.Groq.Key));
        AnthropicProviderStatus.Text = ProviderStatusText(!string.IsNullOrEmpty(App.Config.Current.Anthropic.Key));
        GeminiProviderStatus.Text    = ProviderStatusText(!string.IsNullOrEmpty(App.Config.Current.Gemini.Key));
        MistralProviderStatus.Text   = ProviderStatusText(!string.IsNullOrEmpty(App.Config.Current.Mistral.Key));
        // No key required for Local LM (most self-hosted servers have no auth) — "configured"
        // means the user has pointed it at a real base URL AND named at least one model (chat
        // or Whisper — one config now optionally serves both roles).
        var loc = App.Config.Current.Local;
        LocalProviderStatus.Text     = ProviderStatusText(!string.IsNullOrEmpty(loc.BaseUrl) &&
            (!string.IsNullOrEmpty(loc.ChatModel) || !string.IsNullOrEmpty(loc.WhisperModel)));
    }

    private static string ProviderStatusText(bool configured) =>
        configured ? "✓ Configured" : "Not configured";

    private async void ConfigureProviderCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string providerKey) return;
        var dialog = new ProviderConfigDialog(App.Config, App.AiChat, providerKey)
        {
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
        PopulateProviderStatuses(); // reflect whatever was just Saved (or not, if Cancelled)
    }

    private void AudioSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingUi) return;
        var tag = (AudioSourceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "microphone";
        App.Config.Current.AudioRecordingSource = tag;
        App.Config.Save();
        StopMicTest(); // changing source mid-test would otherwise leave a stale capture open
                        // against whatever device/source was selected when Test was clicked
    }

    private void InputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingUi) return;
        if (InputDeviceCombo.SelectedItem is AudioService.AudioDeviceInfo dev)
        {
            App.Config.Current.AudioDeviceId = dev.Id;
            App.Config.Save();
            StopMicTest();
        }
    }

    private void OutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingUi) return;
        if (OutputDeviceCombo.SelectedItem is AudioService.AudioDeviceInfo dev)
        {
            App.Config.Current.AudioOutputDeviceId = dev.Id;
            App.Config.Save();
            StopMicTest();
        }
    }

    private void VoiceThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_populatingUi) return;
        App.Config.Current.Appearance.VoiceRmsThreshold = e.NewValue;
        VoiceThresholdValue.Text = $"Current: {e.NewValue:F1}";
        App.Config.Save();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        _populatingUi = true;
        try { PopulateAudioDevices(); }
        finally { _populatingUi = false; }
    }

    // ── Mic level test (Settings tab) ──────────────────────────────────────────
    // Reuses AudioService's existing voice-monitor RMS plumbing (the same one behind the
    // Script tab's Voice scroll mode indicator) rather than a second capture path. AudioService
    // only supports ONE monitor at a time, so if Voice scroll mode currently owns it, starting a
    // test switches scroll mode to Manual first (which itself calls StopVoiceMonitor via
    // OverlayViewModel.OnScrollModeChanged) to free it up, rather than refusing or silently
    // stealing it out from under Voice mode without telling the user.

    private bool _micTestActive;

    private void MicTestToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_micTestActive) { StopMicTest(); return; }

        if (Overlay?.ViewModel.ScrollMode == ViewModels.ScrollMode.Voice)
        {
            ScrollManualRadio.IsChecked = true; // frees the single mic-monitor slot Voice mode was using
            MicTestStatusText.Text = "ℹ Switched scroll mode to Manual (Voice mode was using the microphone monitor).";
        }
        else
        {
            MicTestStatusText.Text = "";
        }

        _micTestActive = true;
        MicTestPanel.Visibility = Visibility.Visible;
        ResetMicTestLevel(); // clear any stale reading left over from a previous test run —
                              // otherwise the old number (e.g. from Microphone) stays frozen on
                              // screen if the newly selected source is currently silent (WASAPI
                              // loopback in particular delivers NO buffers at all while nothing
                              // is playing, so there'd be nothing to overwrite it with)
        App.Audio.StartVoiceMonitor(App.Config.Current.AudioRecordingSource, OnMicTestRms,
            App.Config.Current.AudioDeviceId, App.Config.Current.AudioOutputDeviceId);
    }

    private void OnMicTestRms(float rms) => DispatcherQueue.TryEnqueue(() =>
    {
        MicTestLevelBar.Value  = rms;
        MicTestLevelText.Text  = $"{rms:F1}";
    });

    private void ResetMicTestLevel()
    {
        MicTestLevelBar.Value = 0;
        MicTestLevelText.Text = "0.0";
    }

    private void StopMicTest()
    {
        if (!_micTestActive) return;
        _micTestActive = false;
        App.Audio.StopVoiceMonitor();
        MicTestToggle.IsChecked  = false;
        MicTestPanel.Visibility  = Visibility.Collapsed;
        MicTestStatusText.Text   = "";
        ResetMicTestLevel(); // don't leave a stale reading behind for the next time the panel opens
    }

    // ── Window stealth ────────────────────────────────────────────────────────

    private StealthWindowService.WindowInfo? _selectedStealthWindow;
    private readonly WindowEmbedService      _embedService = new();

    private void PopulateWindowList()
    {
        var windows = StealthWindowService.GetVisibleWindows();
        WindowListCombo.ItemsSource   = windows;
        WindowListCombo.SelectedIndex = -1;
        EmbedBtn.IsEnabled            = false;
        StealthStatusText.Text        = $"{windows.Count} windows found";
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) =>
        PopulateWindowList();

    private void WindowListCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedStealthWindow = WindowListCombo.SelectedItem as StealthWindowService.WindowInfo;
        EmbedBtn.IsEnabled = _selectedStealthWindow is not null && !_embedService.IsEmbedding;
        StealthStatusText.Text = _selectedStealthWindow?.StatusText ?? "";
    }

    private void EmbedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedStealthWindow is null) return;

        // Position container next to the controller, sized to the target's current dimensions
        var (cx, cy, cw, _) = WindowService.GetGeometry(this);
        int x = (int)(cx + cw + 16);
        int y = (int)cy;
        int w = _selectedStealthWindow.Handle != IntPtr.Zero
            ? 900 : 900;
        int h = 600;

        bool ok = _embedService.Embed(
            _selectedStealthWindow.Handle,
            _selectedStealthWindow.Title,
            x, y, w, h);

        if (ok)
        {
            StealthStatusText.Text     = "🔒 Embedded — container is stealth";
            EmbedBtn.Visibility        = Visibility.Collapsed;
            ReleaseEmbedBtn.Visibility = Visibility.Visible;
        }
        else
        {
            StealthStatusText.Text = "⚠ Failed to create embed container";
        }
    }

    private void ReleaseEmbedBtn_Click(object sender, RoutedEventArgs e) => ReleaseStealthContainer();

    /// <summary>
    /// Releases the embedded App Stealth container, if any. Safe to call when nothing is
    /// embedded (no-op) — used by both the Release button and the Ctrl+Alt+U global hotkey.
    /// </summary>
    public void ReleaseStealthContainer()
    {
        if (!_embedService.IsEmbedding) return;

        _embedService.Release();
        StealthStatusText.Text     = "Released";
        EmbedBtn.Visibility        = Visibility.Visible;
        ReleaseEmbedBtn.Visibility = Visibility.Collapsed;
        EmbedBtn.IsEnabled         = _selectedStealthWindow is not null;
    }

    /// <summary>Small JSON-friendly projection of <see cref="StealthWindowService.WindowInfo"/>
    /// for the Web Remote's App Stealth tab — the real type carries a native <see cref="IntPtr"/>
    /// handle that doesn't round-trip through System.Text.Json, so it's serialized as a decimal
    /// string (<see cref="Id"/>) instead. <see cref="Display"/> reuses
    /// <see cref="StealthWindowService.WindowInfo.ToString"/> so the remote page's window list
    /// never needs its own copy of the "Title  [ProcessName]" formatting logic.</summary>
    public sealed class RemoteWindowInfo
    {
        public string Id      { get; init; } = "";
        public string Display { get; init; } = "";
    }

    /// <summary>Enumerates visible windows for the Web Remote's App Stealth tab — same source
    /// list <see cref="PopulateWindowList"/> uses for <see cref="WindowListCombo"/>.</summary>
    public List<RemoteWindowInfo> ListStealthWindowsRemote() =>
        StealthWindowService.GetVisibleWindows()
            .Select(w => new RemoteWindowInfo { Id = w.Handle.ToInt64().ToString(), Display = w.ToString() })
            .ToList();

    /// <summary>Embeds the window identified by <paramref name="id"/> (as produced by
    /// <see cref="ListStealthWindowsRemote"/>) — the Web Remote's equivalent of picking a window
    /// in <see cref="WindowListCombo"/> then pressing <see cref="EmbedBtn"/>. Re-resolves the
    /// window list fresh rather than trusting a stale handle, since a window can close between
    /// the remote client's list fetch and its embed request.</summary>
    public (bool Success, string? Error) EmbedStealthWindowRemote(string id)
    {
        if (!long.TryParse(id, out var handleValue)) return (false, "Invalid window id");
        var handle = new IntPtr(handleValue);
        var target = StealthWindowService.GetVisibleWindows().FirstOrDefault(w => w.Handle == handle);
        if (target is null) return (false, "Window no longer available — refresh the list");

        var (cx, cy, cw, _) = WindowService.GetGeometry(this);
        bool ok = _embedService.Embed(target.Handle, target.Title, (int)(cx + cw + 16), (int)cy, 900, 600);
        if (!ok) return (false, "Failed to create embed container");

        _selectedStealthWindow     = target;
        StealthStatusText.Text     = "🔒 Embedded — container is stealth";
        EmbedBtn.Visibility        = Visibility.Collapsed;
        ReleaseEmbedBtn.Visibility = Visibility.Visible;
        return (true, null);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void OnClosed(object sender, WindowEventArgs args)
    {
        StopMicTest();
        _embedService.Dispose();

        // Restore any stealthed windows before quitting
        var stealthed = (WindowListCombo.ItemsSource as IEnumerable<StealthWindowService.WindowInfo>) ?? [];
        StealthWindowService.RestoreAll(stealthed);

        Overlay?.ViewModel.Cleanup();
        Overlay?.SaveGeometry();
        var (x, y, w, h) = WindowService.GetGeometry(this);
        var s = App.Config.Current.ControllerWindow;
        s.X = x; s.Y = y; s.Width = w; s.Height = h;
        App.Config.Save();
    }
}
