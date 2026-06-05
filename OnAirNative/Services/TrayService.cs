using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using OnAirNative.Win32;

namespace OnAirNative.Services;

/// <summary>
/// Manages the system-tray icon via Win32 Shell_NotifyIcon.
/// Runs on a dedicated background thread (own message loop).
/// All event callbacks are dispatched to the UI thread.
/// </summary>
public sealed class TrayService : IDisposable
{
    // ── Menu item IDs ─────────────────────────────────────────────────────────

    private const uint MENU_SHOW_OVERLAY  = 101;
    private const uint MENU_HIDE_OVERLAY  = 102;
    private const uint MENU_LOAD_SCRIPT   = 103;
    private const uint MENU_CONTROLLER    = 104;
    private const uint MENU_SEPARATOR     = 0;
    private const uint MENU_QUIT          = 199;

    // ── Tray callback message (WM_USER + 1) ───────────────────────────────────
    private const uint WM_TRAY = 0x0401;

    // ── Public events (raised on UI thread) ───────────────────────────────────

    public event EventHandler? ShowOverlayClicked;
    public event EventHandler? HideOverlayClicked;
    public event EventHandler? LoadScriptClicked;
    public event EventHandler? ShowControllerClicked;
    public event EventHandler? QuitClicked;

    // ── Private state ─────────────────────────────────────────────────────────

    private readonly DispatcherQueue _uiQueue;
    private Thread?  _thread;
    private uint     _threadId;
    private IntPtr   _hwnd;
    private bool     _disposed;

    // Keep delegate alive — GC must not collect while thread is running
    private NativeWndProc? _wndProcDelegate;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr NativeWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public TrayService(DispatcherQueue uiQueue) => _uiQueue = uiQueue;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        _thread = new Thread(TrayLoop) { IsBackground = true, Name = "TrayMsgLoop" };
        _thread.Start();
    }

    private void TrayLoop()
    {
        _threadId = NativeMethods.GetCurrentThreadId();

        _wndProcDelegate = WndProc;
        var wndProcPtr   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        var hInst        = NativeMethods.GetModuleHandleW(null);
        var className    = $"OnAirTrayWnd_{Environment.ProcessId}";
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

            _hwnd = NativeMethods.CreateWindowExW(
                0, className, className, 0, 0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, hInst, IntPtr.Zero);

            AddTrayIcon();

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        finally
        {
            RemoveTrayIcon();
            if (_hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(_hwnd);
            Marshal.FreeHGlobal(classNamePtr);
        }
    }

    // ── Tray icon management ──────────────────────────────────────────────────

    private void AddTrayIcon()
    {
        // Try to load the custom .ico from the same folder as the .exe
        var exeDir  = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var icoPath = Path.Combine(exeDir, "Assets", "app-icon.ico");

        IntPtr hIcon;
        if (File.Exists(icoPath))
        {
            hIcon = NativeMethods.LoadImageFromFile(
                IntPtr.Zero, icoPath,
                NativeMethods.IMAGE_ICON, 0, 0,
                NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
        }
        else
        {
            // Fallback to system application icon
            hIcon = NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
        }

        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize           = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd             = _hwnd,
            uID              = 1,
            uFlags           = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon            = hIcon,
            szTip            = "onAIr Native",
            szInfo           = "",
            szInfoTitle      = "",
        };

        bool ok = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);

        // Log success/failure for diagnostics
        var logDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "onAIr Native");
        Directory.CreateDirectory(logDir);
        File.AppendAllText(Path.Combine(logDir, "tray.log"),
            $"{DateTime.Now:HH:mm:ss} Shell_NotifyIcon={ok}, hIcon={hIcon}, ico={icoPath}, exists={File.Exists(icoPath)}\n");
    }

    private void RemoveTrayIcon()
    {
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd   = _hwnd,
            uID    = 1,
            szTip  = "",
            szInfo = "",
            szInfoTitle = "",
        };
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
    }

    // ── WndProc ───────────────────────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAY)
            {
                uint notification = (uint)(lParam.ToInt64() & 0xFFFF);

                if (notification == NativeMethods.WM_LBUTTONDBLCLK)
                    _uiQueue.TryEnqueue(() => ShowControllerClicked?.Invoke(this, EventArgs.Empty));
                else if (notification == NativeMethods.WM_RBUTTONUP)
                    ShowContextMenu(hWnd);
            }
        }
        catch { /* never let exceptions escape WndProc */ }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private void ShowContextMenu(IntPtr hWnd)
    {
        var menu = NativeMethods.CreatePopupMenu();

        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MENU_SHOW_OVERLAY, "👁  Show overlay");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MENU_HIDE_OVERLAY, "🫥  Hide overlay");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, MENU_SEPARATOR, null);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MENU_LOAD_SCRIPT,  "📄  Load script (.txt)…");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MENU_CONTROLLER,   "🎛  Show controller");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, MENU_SEPARATOR, null);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MENU_QUIT,         "✕  Quit onAIr Native");

        // Required so the menu dismisses when you click outside it
        NativeMethods.SetForegroundWindow(hWnd);

        NativeMethods.GetCursorPos(out var pt);

        // TPM_RETURNCMD returns the selected ID directly (no WM_COMMAND)
        int selected = NativeMethods.TrackPopupMenu(
            menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_BOTTOMALIGN,
            pt.x, pt.y, 0, hWnd, IntPtr.Zero);

        NativeMethods.DestroyMenu(menu);

        if (selected > 0)
        {
            _uiQueue.TryEnqueue(() =>
            {
                switch ((uint)selected)
                {
                    case MENU_SHOW_OVERLAY:  ShowOverlayClicked?.Invoke(this, EventArgs.Empty);  break;
                    case MENU_HIDE_OVERLAY:  HideOverlayClicked?.Invoke(this, EventArgs.Empty);  break;
                    case MENU_LOAD_SCRIPT:   LoadScriptClicked?.Invoke(this, EventArgs.Empty);   break;
                    case MENU_CONTROLLER:    ShowControllerClicked?.Invoke(this, EventArgs.Empty); break;
                    case MENU_QUIT:          QuitClicked?.Invoke(this, EventArgs.Empty);          break;
                }
            });
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0)
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
