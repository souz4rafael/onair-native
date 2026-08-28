using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OnAirNative.Models;
using OnAirNative.Services;
using OnAirNative.ViewModels;
using OnAirNative.Views.Dialogs;
using OnAirNative.Win32;

namespace OnAirNative.Views;

/// <summary>
/// Controller window — the presenter's control panel.
/// Contains 5 tabs (Script, Q&amp;A, App Stealth, Settings, About) plus the footer
/// overlay/visibility and screen-share protection toggles.
/// </summary>
public sealed partial class ControllerWindow : Window
{
    public ControllerViewModel ViewModel { get; private set; } = null!;
    public OverlayWindow?      Overlay   { get; set; }

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

            // Box (overlay window) starts hidden — sync the toggle button
            OverlayToggle.IsChecked = false;
            SyncOverlayToggle(false);

            // Box starts unlocked (move mode = interactive)
            LockToggle.IsChecked = false;
            SyncLockToggle(false);

            // Screen-share protection state
            WindowService.SetContentProtection(_hwnd, App.Config.Current.ControllerProtected);
            ProtectToggle.IsChecked = App.Config.Current.ControllerProtected;

            // Box screen-share protection state (applied by OverlayWindow itself on
            // its own first-activate; here we just sync the footer toggle to match).
            OverlayProtectToggle.IsChecked = App.Config.Current.OverlayProtected;
            SyncOverlayProtectToggle(App.Config.Current.OverlayProtected);

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
        ViewModel = new ControllerViewModel(App.Config, Overlay!.ViewModel, App.AiChat, App.Update);
        ViewModel.ControllerProtectionChanged += (_, protect) =>
            WindowService.SetContentProtection(_hwnd, protect);
        ViewModel.OverlayProtectionChanged += (_, protect) =>
        {
            if (Overlay is not null)
                WindowService.SetContentProtection(WindowService.GetHwnd(Overlay), protect);
        };
        ViewModel.ThemeChanged += (_, theme) => ApplyTheme(theme);

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

        // Keep FileNameText live when a new script is loaded
        ViewModel.ScrollTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScrollTabViewModel.LoadedFileName))
                FileNameText.Text = ViewModel.ScrollTab.LoadedFileName;
        };

        // Sync LockToggle when move mode changes (e.g., via Ctrl+Alt+Home hotkey).
        // Setting IsChecked (not just calling SyncLockToggle) keeps the button's actual
        // toggle state in sync with its label — otherwise a hotkey-driven change would
        // update the text but leave IsChecked stale, so the next manual click would
        // silently re-affirm the current state instead of flipping it as the label implies.
        Overlay.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OnAirNative.ViewModels.OverlayViewModel.IsMoveModeActive))
                LockToggle.IsChecked = !Overlay.ViewModel.IsMoveModeActive;
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

            SystemPromptBox.Text        = ViewModel.AiTab.SystemPrompt;
            PresentationContextBox.Text = ViewModel.AiTab.PresentationContext;
            WhisperModelBox.Text        = ViewModel.AiTab.WhisperModelPath;

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

            FileNameText.Text = ViewModel.ScrollTab.LoadedFileName;
            FontColorIndicator.Text = App.Config.Current.Appearance.FontColor;

            // About
            VersionText.Text = $"v{ViewModel.AboutTab.Version}";
            AuthorsText.Text = ViewModel.AboutTab.Authors;
            UpdateStatusTextBlock.Text  = "";
            UpdateDownloadPanel.Visibility = Visibility.Collapsed;
            UpdateProgressBar.Visibility   = Visibility.Collapsed;

            // Select first nav item
            NavView.SelectedItem = NavView.MenuItems[0];

            // Settings tab — audio devices + voice threshold
            PopulateAudioDevices();
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

    // ── NavigationView tab switching ──────────────────────────────────────────

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();

        ScrollPanel.Visibility   = tag == "script"   ? Visibility.Visible : Visibility.Collapsed;
        AiPanel.Visibility       = tag == "qa"       ? Visibility.Visible : Visibility.Collapsed;
        StealthPanel.Visibility  = tag == "stealth"  ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility    = tag == "about"    ? Visibility.Visible : Visibility.Collapsed;

        // Auto-refresh window list when App Stealth tab is opened
        if (tag == "stealth" && WindowListCombo.Items.Count == 0)
            PopulateWindowList();

        // Auto-refresh device list when Settings tab is opened
        if (tag == "settings" && WindowListCombo.Items.Count == 0)
            PopulateAudioDevices();

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

    private async void ConfigureProvider_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProviderConfigDialog(App.Config, ViewModel.AiTab)
        {
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
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

    private void WhisperModelBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.AiTab.WhisperModelPath = WhisperModelBox.Text;

    private async void BrowseWhisperModel_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add(".bin");
        picker.FileTypeFilter.Add(".gguf");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            WhisperModelBox.Text = file.Path;
            ViewModel.AiTab.WhisperModelPath = file.Path;
        }
    }

    // ── About tab ─────────────────────────────────────────────────────────────

    private void GitHubLink_Click(object sender, RoutedEventArgs e) =>
        ViewModel.AboutTab.OpenSourceRepoCommand.Execute(null);

    // ── Box visibility (Open Box) ─────────────────────────────────────────────

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

    /// <summary>Syncs the "Open Box" toggle's checked state. The label itself is static
    /// ("📦 Open Box") — only the pressed/checked visual reflects whether the Box is
    /// currently open. Public because the tray menu shows/hides the Box directly via
    /// WindowService without going through this button's click.</summary>
    public void SyncOverlayToggle(bool visible)
    {
        if (OverlayToggle is null) return;
        OverlayToggle.IsChecked = visible;
    }

    /// <summary>Toggles the Box open/hidden — used by the Ctrl+Alt+V global hotkey.</summary>
    public void ToggleOverlayVisibility() =>
        OverlayToggle.IsChecked = !(OverlayToggle.IsChecked ?? false);

    // ── Lock / Unlock Box ──────────────────────────────────────────────────────

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

    /// <summary>Syncs the "Lock Box" toggle's checked state (static label; only the
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

    private void FontColor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string hex) return;
        ViewModel.ScrollTab.SetFontColor(hex);
        FontColorIndicator.Text = hex;
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

    // ── Footer: Hide Box (screen-share protection) ─────────────────────────────
    // Lets the presenter reveal the Box to viewers of a shared screen/recording
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

    /// <summary>Syncs the "Hide Box" toggle's checked state (static label; only the
    /// pressed/checked visual reflects whether the Box is currently hidden from capture).</summary>
    private void SyncOverlayProtectToggle(bool protect)
    {
        if (OverlayProtectToggle is null) return;
        OverlayProtectToggle.IsChecked = protect;
    }

    /// <summary>Toggles "Hide Box" (visible/hidden in share) — used by the Ctrl+Alt+S global hotkey.</summary>
    public void ToggleOverlayCaptureProtection() =>
        OverlayProtectToggle.IsChecked = !(OverlayProtectToggle.IsChecked ?? false);

    // ── Q&A — Record button (moved from overlay) ──────────────────────────────

    private void ControllerRecordBtn_Click(object sender, RoutedEventArgs e) =>
        _ = Overlay?.ViewModel.ToggleRecordingAsync();

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

    private void AudioSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingUi) return;
        var tag = (AudioSourceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "microphone";
        App.Config.Current.AudioRecordingSource = tag;
        App.Config.Save();
    }

    private void InputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingUi) return;
        if (InputDeviceCombo.SelectedItem is AudioService.AudioDeviceInfo dev)
        {
            App.Config.Current.AudioDeviceId = dev.Id;
            App.Config.Save();
        }
    }

    private void OutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingUi) return;
        if (OutputDeviceCombo.SelectedItem is AudioService.AudioDeviceInfo dev)
        {
            App.Config.Current.AudioOutputDeviceId = dev.Id;
            App.Config.Save();
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

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void OnClosed(object sender, WindowEventArgs args)
    {
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
