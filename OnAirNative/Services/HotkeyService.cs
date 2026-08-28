using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using OnAirNative.Win32;

namespace OnAirNative.Services;

public enum HotkeyAction
{
    ScrollUp,
    ScrollDown,
    ToggleMoveMode,
    OpenFile,
    ToggleRecording,
    IncreaseOpacity,
    DecreaseOpacity,
    ReleaseStealthContainer,
    ToggleOverlayVisibility,
    ToggleOverlayCaptureProtection,
    ToggleControllerCaptureProtection,
    IncreaseScrollSpeed,
    DecreaseScrollSpeed,
    IncreaseFontSize,
    DecreaseFontSize,
}

/// <summary>
/// Registers Win32 global hotkeys on a dedicated background thread with its own
/// message loop. Dispatches <see cref="HotkeyTriggered"/> back to the UI thread
/// via the <see cref="DispatcherQueue"/> provided at construction.
///
/// Hotkeys:
///   Ctrl+Alt+PgUp  → ScrollUp
///   Ctrl+Alt+PgDn  → ScrollDown
///   Ctrl+Alt+Home  → ToggleMoveMode (overlay locked/unlocked)
///   Ctrl+Alt+O     → OpenFile
///   Ctrl+Alt+R     → ToggleRecording
///   Ctrl+Alt+]     → IncreaseOpacity
///   Ctrl+Alt+[     → DecreaseOpacity
///   Ctrl+Alt+U     → ReleaseStealthContainer
///   Ctrl+Alt+V     → ToggleOverlayVisibility (show/hide the overlay window)
///   Ctrl+Alt+S     → ToggleOverlayCaptureProtection (overlay visible/hidden in share)
///   Ctrl+Alt+H     → ToggleControllerCaptureProtection (hide controller from capture)
///   Ctrl+Alt+.     → IncreaseScrollSpeed (Auto-scroll speed)
///   Ctrl+Alt+,     → DecreaseScrollSpeed (Auto-scroll speed)
///   Ctrl+Alt+=     → IncreaseFontSize
///   Ctrl+Alt+-     → DecreaseFontSize
/// </summary>
public sealed class HotkeyService : IDisposable
{
    // Hotkey IDs — arbitrary unique integers per application.
    // Must stay contiguous from ID_SCROLL_UP to the last one: the cleanup loop
    // unregisters the whole range by iterating this span.
    private const int ID_SCROLL_UP                       = 1;
    private const int ID_SCROLL_DOWN                     = 2;
    private const int ID_MOVE_MODE                       = 3;
    private const int ID_OPEN_FILE                       = 4;
    private const int ID_RECORD                          = 5;
    private const int ID_OPACITY_UP                      = 6;
    private const int ID_OPACITY_DOWN                    = 7;
    private const int ID_RELEASE_STEALTH                 = 8;
    private const int ID_OVERLAY_VISIBILITY              = 9;
    private const int ID_OVERLAY_CAPTURE_PROTECTION      = 10;
    private const int ID_CONTROLLER_CAPTURE_PROTECTION   = 11;
    private const int ID_SCROLL_SPEED_UP                 = 12;
    private const int ID_SCROLL_SPEED_DOWN               = 13;
    private const int ID_FONT_SIZE_UP                    = 14;
    private const int ID_FONT_SIZE_DOWN                  = 15;

    private readonly DispatcherQueue _uiQueue;
    private Thread?  _thread;
    private uint     _threadId;
    private IntPtr   _hwnd = IntPtr.Zero;
    private bool     _disposed;

    // Keep the WndProc delegate alive — GC must not collect it while the thread runs
    private NativeWndProc? _wndProcDelegate;

    public event EventHandler<HotkeyAction>? HotkeyTriggered;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr NativeWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public HotkeyService(DispatcherQueue uiQueue) => _uiQueue = uiQueue;

    public void Start()
    {
        _thread = new Thread(HotkeyLoop) { IsBackground = true, Name = "HotkeyMsgLoop" };
        _thread.Start();
    }

    private void HotkeyLoop()
    {
        _threadId = NativeMethods.GetCurrentThreadId();

        // Register a tiny custom window class pointing to our WndProc
        _wndProcDelegate = WndProc;
        var wndProcPtr   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        var hInst        = NativeMethods.GetModuleHandleW(null);
        var className    = $"OnAirHotkeyWnd_{Environment.ProcessId}";
        var classNamePtr = Marshal.StringToHGlobalUni(className);

        try
        {
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc   = wndProcPtr,
                hInstance     = hInst,
                lpszClassName = classNamePtr,
            };
            NativeMethods.RegisterClassExW(in wc);

            // Create a message-only window (parent = HWND_MESSAGE)
            _hwnd = NativeMethods.CreateWindowExW(
                0, className, className, 0, 0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, hInst, IntPtr.Zero);

            // Register all hotkeys
            Register(ID_SCROLL_UP,                   NativeMethods.VK_PRIOR);
            Register(ID_SCROLL_DOWN,                  NativeMethods.VK_NEXT);
            Register(ID_MOVE_MODE,                    NativeMethods.VK_HOME);
            Register(ID_OPEN_FILE,                    NativeMethods.VK_O);
            Register(ID_RECORD,                       NativeMethods.VK_R);
            Register(ID_OPACITY_UP,                   NativeMethods.VK_OEM_6);
            Register(ID_OPACITY_DOWN,                 NativeMethods.VK_OEM_4);
            Register(ID_RELEASE_STEALTH,              NativeMethods.VK_U);
            Register(ID_OVERLAY_VISIBILITY,           NativeMethods.VK_V);
            Register(ID_OVERLAY_CAPTURE_PROTECTION,   NativeMethods.VK_S);
            Register(ID_CONTROLLER_CAPTURE_PROTECTION, NativeMethods.VK_H);
            Register(ID_SCROLL_SPEED_UP,              NativeMethods.VK_OEM_PERIOD);
            Register(ID_SCROLL_SPEED_DOWN,            NativeMethods.VK_OEM_COMMA);
            Register(ID_FONT_SIZE_UP,                 NativeMethods.VK_OEM_PLUS);
            Register(ID_FONT_SIZE_DOWN,               NativeMethods.VK_OEM_MINUS);

            // Pump messages until WM_QUIT
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        finally
        {
            // Cleanup hotkeys and window
            for (int id = ID_SCROLL_UP; id <= ID_FONT_SIZE_DOWN; id++)
                NativeMethods.UnregisterHotKey(_hwnd, id);

            if (_hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(_hwnd);
            Marshal.FreeHGlobal(classNamePtr);
        }
    }

    private void Register(int id, uint vk) =>
        NativeMethods.RegisterHotKey(_hwnd, id,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, vk);

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                var action = (int)wParam switch
                {
                    ID_SCROLL_UP                        => (HotkeyAction?)HotkeyAction.ScrollUp,
                    ID_SCROLL_DOWN                       => HotkeyAction.ScrollDown,
                    ID_MOVE_MODE                          => HotkeyAction.ToggleMoveMode,
                    ID_OPEN_FILE                          => HotkeyAction.OpenFile,
                    ID_RECORD                             => HotkeyAction.ToggleRecording,
                    ID_OPACITY_UP                         => HotkeyAction.IncreaseOpacity,
                    ID_OPACITY_DOWN                       => HotkeyAction.DecreaseOpacity,
                    ID_RELEASE_STEALTH                    => HotkeyAction.ReleaseStealthContainer,
                    ID_OVERLAY_VISIBILITY                 => HotkeyAction.ToggleOverlayVisibility,
                    ID_OVERLAY_CAPTURE_PROTECTION         => HotkeyAction.ToggleOverlayCaptureProtection,
                    ID_CONTROLLER_CAPTURE_PROTECTION      => HotkeyAction.ToggleControllerCaptureProtection,
                    ID_SCROLL_SPEED_UP                    => HotkeyAction.IncreaseScrollSpeed,
                    ID_SCROLL_SPEED_DOWN                  => HotkeyAction.DecreaseScrollSpeed,
                    ID_FONT_SIZE_UP                       => HotkeyAction.IncreaseFontSize,
                    ID_FONT_SIZE_DOWN                     => HotkeyAction.DecreaseFontSize,
                    _                                     => null,
                };
                if (action.HasValue)
                {
                    var a = action.Value;
                    _uiQueue.TryEnqueue(() => HotkeyTriggered?.Invoke(this, a));
                }
            }
        }
        catch { /* never let exceptions escape WndProc */ }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0)
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
