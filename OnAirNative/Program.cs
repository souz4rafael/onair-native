using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace OnAirNative;

/// <summary>
/// Custom entry point (replaces the XAML-generated Main via
/// DISABLE_XAML_GENERATED_MAIN). Enforces a single running instance: a second
/// launch redirects its activation (e.g. an opened .txt) to the first instance
/// and exits, instead of spawning duplicate overlay/tray/hotkey registrations.
/// </summary>
public static class Program
{
    private const string InstanceKey = "onAIr-native-main";

    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
            return 0; // another instance owns the app; we redirected and exit

        Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <returns>true if this launch was redirected to the primary instance (so we should exit).</returns>
    private static bool DecideRedirection()
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var primary = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (primary.IsCurrent)
        {
            // We are the primary instance — handle future redirected activations.
            primary.Activated += OnActivated;
            return false;
        }

        // A primary instance already exists — hand our activation to it and exit.
        RedirectActivationTo(activationArgs, primary);
        return true;
    }

    // RedirectActivationToAsync is async; block until it completes before exiting.
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance primary)
    {
        using var done = new ManualResetEvent(false);
        _ = Task.Run(async () =>
        {
            try { await primary.RedirectActivationToAsync(args); }
            finally { done.Set(); }
        });
        done.WaitOne(5000);
    }

    // Fired on the PRIMARY instance when a second launch redirects to it.
    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        App.Instance?.OnRedirectedActivation(args);
    }
}
