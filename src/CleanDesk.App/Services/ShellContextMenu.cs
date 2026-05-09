using System.Runtime.InteropServices;

namespace CleanDesk.App.Services;

public static class ShellContextMenu
{
    private const uint CmfNormal = 0x00000000;
    private const uint CmfExplore = 0x00000004;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const int SwShownormal = 1;
    private const int CmicMaskUnicode = 0x00004000;
    private const int CmicMaskPtInvoke = 0x20000000;

    public static bool Show(string path, IntPtr owner, int x, int y)
    {
        if (!ShellOperations.Exists(path))
        {
            return false;
        }

        IntPtr absolutePidl = IntPtr.Zero;
        IntPtr childPidl = IntPtr.Zero;
        IntPtr menu = IntPtr.Zero;
        object? contextMenuObject = null;
        IShellFolder? parentFolder = null;
        var oleInitialized = false;

        try
        {
            oleInitialized = OleInitialize(IntPtr.Zero) >= 0;

            var parseResult = SHParseDisplayName(path, IntPtr.Zero, out absolutePidl, 0, out _);
            if (parseResult != 0 || absolutePidl == IntPtr.Zero)
            {
                return false;
            }

            var shellFolderId = typeof(IShellFolder).GUID;
            var bindResult = SHBindToParent(absolutePidl, ref shellFolderId, out parentFolder, out childPidl);
            if (bindResult != 0 || parentFolder is null || childPidl == IntPtr.Zero)
            {
                return false;
            }

            var contextMenuId = typeof(IContextMenu).GUID;
            parentFolder.GetUIObjectOf(owner, 1, [childPidl], ref contextMenuId, IntPtr.Zero, out var contextMenuPtr);
            if (contextMenuPtr == IntPtr.Zero)
            {
                return false;
            }

            contextMenuObject = Marshal.GetObjectForIUnknown(contextMenuPtr);
            Marshal.Release(contextMenuPtr);

            if (contextMenuObject is not IContextMenu contextMenu)
            {
                return false;
            }

            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return false;
            }

            var firstCommand = 1u;
            var queryResult = contextMenu.QueryContextMenu(menu, 0, firstCommand, 0x7FFF, CmfNormal | CmfExplore);
            if (queryResult < 0)
            {
                return false;
            }

            SetForegroundWindow(owner);
            var selected = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, x, y, owner, IntPtr.Zero);
            if (selected == 0)
            {
                return true;
            }

            var invoke = new CmInvokeCommandInfoEx
            {
                cbSize = Marshal.SizeOf<CmInvokeCommandInfoEx>(),
                fMask = CmicMaskUnicode | CmicMaskPtInvoke,
                hwnd = owner,
                lpVerb = (IntPtr)(selected - firstCommand),
                lpVerbW = (IntPtr)(selected - firstCommand),
                nShow = SwShownormal,
                ptInvoke = new NativePoint { X = x, Y = y }
            };

            contextMenu.InvokeCommand(ref invoke);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Shell context menu failed.");
            return false;
        }
        finally
        {
            if (menu != IntPtr.Zero)
            {
                DestroyMenu(menu);
            }

            if (absolutePidl != IntPtr.Zero)
            {
                ILFree(absolutePidl);
            }

            if (contextMenuObject is not null)
            {
                Marshal.FinalReleaseComObject(contextMenuObject);
            }

            if (parentFolder is not null)
            {
                Marshal.FinalReleaseComObject(parentFolder);
            }

            if (oleInitialized)
            {
                OleUninitialize();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CmInvokeCommandInfoEx
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public NativePoint ptInvoke;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CmInvokeCommandInfoEx pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IShellFolder ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
