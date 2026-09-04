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
    IncreaseVoiceScrollSpeed,
    DecreaseVoiceScrollSpeed,
    IncreaseScrollStep,
    DecreaseScrollStep,
    IncreaseVoiceThreshold,
    DecreaseVoiceThreshold,
    /// <summary>No physical hotkey registered for this one — only reachable via the WHISPER
    /// MODEL card's "🔄 Recheck" button or the Stream Deck plugin's AI Status tile press
    /// (through <see cref="RemoteControlService"/>). Included in this enum anyway since it's
    /// the shared command vocabulary for both physical hotkeys and remote control.</summary>
    RecheckWhisperModel,
    ToggleInsightsVisibility,
    ToggleInsightsLock,
    ToggleInsightsCaptureProtection,
    /// <summary>No physical hotkey registered for any of these four — only reachable via the
    /// Controller's own buttons (Q&amp;A tab's Conversation Memory / Q&amp;A Session Recording /
    /// Settings → USAGE cards) or remote control (Web Remote / Stream Deck / MCP). Grouped here
    /// since they were all added together for Web Remote Q&amp;A-tab parity — see
    /// <see cref="WebRemoteService"/>'s doc comment.</summary>
    ClearConversation,
    /// <summary>Starts a brand-new Q&amp;A session with no label (equivalent to leaving the
    /// Controller's optional "Session label" box empty) — see
    /// <c>OverlayViewModel.StartNewQaSession</c>.</summary>
    StartNewQaSession,
    CloseQaSession,
    ResetUsage,
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
///   Ctrl+Alt+Up    → IncreaseVoiceScrollSpeed
///   Ctrl+Alt+Down  → DecreaseVoiceScrollSpeed
///   Ctrl+Alt+Right → IncreaseScrollStep (Manual mode)
///   Ctrl+Alt+Left  → DecreaseScrollStep (Manual mode)
///   Ctrl+Alt+'     → IncreaseVoiceThreshold (Voice scroll sensitivity)
///   Ctrl+Alt+;     → DecreaseVoiceThreshold (Voice scroll sensitivity)
///   Ctrl+Alt+I     → ToggleInsightsVisibility (show/hide the AI Insights window)
///   Ctrl+Alt+L     → ToggleInsightsLock (lock/unlock the AI Insights window)
///   Ctrl+Alt+P     → ToggleInsightsCaptureProtection (AI Insights visible/hidden in share)
///
/// Note: Ctrl+Alt+Arrow is, on some machines, also bound by legacy Intel/NVIDIA graphics
/// driver control panels to rotate the display. If that binding claims the combo first,
/// RegisterHotKey here simply fails (harmlessly — the return value is ignored, nothing
/// throws) so onAIr's arrow hotkeys would silently not fire rather than fight over it;
/// hasn't been observed in testing, but worth knowing if a user reports Ctrl+Alt+Up/Down/Left/Right
/// doing nothing.
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
    private const int ID_VOICE_SPEED_UP                  = 16;
    private const int ID_VOICE_SPEED_DOWN                = 17;
    private const int ID_SCROLL_STEP_UP                  = 18;
    private const int ID_SCROLL_STEP_DOWN                = 19;
    private const int ID_VOICE_THRESHOLD_UP              = 20;
    private const int ID_VOICE_THRESHOLD_DOWN            = 21;
    private const int ID_INSIGHTS_VISIBILITY             = 22;
    private const int ID_INSIGHTS_LOCK                   = 23;
    private const int ID_INSIGHTS_CAPTURE_PROTECTION     = 24;

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
            Register(ID_VOICE_SPEED_UP,               NativeMethods.VK_UP);
            Register(ID_VOICE_SPEED_DOWN,             NativeMethods.VK_DOWN);
            Register(ID_SCROLL_STEP_UP,               NativeMethods.VK_RIGHT);
            Register(ID_SCROLL_STEP_DOWN,             NativeMethods.VK_LEFT);
            Register(ID_VOICE_THRESHOLD_UP,           NativeMethods.VK_OEM_7);
            Register(ID_VOICE_THRESHOLD_DOWN,         NativeMethods.VK_OEM_1);
            Register(ID_INSIGHTS_VISIBILITY,          NativeMethods.VK_I);
            Register(ID_INSIGHTS_LOCK,                NativeMethods.VK_L);
            Register(ID_INSIGHTS_CAPTURE_PROTECTION,  NativeMethods.VK_P);

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
            for (int id = ID_SCROLL_UP; id <= ID_INSIGHTS_CAPTURE_PROTECTION; id++)
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
                    ID_VOICE_SPEED_UP                     => HotkeyAction.IncreaseVoiceScrollSpeed,
                    ID_VOICE_SPEED_DOWN                   => HotkeyAction.DecreaseVoiceScrollSpeed,
                    ID_SCROLL_STEP_UP                     => HotkeyAction.IncreaseScrollStep,
                    ID_SCROLL_STEP_DOWN                   => HotkeyAction.DecreaseScrollStep,
                    ID_VOICE_THRESHOLD_UP                 => HotkeyAction.IncreaseVoiceThreshold,
                    ID_VOICE_THRESHOLD_DOWN               => HotkeyAction.DecreaseVoiceThreshold,
                    ID_INSIGHTS_VISIBILITY                 => HotkeyAction.ToggleInsightsVisibility,
                    ID_INSIGHTS_LOCK                       => HotkeyAction.ToggleInsightsLock,
                    ID_INSIGHTS_CAPTURE_PROTECTION         => HotkeyAction.ToggleInsightsCaptureProtection,
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
