using System.Runtime.InteropServices;

namespace OnAirNative.Win32;

/// <summary>
/// A minimal wrapper around the classic Win32 Common Item Dialog (<c>IFileOpenDialog</c> COM
/// interface, shell32.dll) — used INSTEAD of <c>Windows.Storage.Pickers.FileOpenPicker</c>
/// (the WinRT/modern picker) for every "browse for a file" need in this app.
///
/// WHY: in an UNPACKAGED WinAppSDK/WinUI 3 app (this app has no MSIX identity —
/// WindowsPackageType=None), <c>Windows.Storage.Pickers.FileOpenPicker</c> marshals its actual UI
/// through an out-of-process broker (<c>PickerHost.exe</c>) to preserve the packaged-app
/// sandboxing model it was designed for. In some session types (observed: a remote/automation
/// desktop session driving this exact app) that broker process spawns but never creates a
/// window, so <c>PickSingleFileAsync()</c>/<c>PickMultipleFilesAsync()</c> hang FOREVER with no
/// exception, no dialog, nothing — confirmed via diagnostic logging (the awaited call never
/// returns) and via Process Explorer (a <c>PickerHost.exe</c> process appears with
/// <c>MainWindowHandle == 0</c>, i.e. it never rendered).
///
/// <c>IFileOpenDialog</c> is the SAME underlying Win32 Common Item Dialog every native Win32 app
/// (Notepad, VS Code's native dialogs, File Explorer itself) uses directly — calling it via COM
/// (<c>CoCreateInstance</c>) runs entirely IN-PROCESS, with no broker involved at all, so it
/// can't hit this specific failure mode. It also renders the exact same modern Windows 11-style
/// dialog UI as the WinRT picker — this is not a downgrade to the "classic" 1990s file dialog.
///
/// Deliberately synchronous (<c>Show</c> blocks the calling thread until the user closes the
/// dialog) — this matches how every classic Win32 common dialog behaves (same pattern as
/// WinForms' <c>OpenFileDialog</c>/WPF's <c>Microsoft.Win32.OpenFileDialog</c>, both also
/// synchronous): the dialog pumps its own nested message loop while shown, so this does not
/// freeze the app's own UI thread the way a genuine deadlock would — it behaves exactly like any
/// other modal dialog (e.g. a ContentDialog's own blocking-until-closed await pattern), just
/// synchronous instead of async since there's no broker round-trip to await.
/// </summary>
internal static class Win32FileDialog
{
    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private static readonly Guid IidIFileOpenDialog   = new("d57c7288-d4ad-4768-be02-9d969532d960");

    private const uint FOS_ALLOWMULTISELECT = 0x00000200;
    private const uint FOS_FORCEFILESYSTEM  = 0x00000040;
    private const uint FOS_FILEMUSTEXIST    = 0x00001000;

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);

        // IFileDialog (partial — only the members this wrapper actually calls, in declaration order)
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);

        // IFileOpenDialog
        void GetResults(out IShellItemArray ppenum);
        void GetSelectedItems(out IShellItemArray ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int  HRESULT_CANCELLED = unchecked((int)0x800704C7); // user closed/cancelled the dialog

    /// <summary>Shows a single-file "Open" dialog. Returns the selected file's full path, or
    /// null if the user cancelled.</summary>
    /// <param name="ownerHwnd">The owning window's HWND — makes the dialog modal to it.</param>
    /// <param name="filterName">Display label for the extension filter, e.g. "Text files".</param>
    /// <param name="extensions">Extensions to allow, WITHOUT the leading dot, e.g. "txt".
    /// Multiple extensions are OR'd into one filter entry, e.g. ["txt","md"].</param>
    public static string? PickSingleFile(IntPtr ownerHwnd, string filterName, params string[] extensions)
    {
        var dialog = (IFileOpenDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidFileOpenDialog)!)!;
        try
        {
            ApplyFilter(dialog, filterName, extensions);
            dialog.SetOptions(FOS_FORCEFILESYSTEM | FOS_FILEMUSTEXIST);

            var hr = dialog.Show(ownerHwnd);
            if (hr == HRESULT_CANCELLED) return null;
            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResult(out var item);
            return GetPath(item);
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    /// <summary>Shows a multi-file "Open" dialog. Returns the selected files' full paths (empty
    /// if the user cancelled or selected nothing).</summary>
    public static List<string> PickMultipleFiles(IntPtr ownerHwnd, string filterName, params string[] extensions)
    {
        var dialog = (IFileOpenDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidFileOpenDialog)!)!;
        try
        {
            ApplyFilter(dialog, filterName, extensions);
            dialog.SetOptions(FOS_FORCEFILESYSTEM | FOS_FILEMUSTEXIST | FOS_ALLOWMULTISELECT);

            var hr = dialog.Show(ownerHwnd);
            if (hr == HRESULT_CANCELLED) return [];
            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResults(out var items);
            items.GetCount(out var count);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out var item);
                var path = GetPath(item);
                if (path is not null) paths.Add(path);
            }
            return paths;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private static void ApplyFilter(IFileOpenDialog dialog, string filterName, string[] extensions)
    {
        if (extensions.Length == 0) return;
        var spec = string.Join(';', extensions.Select(e => $"*.{e.TrimStart('.')}"));
        dialog.SetFileTypes(1, [new COMDLG_FILTERSPEC { pszName = $"{filterName} ({spec})", pszSpec = spec }]);
        dialog.SetFileTypeIndex(1);
    }

    private static string? GetPath(IShellItem item)
    {
        try
        {
            item.GetDisplayName(SIGDN_FILESYSPATH, out var ptr);
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }
}
