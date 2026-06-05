using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OnAirNative.Win32;
using WinRT.Interop;

namespace OnAirNative.Services;

/// <summary>
/// Centralises Win32 window manipulation used by both the Overlay and Controller.
/// All methods are static and accept an HWND so they work from any thread context.
/// </summary>
public static partial class WindowService
{
    // ── HWND helpers ──────────────────────────────────────────────────────────

    public static IntPtr GetHwnd(Window window) =>
        WindowNative.GetWindowHandle(window);

    public static AppWindow GetAppWindow(Window window) =>
        window.AppWindow;

    // ── Show / Hide ────────────────────────────────────────────────────────────

    public static void ShowWindow(Window window) => window.AppWindow.Show(activateWindow: true);
    public static void HideWindow(Window window) => window.AppWindow.Hide();
    public static bool IsVisible(Window window)  => window.AppWindow.IsVisible;

    // ── Transparency & frame ─────────────────────────────────────────────────

    /// <summary>
    /// Makes the entire window background transparent via DWM composition.
    /// Must be called after the window HWND is valid (i.e. after Activate).
    /// </summary>
    public static void MakeTransparent(IntPtr hwnd)
    {
        // WS_EX_LAYERED is required for opacity / transparency operations
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_LAYERED);

        // Extend the DWM frame over the whole client area → true transparency
        var margins = NativeMethods.MARGINS.FullSheet;
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>Sets overlay opacity (0 = invisible, 255 = fully opaque).</summary>
    public static void SetOpacity(IntPtr hwnd, byte opacity) =>
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, opacity, NativeMethods.LWA_ALPHA);

    /// <summary>
    /// Removes the title bar and makes window content extend to all edges.
    /// Uses the new AppWindowTitleBar API (Windows App SDK 1.3+).
    /// </summary>
    public static void RemoveTitleBar(Window window)
    {
        var tb = window.AppWindow.TitleBar;
        tb.ExtendsContentIntoTitleBar = true;
        if (AppWindowTitleBar.IsCustomizationSupported())
            tb.PreferredHeightOption = TitleBarHeightOption.Collapsed;
    }

    // ── Always-on-top ─────────────────────────────────────────────────────────

    public static void SetAlwaysOnTop(IntPtr hwnd, bool enable)
    {
        var insertAfter = enable ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;
        NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // ── Click-through (move mode toggle) ─────────────────────────────────────

    /// <summary>
    /// When clickThrough=true the overlay is invisible to mouse/keyboard;
    /// input passes straight to whatever is underneath (screen share content).
    /// When false, the overlay is interactive (drag/resize — "Move Mode").
    /// </summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle = clickThrough
            ? exStyle |  NativeMethods.WS_EX_TRANSPARENT
            : exStyle & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    // ── Screen-share protection ───────────────────────────────────────────────

    /// <summary>
    /// Hides or reveals the window in screen captures / Teams / OBS.
    /// Uses SetWindowDisplayAffinity (WDA_EXCLUDEFROMCAPTURE — Windows 10 2004+).
    /// </summary>
    public static void SetContentProtection(IntPtr hwnd, bool protect)
    {
        var affinity = protect
            ? NativeMethods.WDA_EXCLUDEFROMCAPTURE
            : NativeMethods.WDA_NONE;
        NativeMethods.SetWindowDisplayAffinity(hwnd, affinity);
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    public static void SetPosition(Window window, double x, double y)
    {
        var scale = GetScaleFactor(window);
        window.AppWindow.Move(new((int)(x * scale), (int)(y * scale)));
    }

    public static void SetSize(Window window, double width, double height)
    {
        var scale = GetScaleFactor(window);
        window.AppWindow.Resize(new((int)(width * scale), (int)(height * scale)));
    }

    public static (double X, double Y, double Width, double Height) GetGeometry(Window window)
    {
        var scale = GetScaleFactor(window);
        var pos   = window.AppWindow.Position;
        var size  = window.AppWindow.Size;
        return (pos.X / scale, pos.Y / scale, size.Width / scale, size.Height / scale);
    }

    private static double GetScaleFactor(Window window)
    {
        var hwnd = GetHwnd(window);
        uint dpi  = GetDpiForWindow(hwnd);
        return dpi / 96.0;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);
}
