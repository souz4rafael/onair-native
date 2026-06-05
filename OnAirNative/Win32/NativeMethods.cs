using System.Runtime.InteropServices;

namespace OnAirNative.Win32;

/// <summary>
/// Win32 P/Invoke declarations used across the app.
/// Covers: window transparency, click-through, always-on-top,
/// content protection, global hotkeys, and DWM composition.
/// </summary>
internal static partial class NativeMethods
{
    // ── Scroll bar (for embed container) ─────────────────────────────────────

    internal const uint WM_HSCROLL = 0x0114;
    internal const uint WM_VSCROLL = 0x0115;

    internal const int SB_HORZ = 0;
    internal const int SB_VERT = 1;

    internal const int SB_LINEUP       = 0;
    internal const int SB_LINEDOWN     = 1;
    internal const int SB_PAGEUP       = 2;
    internal const int SB_PAGEDOWN     = 3;
    internal const int SB_THUMBPOSITION = 4;
    internal const int SB_THUMBTRACK   = 5;
    internal const int SB_TOP          = 6;
    internal const int SB_BOTTOM       = 7;

    internal const uint SIF_RANGE    = 0x0001;
    internal const uint SIF_PAGE     = 0x0002;
    internal const uint SIF_POS      = 0x0004;
    internal const uint SIF_TRACKPOS = 0x0010;
    internal const uint SIF_ALL      = SIF_RANGE | SIF_PAGE | SIF_POS | SIF_TRACKPOS;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SCROLLINFO
    {
        public uint  cbSize;
        public uint  fMask;
        public int   nMin;
        public int   nMax;
        public uint  nPage;
        public int   nPos;
        public int   nTrackPos;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetScrollInfo")]
    internal static partial int SetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO lpsi, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [LibraryImport("user32.dll", EntryPoint = "GetScrollInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO lpsi);

    // ── Window embedding (SetParent, MoveWindow, etc.) ────────────────────────

    internal const int WS_CHILD      = 0x40000000;
    internal const int WS_VISIBLE    = 0x10000000;
    internal const int WS_CAPTION    = 0x00C00000;
    internal const int WS_SYSMENU    = 0x00080000;
    internal const int WS_THICKFRAME = 0x00040000;
    internal const int WS_MINIMIZEBOX = 0x00020000;
    internal const int WS_MAXIMIZEBOX = 0x00010000;
    internal const int WS_HSCROLL    = 0x00100000;
    internal const int WS_VSCROLL    = 0x00200000;
    internal const int WS_POPUP      = unchecked((int)0x80000000);

    internal const uint WS_EX_APPWINDOW = 0x00040000;

    internal const int  SW_SHOW    = 5;
    internal const uint WM_SIZE    = 0x0005;
    internal const uint WM_CLOSE   = 0x0010;
    internal const uint WM_DESTROY = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SetParent")]
    internal static partial IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [LibraryImport("user32.dll", EntryPoint = "MoveWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveWindow(
        IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ── EnumWindows — window discovery ────────────────────────────────────────

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "EnumWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static partial int GetWindowTextLength(IntPtr hWnd);

    // [DllImport] required because LibraryImport doesn't support StringBuilder
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ── Shell_NotifyIcon (system tray) ────────────────────────────────────────

    internal const uint NIM_ADD     = 0x00000000;
    internal const uint NIM_MODIFY  = 0x00000001;
    internal const uint NIM_DELETE  = 0x00000002;

    internal const uint NIF_MESSAGE  = 0x00000001;
    internal const uint NIF_ICON     = 0x00000002;
    internal const uint NIF_TIP      = 0x00000004;

    // Menu flags for AppendMenu
    internal const uint MF_STRING    = 0x00000000;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_GRAYED    = 0x00000001;

    // TrackPopupMenu flags
    internal const uint TPM_RETURNCMD  = 0x00000100;
    internal const uint TPM_RIGHTBUTTON = 0x00000002;
    internal const uint TPM_BOTTOMALIGN = 0x00000020;

    // Tray notification codes (lParam of tray callback message)
    internal const uint WM_LBUTTONDBLCLK = 0x0203;
    internal const uint WM_RBUTTONUP     = 0x0205;

    // Icon IDs
    internal const int IDI_APPLICATION = 32512;

    // DllImport required for Shell_NotifyIconW (complex NOTIFYICONDATA struct with ByValTStr)
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    internal static partial IntPtr LoadIcon(IntPtr hInstance, int lpIconName);

    // Load icon from file (LR_LOADFROMFILE = 0x10, IMAGE_ICON = 1)
    internal const uint IMAGE_ICON       = 1;
    internal const uint LR_LOADFROMFILE  = 0x00000010;
    internal const uint LR_DEFAULTSIZE   = 0x00000040;

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr LoadImageFromFile(
        IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [LibraryImport("user32.dll", EntryPoint = "CreatePopupMenu")]
    internal static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll", EntryPoint = "TrackPopupMenu")]
    internal static partial int TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [LibraryImport("user32.dll", EntryPoint = "DestroyMenu")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ── GetSystemMetrics ──────────────────────────────────────────────────────

    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    internal static partial uint GetDpiForWindowPublic(IntPtr hwnd);

    // ── GetWindowLong / SetWindowLong indices ─────────────────────────────────

    internal const int GWL_EXSTYLE = -20;
    internal const int GWL_STYLE   = -16;

    // ── Extended window style flags ───────────────────────────────────────────

    internal const int WS_EX_LAYERED    = 0x00080000;
    internal const int WS_EX_TRANSPARENT = 0x00000020;  // click-through
    internal const int WS_EX_NOACTIVATE  = 0x08000000;  // never steal focus
    internal const int WS_EX_TOOLWINDOW  = 0x00000080;  // hide from Alt+Tab

    // ── SetLayeredWindowAttributes ────────────────────────────────────────────

    internal const uint LWA_ALPHA    = 0x00000002;
    internal const uint LWA_COLORKEY = 0x00000001;

    // ── SetWindowPos ──────────────────────────────────────────────────────────

    internal static readonly IntPtr HWND_TOPMOST   = new(-1);
    internal static readonly IntPtr HWND_NOTOPMOST = new(-2);
    internal static readonly IntPtr HWND_MESSAGE   = new(-3);  // message-only parent

    internal const uint SWP_NOMOVE    = 0x0002;
    internal const uint SWP_NOSIZE    = 0x0001;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    // ── WM_ messages ─────────────────────────────────────────────────────────

    internal const uint WM_HOTKEY = 0x0312;
    internal const uint WM_QUIT   = 0x0012;

    // ── RegisterHotKey modifier flags ─────────────────────────────────────────

    internal const uint MOD_ALT      = 0x0001;
    internal const uint MOD_CONTROL  = 0x0002;
    internal const uint MOD_SHIFT    = 0x0004;
    internal const uint MOD_WIN      = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    // ── Virtual key codes ────────────────────────────────────────────────────

    internal const uint VK_PRIOR  = 0x21;  // Page Up
    internal const uint VK_NEXT   = 0x22;  // Page Down
    internal const uint VK_HOME   = 0x24;
    internal const uint VK_INSERT = 0x2D;
    internal const uint VK_M      = 0x4D;
    internal const uint VK_O      = 0x4F;
    internal const uint VK_R      = 0x52;

    // ── SetWindowDisplayAffinity ──────────────────────────────────────────────

    internal const uint WDA_NONE               = 0x00000000;
    internal const uint WDA_MONITOR            = 0x00000001;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;  // Windows 10 2004+

    // ── P/Invoke declarations ─────────────────────────────────────────────────

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    internal static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(
        IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(
        IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    // ── Message loop helpers (for HotkeyService background thread) ───────────

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMessage(
        out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial IntPtr DispatchMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(
        uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    // Window class + creation for the message-only hotkey receiver
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(in WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr DefWindowProcW(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr GetModuleHandleW(string? lpModuleName);

    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public uint   cbSize;
        public IntPtr hWnd;
        public uint   uID;
        public uint   uFlags;
        public uint   uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint   dwState;
        public uint   dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint   uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint   dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left, top, right, bottom;
        public int Width  => right - left;
        public int Height => bottom - top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;

        /// <summary>Extends DWM frame to cover the entire window — makes background transparent.</summary>
        public static MARGINS FullSheet => new() { cxLeftWidth = -1 };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public POINT  pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint   cbSize;
        public uint   style;
        public IntPtr lpfnWndProc;
        public int    cbClsExtra;
        public int    cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;  // points to unmanaged string
        public IntPtr hIconSm;
    }
}
