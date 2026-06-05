using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using OnAirNative.Services;
using OnAirNative.Views;

namespace OnAirNative;

public partial class App : Application
{
    // Singleton services — consumed by ViewModels and Views
    public static ConfigService     Config     { get; private set; } = null!;
    public static AudioService      Audio      { get; private set; } = null!;
    public static WhisperService    Whisper    { get; private set; } = null!;
    public static AiChatService     AiChat     { get; private set; } = null!;
    public static HotkeyService     Hotkeys    { get; private set; } = null!;
    public static TrayService       Tray       { get; private set; } = null!;

    private OverlayWindow?    _overlay;
    private ControllerWindow? _controller;

    public App()
    {
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
                "onAIr Native", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, $"{DateTime.Now}\n{ex}\n");
            throw;
        }
    }

    private void LaunchCore(LaunchActivatedEventArgs args)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "onAIr Native");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "launch.log");
        File.AppendAllText(logPath, $"\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} === Launch start ===\n");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} UnhandledException: {e.ExceptionObject}\n");

        // Init services
        Config  = new ConfigService();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} ConfigService OK\n");
        Audio   = new AudioService();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} AudioService OK\n");
        Whisper = new WhisperService();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} WhisperService OK\n");
        AiChat  = new AiChatService();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} AiChatService OK\n");

        // Create and show windows
        _overlay    = new OverlayWindow();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} OverlayWindow created\n");
        _controller = new ControllerWindow();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} ControllerWindow created\n");

        // Wire overlay → controller reference for cross-window commands
        _overlay.Controller = _controller;
        _controller.Overlay = _overlay;

        _overlay.Activate();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} Overlay activated\n");
        // Hide overlay immediately — user shows it from Controller when ready
        _overlay.AppWindow.Hide();
        _controller.Activate();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} Controller activated\n");

        // InitViewModel is called from ControllerWindow.OnFirstActivated (after _hwnd is valid)

        // Global hotkeys — start after windows are created so HWNDs are valid
        Hotkeys = new HotkeyService(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        Hotkeys.HotkeyTriggered += OnHotkeyTriggered;
        Hotkeys.Start();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} HotkeyService started\n");

        // System tray icon
        var uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Tray = new TrayService(uiQueue);
        Tray.ShowOverlayClicked   += (_, _) => { WindowService.ShowWindow(_overlay!); _controller?.SyncOverlayToggle(true); };
        Tray.HideOverlayClicked   += (_, _) => { WindowService.HideWindow(_overlay!); _controller?.SyncOverlayToggle(false); };
        Tray.LoadScriptClicked    += (_, _) => _ = _overlay?.ViewModel.OpenFilePickerAsync(_overlay!);
        Tray.ShowControllerClicked += (_, _) => _controller?.Activate();
        Tray.QuitClicked          += (_, _) => _controller?.Close();
        Tray.Start();
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} TrayService started\n");

        // Handle .txt file opened via right-click → "Open with onAIr Native"
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        HandleActivation(activationArgs);

        // Closing the Controller = quit the entire app
        _controller.Closed += (_, _) =>
        {
            _overlay?.SaveGeometry();
            Config.Save();
            Hotkeys.Dispose();
            Tray.Dispose();
            Audio.Dispose();
            Whisper.Dispose();
            AiChat.Dispose();
            Exit();
        };
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} LaunchCore done\n");
    }

    private void OnHotkeyTriggered(object? sender, HotkeyAction action)
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
            case HotkeyAction.SwitchMode:
                _overlay?.ViewModel.CycleMode();
                break;
            case HotkeyAction.OpenController:
                _controller?.Activate();
                break;
        }
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
