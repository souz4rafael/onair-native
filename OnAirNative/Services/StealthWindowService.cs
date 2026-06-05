using System.Diagnostics;
using OnAirNative.Win32;

namespace OnAirNative.Services;

/// <summary>
/// Enumerates visible top-level windows and manages per-window stealth mode
/// (SetWindowDisplayAffinity / WDA_EXCLUDEFROMCAPTURE).
///
/// Note: WDA_EXCLUDEFROMCAPTURE can only be applied to windows owned by the
/// calling process without elevation. For windows owned by other processes the
/// call may fail with ACCESS_DENIED. The Result field reports success or failure.
/// </summary>
public static class StealthWindowService
{
    // ── Window info ────────────────────────────────────────────────────────────

    public sealed class WindowInfo
    {
        public IntPtr Handle      { get; init; }
        public string Title       { get; init; } = "";
        public string ProcessName { get; init; } = "";
        public uint   ProcessId   { get; init; }
        public bool   StealthOn   { get; set; }
        public string StatusText  { get; set; } = "";

        public override string ToString() =>
            string.IsNullOrEmpty(Title)
                ? $"[{ProcessName}] (no title)"
                : $"{Title}  [{ProcessName}]";
    }

    // ── Enumerate windows ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns all visible, named top-level windows, excluding the current process.
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var result    = new List<WindowInfo>();
        var currentPid = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;

            // Get title
            int len = NativeMethods.GetWindowTextLength(hWnd);
            if (len == 0) return true; // skip untitled

            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();

            // Get owning process
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == currentPid) return true; // skip our own windows

            string procName = "";
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { /* process may have exited */ }

            result.Add(new WindowInfo
            {
                Handle      = hWnd,
                Title       = title,
                ProcessName = procName,
                ProcessId   = pid,
            });

            return true; // continue enumeration
        }, IntPtr.Zero);

        return result
            .OrderBy(w => w.ProcessName)
            .ThenBy(w => w.Title)
            .ToList();
    }

    // ── Apply / remove stealth ─────────────────────────────────────────────────

    /// <summary>
    /// Attempts to toggle stealth (WDA_EXCLUDEFROMCAPTURE) on the given window.
    /// Sets <see cref="WindowInfo.StealthOn"/> and <see cref="WindowInfo.StatusText"/>
    /// to reflect the outcome.
    /// </summary>
    public static void ToggleStealth(WindowInfo win)
    {
        var desired = win.StealthOn ? NativeMethods.WDA_NONE : NativeMethods.WDA_EXCLUDEFROMCAPTURE;

        bool ok = NativeMethods.SetWindowDisplayAffinity(win.Handle, desired);

        if (ok)
        {
            win.StealthOn  = !win.StealthOn;
            win.StatusText = win.StealthOn ? "🙈 Stealth ON" : "👁 Visible";
        }
        else
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            win.StatusText = err == 5 /* ERROR_ACCESS_DENIED */
                ? "⛔ Access denied — can't stealth windows from other processes"
                : $"⚠ Failed (Win32 error {err})";
        }
    }

    /// <summary>
    /// Removes stealth from all windows tracked as stealthed (e.g., on app close).
    /// </summary>
    public static void RestoreAll(IEnumerable<WindowInfo> windows)
    {
        foreach (var win in windows.Where(w => w.StealthOn))
        {
            NativeMethods.SetWindowDisplayAffinity(win.Handle, NativeMethods.WDA_NONE);
            win.StealthOn  = false;
            win.StatusText = "";
        }
    }
}
