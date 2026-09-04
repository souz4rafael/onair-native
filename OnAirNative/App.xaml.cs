using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using OnAirNative.Services;
using OnAirNative.Views;

namespace OnAirNative;

public partial class App : Application
{
    // Set in the constructor so Program.cs can route redirected activations here.
    public static App Instance { get; private set; } = null!;

    // Singleton services — consumed by ViewModels and Views
    public static ConfigService     Config     { get; private set; } = null!;
    public static AudioService      Audio      { get; private set; } = null!;
    public static WhisperService    Whisper    { get; private set; } = null!;
    public static AiChatService     AiChat     { get; private set; } = null!;
    public static HotkeyService     Hotkeys    { get; private set; } = null!;
    public static TrayService       Tray       { get; private set; } = null!;
    public static UpdateService     Update     { get; private set; } = null!;
    public static RemoteControlService? RemoteControl { get; private set; }
    public static WebRemoteService?    WebRemote  { get; private set; }
    /// <summary>True when the Web Remote is enabled but couldn't bind because the one-time
    /// Windows URL-ACL grant hasn't been made yet — drives the Settings tab's "needs elevation"
    /// status text / "Grant Network Access" button.</summary>
    public static bool WebRemoteNeedsElevation { get; private set; }

    private OverlayWindow?    _overlay;
    private ControllerWindow? _controller;
    private InsightWindow?    _insightWindow;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    public App()
    {
        Instance = this;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            LaunchCore(args);
        }
        catch (Exception ex)
        {
            var log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "onAIr", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, $"{DateTime.Now}\n{ex}\n");
            throw;
        }
    }

    private void LaunchCore(LaunchActivatedEventArgs args)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "onAIr");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "launch.log");
        File.AppendAllText(logPath, $"\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} === Launch start ===\n");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} UnhandledException: {e.ExceptionObject}\n");

        // Init services
        // ONAIR_CONFIG_DIR lets OnAirNative.IntegrationTests point config.json at an isolated
        // temp directory instead of the real %LocalAppData%\onAIr\ — unset in every normal
        // launch (dev, installed, Store), so behavior is completely unchanged for real users.
        Config  = new ConfigService(Environment.GetEnvironmentVariable("ONAIR_CONFIG_DIR"));
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} ConfigService OK\n");
        Audio   = new AudioService();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} AudioService OK\n");
        Whisper = new WhisperService();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} WhisperService OK\n");
        AiChat  = new AiChatService();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} AiChatService OK\n");
        Update  = new UpdateService();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} UpdateService OK\n");

        // Create and show windows
        _overlay    = new OverlayWindow();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} OverlayWindow created\n");
        _controller = new ControllerWindow();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} ControllerWindow created\n");
        // AI Insights window — a separate, freely resizable panel independent from the TP (see
        // Views/InsightWindow.xaml). Observes _overlay.ViewModel directly rather than owning its
        // own copy of the insight state. Takes AiChat (not AiTabViewModel/ControllerViewModel)
        // for its token-usage display, since ControllerViewModel doesn't exist yet at this point
        // (it's only created in ControllerWindow.InitViewModel, on first Activate) — AiChat is
        // already fully constructed above, so there's no ordering issue.
        _insightWindow = new InsightWindow(_overlay.ViewModel, AiChat);
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} InsightWindow created\n");

        // Wire overlay → controller reference for cross-window commands
        _overlay.Controller = _controller;
        _controller.Overlay = _overlay;
        _controller.Insights = _insightWindow;

        _overlay.Activate();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} Overlay activated\n");
        // Hide overlay immediately — user shows it from Controller when ready
        _overlay.AppWindow.Hide();
        _insightWindow.Activate();
        // Hide Insights immediately too — same "show from Controller when ready" convention
        _insightWindow.AppWindow.Hide();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} InsightWindow activated+hidden\n");
        _controller.Activate();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} Controller activated\n");

        // InitViewModel is called from ControllerWindow.OnFirstActivated (after _hwnd is valid)

        // Global hotkeys — start after windows are created so HWNDs are valid
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Hotkeys = new HotkeyService(_uiQueue);
        Hotkeys.HotkeyTriggered += OnHotkeyTriggered;
        Hotkeys.Start();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} HotkeyService started\n");

        // System tray icon
        var uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Tray = new TrayService(uiQueue);
        Tray.ShowOverlayClicked   += (_, _) => { WindowService.ShowWindow(_overlay!); _controller?.SyncOverlayToggle(true); };
        Tray.HideOverlayClicked   += (_, _) => { WindowService.HideWindow(_overlay!); _controller?.SyncOverlayToggle(false); };
        Tray.LoadScriptClicked    += (_, _) => _ = _overlay?.ViewModel.OpenFilePickerAsync(_overlay!);
        Tray.ShowControllerClicked += (_, _) => { if (_controller is not null) WindowService.BringToFront(_controller); };
        Tray.QuitClicked          += (_, _) => _controller?.Close();
        Tray.Start();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} TrayService started\n");

        // Stream Deck remote control — localhost-only WebSocket server, user-toggleable from
        // the Settings tab (Config.RemoteControlEnabled). Best-effort: a failure here (e.g. the
        // port is already in use) must never prevent the app itself from starting.
        if (Config.Current.RemoteControlEnabled)
            StartRemoteControl();
        else
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} RemoteControlService not started (disabled in settings)\n");

        // Web Remote — LAN-reachable sibling server (WebRemoteService, port 47824), off by
        // default. Same best-effort posture: never let a bind failure block app startup. Unlike
        // RemoteControl, a failure here is commonly EXPECTED (URL-ACL not granted yet) rather
        // than exceptional — see StartWebRemote's own doc comment.
        if (Config.Current.WebRemote.Enabled)
            StartWebRemote();

        // PopulateStaticUi() already ran (triggered by _controller.Activate() above, before this
        // point) and read App.RemoteControl too early — refresh now that start/skip is decided.
        _controller.RefreshRemoteControlStatusText();
        _controller.RefreshWebRemoteStatusText();

        // Handle .txt file opened via right-click → "Open with onAIr"
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        HandleActivation(activationArgs);

        // Closing the Controller = quit the entire app
        _controller.Closed += (_, _) =>
        {
            _overlay?.SaveGeometry();
            _insightWindow?.SaveGeometry();
            Config.Save();
            Hotkeys.Dispose();
            Tray.Dispose();
            RemoteControl?.Dispose();
            WebRemote?.Dispose();
            Audio.Dispose();
            Whisper.Dispose();
            AiChat.Dispose();

            // Explicitly close every other top-level window — Application.Exit() below does not
            // reliably tear down independently-Activate()'d Window instances in this unpackaged
            // WinAppSDK app (observed: it closed InsightWindow but left OverlayWindow/the TP
            // running after the Controller closed). Best-effort/try-catch since a window may
            // already be in a partially torn-down state by the time this runs.
            try { _overlay?.Close(); } catch { /* best-effort */ }
            try { _insightWindow?.Close(); } catch { /* best-effort */ }

            Exit();

            // Hard-stop fallback: everything above (state saved, services disposed, windows
            // asked to close, Exit() requested) already happened, so it's safe to guarantee the
            // process itself actually terminates instead of relying on Exit()'s own async
            // shutdown to reliably tear down every window/thread.
            Environment.Exit(0);
        };
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} LaunchCore done\n");
    }

    private void OnHotkeyTriggered(object? sender, HotkeyAction action) => ExecuteAction(action);

    /// <summary>
    /// Executes a <see cref="HotkeyAction"/> against the live windows/services — the single
    /// dispatch point shared by the physical global hotkeys (<see cref="HotkeyService"/>) and
    /// the Stream Deck remote control WebSocket (<see cref="RemoteControlService"/>), so both
    /// input sources can never drift out of sync with each other's behavior.
    /// </summary>
    public void ExecuteAction(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ScrollUp:
                _overlay?.ViewModel.Scroll(-Config.Current.Appearance.ScrollStep);
                break;
            case HotkeyAction.ScrollDown:
                _overlay?.ViewModel.Scroll(Config.Current.Appearance.ScrollStep);
                break;
            case HotkeyAction.ToggleMoveMode:
                _overlay?.ViewModel.ToggleMoveMode();
                break;
            case HotkeyAction.ToggleRecording:
                _ = _overlay?.ViewModel.ToggleRecordingAsync();
                break;
            case HotkeyAction.OpenFile:
                _ = _overlay?.ViewModel.OpenFilePickerAsync(_overlay);
                break;
            case HotkeyAction.IncreaseOpacity:
                _controller?.AdjustOpacity(+1);
                break;
            case HotkeyAction.DecreaseOpacity:
                _controller?.AdjustOpacity(-1);
                break;
            case HotkeyAction.ReleaseStealthContainer:
                _controller?.ReleaseStealthContainer();
                break;
            case HotkeyAction.ToggleOverlayVisibility:
                _controller?.ToggleOverlayVisibility();
                break;
            case HotkeyAction.ToggleOverlayCaptureProtection:
                _controller?.ToggleOverlayCaptureProtection();
                break;
            case HotkeyAction.ToggleControllerCaptureProtection:
                _controller?.ToggleControllerCaptureProtection();
                break;
            case HotkeyAction.IncreaseScrollSpeed:
                _controller?.AdjustScrollSpeed(+1);
                break;
            case HotkeyAction.DecreaseScrollSpeed:
                _controller?.AdjustScrollSpeed(-1);
                break;
            case HotkeyAction.IncreaseFontSize:
                _controller?.AdjustFontSize(+1);
                break;
            case HotkeyAction.DecreaseFontSize:
                _controller?.AdjustFontSize(-1);
                break;
            case HotkeyAction.IncreaseVoiceScrollSpeed:
                _controller?.AdjustVoiceScrollSpeed(+1);
                break;
            case HotkeyAction.DecreaseVoiceScrollSpeed:
                _controller?.AdjustVoiceScrollSpeed(-1);
                break;
            case HotkeyAction.IncreaseScrollStep:
                _controller?.AdjustScrollStep(+1);
                break;
            case HotkeyAction.DecreaseScrollStep:
                _controller?.AdjustScrollStep(-1);
                break;
            case HotkeyAction.IncreaseVoiceThreshold:
                _controller?.AdjustVoiceThreshold(+1);
                break;
            case HotkeyAction.DecreaseVoiceThreshold:
                _controller?.AdjustVoiceThreshold(-1);
                break;
            case HotkeyAction.RecheckWhisperModel:
                _controller?.RecheckWhisperModel();
                break;
            case HotkeyAction.ToggleInsightsVisibility:
                _controller?.ToggleInsightsVisibility();
                break;
            case HotkeyAction.ToggleInsightsLock:
                _controller?.ToggleInsightsLock();
                break;
            case HotkeyAction.ToggleInsightsCaptureProtection:
                _controller?.ToggleInsightsCaptureProtection();
                break;
            case HotkeyAction.ClearConversation:
                _overlay?.ViewModel.ClearConversationCommand.Execute(null);
                break;
            case HotkeyAction.StartNewQaSession:
                _overlay?.ViewModel.StartNewQaSessionCommand.Execute(null);
                break;
            case HotkeyAction.CloseQaSession:
                _overlay?.ViewModel.CloseQaSessionCommand.Execute(null);
                break;
            case HotkeyAction.ResetUsage:
                _controller?.ResetUsage();
                break;
        }
        NotifyRemoteClientsStateChanged();
    }

    /// <summary>Fans out a state-changed notification to every remote server that's currently
    /// running — the loopback Stream Deck/MCP server AND the LAN-reachable Web Remote, if
    /// enabled. Kept as a single helper so a caller (this class or <see cref="ControllerWindow"/>)
    /// never has to remember to update both individually.</summary>
    public void NotifyRemoteClientsStateChanged()
    {
        RemoteControl?.NotifyStateMayHaveChanged();
        WebRemote?.NotifyStateMayHaveChanged();
    }

    /// <summary>Builds a snapshot of the current, remotely-interesting app state — consumed by
    /// <see cref="RemoteControlService"/> to push to the Stream Deck plugin.</summary>
    public RemoteState GetRemoteState() => _controller?.GetRemoteState() ?? new RemoteState();

    /// <summary>Applies an absolute setter value by field name — the MCP server's write path
    /// (e.g. "set font size to 24"), as opposed to the relative Increase/Decrease
    /// <see cref="HotkeyAction"/>s used by physical hotkeys/dials. Delegates to
    /// <see cref="ControllerWindow.SetRemoteField"/>, which owns the actual validation (same
    /// clamps/regex the UI itself uses) since it alone has the live slider bounds/font list.</summary>
    public (bool Success, string? Error) SetRemoteField(string field, System.Text.Json.JsonElement value)
        => _controller?.SetRemoteField(field, value) ?? (false, "Controller not ready");

    /// <summary>Loads a script by explicit file path — the MCP server's non-interactive
    /// alternative to <see cref="HotkeyAction.OpenFile"/> (which pops the file picker UI).
    /// Validates existence/extension itself before delegating to the same
    /// <see cref="ViewModels.OverlayViewModel.LoadScriptAsync"/> the file picker uses.</summary>
    public async Task<(bool Success, string? Error)> LoadScriptRemoteAsync(string path)
    {
        if (_overlay is null) return (false, "Overlay not ready");
        if (string.IsNullOrWhiteSpace(path)) return (false, "Path is required");
        if (!string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
            return (false, "Only .txt files are supported");
        if (!File.Exists(path)) return (false, $"File not found: {path}");

        await _overlay.ViewModel.LoadScriptAsync(path);
        NotifyRemoteClientsStateChanged();
        return (true, null);
    }

    /// <summary>Returns the currently loaded script's full text — the MCP server's read path
    /// for "what's on the teleprompter right now".</summary>
    public string GetScriptTextRemote() => _overlay?.ViewModel.ScriptText ?? "";

    /// <summary>Returns every font family installed on this PC — same source
    /// <see cref="Views.ControllerWindow.PopulateFontFamilies"/> uses for the Appearance picker,
    /// so the MCP server's "list fonts" tool and the Settings UI can never disagree.</summary>
    public List<string> ListFontsRemote() => Views.ControllerWindow.GetInstalledFontFamilies();

    /// <summary>Pushes a Copilot-insight message into the TP's footer (Block 6) — the write
    /// path for an EXTERNAL agent monitoring onAIr's Q&amp;A over MCP (see
    /// OverlayViewModel.SetInsight for the length cap / blank-clears behavior). Deliberately
    /// requires non-blank text — a caller that wants to remove the insight should call
    /// <see cref="ClearInsightRemote"/> instead, an explicit, unambiguous action rather than
    /// relying on an implicit "empty string means clear" convention.</summary>
    public (bool Success, string? Error) ShowInsightRemote(string text)
    {
        if (_overlay is null) return (false, "Overlay not ready");
        if (string.IsNullOrWhiteSpace(text)) return (false, "Insight text is required — call clear_insight to remove it instead");
        _overlay.ViewModel.SetInsight(text);
        NotifyRemoteClientsStateChanged();
        return (true, null);
    }

    /// <summary>Clears the TP's Copilot-insight footer, if any is currently shown.</summary>
    public (bool Success, string? Error) ClearInsightRemote()
    {
        if (_overlay is null) return (false, "Overlay not ready");
        _overlay.ViewModel.ClearInsight();
        NotifyRemoteClientsStateChanged();
        return (true, null);
    }

    /// <summary>Enumerates visible top-level windows for the Web Remote's App Stealth tab — same
    /// source list the native Stealth tab's WindowListCombo uses, projected through
    /// <see cref="ControllerWindow.ListStealthWindowsRemote"/> since only it has the live
    /// StealthWindowService state.</summary>
    public List<ControllerWindow.RemoteWindowInfo> ListStealthWindowsRemote() =>
        _controller?.ListStealthWindowsRemote() ?? [];

    /// <summary>Embeds the window identified by <paramref name="id"/> (from
    /// <see cref="ListStealthWindowsRemote"/>) into the App Stealth container — the Web Remote's
    /// equivalent of picking a window in the native Stealth tab then pressing "Embed in
    /// container". Delegates to <see cref="ControllerWindow.EmbedStealthWindowRemote"/> for the
    /// actual re-parenting/positioning logic.</summary>
    public (bool Success, string? Error) EmbedStealthWindowRemote(string id)
    {
        if (_controller is null) return (false, "Controller not ready");
        var (success, error) = _controller.EmbedStealthWindowRemote(id);
        if (success) NotifyRemoteClientsStateChanged();
        return (success, error);
    }

    /// <summary>Starts the Stream Deck remote control WebSocket server if it isn't already
    /// running. Best-effort: a bind failure (e.g. the port is already in use) is logged, never
    /// thrown — called both at launch (when enabled in settings) and from the Settings tab's
    /// on/off toggle.</summary>
    public void StartRemoteControl()
    {
        if (RemoteControl is not null || _uiQueue is null) return;
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "onAIr", "launch.log");
        try
        {
            RemoteControl = new RemoteControlService(
                ExecuteAction, GetRemoteState, _uiQueue,
                SetRemoteField, LoadScriptRemoteAsync, GetScriptTextRemote, ListFontsRemote,
                ShowInsightRemote, ClearInsightRemote);
            RemoteControl.Start();
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} RemoteControlService started on port {RemoteControlService.Port}\n");
        }
        catch (Exception ex)
        {
            RemoteControl = null;
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} RemoteControlService failed to start: {ex.Message}\n");
        }
    }

    /// <summary>Stops and disposes the Stream Deck remote control server, if running.</summary>
    public void StopRemoteControl()
    {
        RemoteControl?.Dispose();
        RemoteControl = null;
    }

    /// <summary>Starts the Web Remote LAN server if it isn't already running. Best-effort, like
    /// <see cref="StartRemoteControl"/> — but a bind failure here commonly means "the one-time
    /// Windows URL-ACL grant hasn't been made yet" (ErrorCode 5, access denied) rather than a
    /// genuine problem, so that specific case is tracked separately via
    /// <see cref="WebRemoteNeedsElevation"/> to drive the Settings tab's UI instead of just
    /// logging a generic failure. Generates a PIN on first-ever enable if one isn't set yet.</summary>
    public void StartWebRemote()
    {
        if (WebRemote is not null || _uiQueue is null) return;
        WebRemoteNeedsElevation = false;
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "onAIr", "launch.log");

        if (string.IsNullOrEmpty(Config.Current.WebRemote.Pin))
        {
            Config.Current.WebRemote.Pin = WebRemoteService.GeneratePin();
            Config.Save();
        }

        try
        {
            WebRemote = new WebRemoteService(
                ExecuteAction, GetRemoteState, _uiQueue,
                SetRemoteField, LoadScriptRemoteAsync, GetScriptTextRemote, ListFontsRemote,
                ShowInsightRemote, ClearInsightRemote,
                ListStealthWindowsRemote, EmbedStealthWindowRemote,
                () => Config.Current.WebRemote.Pin);
            WebRemote.Start();
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} WebRemoteService started on port {WebRemoteService.Port}\n");
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            WebRemote = null;
            WebRemoteNeedsElevation = true;
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} WebRemoteService needs URL ACL grant: {ex.Message}\n");
        }
        catch (Exception ex)
        {
            WebRemote = null;
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} WebRemoteService failed to start: {ex.Message}\n");
        }
    }

    /// <summary>Stops and disposes the Web Remote server, if running.</summary>
    public void StopWebRemote()
    {
        WebRemote?.Dispose();
        WebRemote = null;
        WebRemoteNeedsElevation = false;
    }

    /// <summary>Runs the one-time elevated URL-ACL grant (shows a UAC prompt) then retries
    /// starting the Web Remote server — called ONLY from the Settings tab's explicit "Grant
    /// Network Access" button, never automatically. Blocking (waits on the elevated netsh
    /// process) — callers must run this off the UI thread. Returns true if the server is running
    /// afterward.</summary>
    public bool TryGrantWebRemoteAccessAndStart()
    {
        if (!WebRemoteService.TryGrantUrlAcl()) return false;
        StartWebRemote();
        return WebRemote is not null;
    }

    // Called on the PRIMARY instance (via Program.OnActivated) when a second
    // launch is redirected here. Bring the Controller forward and, if a .txt was
    // opened, load it — all marshalled onto the UI thread.
    public void OnRedirectedActivation(AppActivationArguments args)
    {
        var queue = _uiQueue;
        if (queue is null) { HandleActivation(args); return; }
        queue.TryEnqueue(() =>
        {
            if (_controller is not null) WindowService.BringToFront(_controller);
            HandleActivation(args);
        });
    }

    private void HandleActivation(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.File &&
            args.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs &&
            fileArgs.Files.FirstOrDefault() is Windows.Storage.StorageFile file)
        {
            _ = _overlay?.ViewModel.LoadScriptAsync(file.Path);
        }
        else if (args.Kind == ExtendedActivationKind.Launch &&
                 args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs)
        {
            // e.g. launched with a .txt path as argument
            var path = launchArgs.Arguments?.Trim('"');
            if (!string.IsNullOrEmpty(path) &&
                path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                System.IO.File.Exists(path))
            {
                _ = _overlay?.ViewModel.LoadScriptAsync(path);
            }
        }
    }
}
