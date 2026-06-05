using System.Runtime.InteropServices;
using OnAirNative.Win32;

namespace OnAirNative.Services;

/// <summary>
/// Embeds a window owned by another process inside a stealth container that has
/// WDA_EXCLUDEFROMCAPTURE, hiding the content from screen captures while allowing
/// full interaction with the embedded app.
///
/// The container:
///   • Has a title bar + close button (WS_CAPTION | WS_SYSMENU)
///   • Is resizable via border handles (WS_THICKFRAME)
///   • Is always-on-top and hidden from screen capture
///   • Resizes the embedded window to fill its client area on WM_SIZE
/// </summary>
public sealed class WindowEmbedService : IDisposable
{
    private IntPtr _containerHwnd;
    private IntPtr _targetHwnd;
    private int               _savedStyle;
    private int               _savedExStyle;
    private NativeMethods.RECT _savedRect;

    public bool IsEmbedding   => _containerHwnd != IntPtr.Zero;
    public string TargetTitle { get; private set; } = "";

    private NativeWndProc? _wndProcDelegate;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr NativeWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public bool Embed(IntPtr targetHwnd, string targetTitle, int x, int y, int width, int height)
    {
        if (IsEmbedding) Release();

        _targetHwnd  = targetHwnd;
        TargetTitle  = targetTitle;

        // Save original state
        _savedStyle   = NativeMethods.GetWindowLong(targetHwnd, NativeMethods.GWL_STYLE);
        _savedExStyle = NativeMethods.GetWindowLong(targetHwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.GetWindowRect(targetHwnd, out _savedRect);

        // Create plain Win32 container
        _wndProcDelegate = ContainerWndProc;
        var wndProcPtr   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        var hInst        = NativeMethods.GetModuleHandleW(null);
        var className    = $"OnAirEmbed_{Environment.ProcessId}";
        var classNamePtr = Marshal.StringToHGlobalUni(className);

        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc   = wndProcPtr,
            hInstance     = hInst,
            lpszClassName = classNamePtr,
            hbrBackground = IntPtr.Zero,
        };
        NativeMethods.RegisterClassExW(in wc);

        uint containerStyle = (uint)(
            NativeMethods.WS_VISIBLE    |
            NativeMethods.WS_CAPTION    |   // title bar + close button
            NativeMethods.WS_SYSMENU    |   // system menu / Alt+Space
            NativeMethods.WS_THICKFRAME);   // resizable borders

        _containerHwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WS_EX_TOOLWINDOW,
            className,
            $"onAIr — {targetTitle}",
            containerStyle,
            x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        Marshal.FreeHGlobal(classNamePtr);

        if (_containerHwnd == IntPtr.Zero)
        {
            _targetHwnd = IntPtr.Zero;
            return false;
        }

        // Stealth + always-on-top
        NativeMethods.SetWindowDisplayAffinity(_containerHwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        NativeMethods.SetWindowPos(_containerHwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        // Strip chrome from target — container provides chrome
        int newStyle = _savedStyle
            & ~(NativeMethods.WS_CAPTION     |
                NativeMethods.WS_THICKFRAME  |
                NativeMethods.WS_SYSMENU     |
                NativeMethods.WS_MINIMIZEBOX |
                NativeMethods.WS_MAXIMIZEBOX);
        NativeMethods.SetWindowLong(targetHwnd, NativeMethods.GWL_STYLE, newStyle);

        // Re-parent into container, fill client area
        NativeMethods.SetParent(targetHwnd, _containerHwnd);
        NativeMethods.GetClientRect(_containerHwnd, out var client);
        NativeMethods.MoveWindow(targetHwnd, 0, 0, client.Width, client.Height, true);
        NativeMethods.ShowWindow(targetHwnd, NativeMethods.SW_SHOW);

        return true;
    }

    public void Release()
    {
        if (!IsEmbedding) return;
        try { ReleaseInternal(); }
        catch { }
    }

    private void ReleaseInternal()
    {
        if (_targetHwnd != IntPtr.Zero)
        {
            try
            {
                NativeMethods.SetParent(_targetHwnd, IntPtr.Zero);
                NativeMethods.SetWindowLong(_targetHwnd, NativeMethods.GWL_STYLE,   _savedStyle);
                NativeMethods.SetWindowLong(_targetHwnd, NativeMethods.GWL_EXSTYLE, _savedExStyle);
                NativeMethods.SetWindowPos(_targetHwnd, IntPtr.Zero,
                    _savedRect.left, _savedRect.top,
                    _savedRect.Width, _savedRect.Height,
                    NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
            }
            catch { /* target process may have exited */ }
            _targetHwnd = IntPtr.Zero;
        }
        if (_containerHwnd != IntPtr.Zero)
        {
            try { NativeMethods.DestroyWindow(_containerHwnd); }
            catch { }
            _containerHwnd = IntPtr.Zero;
        }
        TargetTitle = "";
    }

    private IntPtr ContainerWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == NativeMethods.WM_SIZE && _targetHwnd != IntPtr.Zero)
            {
                NativeMethods.GetClientRect(hWnd, out var rc);
                NativeMethods.MoveWindow(_targetHwnd, 0, 0, rc.Width, rc.Height, true);
            }
            else if (msg == NativeMethods.WM_CLOSE)
            {
                ReleaseInternal();
                return IntPtr.Zero;
            }
            else if (msg == NativeMethods.WM_DESTROY)
            {
                // Target process may have exited — clean up silently
                _targetHwnd    = IntPtr.Zero;
                _containerHwnd = IntPtr.Zero;
            }
        }
        catch
        {
            // Never let exceptions escape a WndProc — they crash the CLR
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose() => Release();
}
