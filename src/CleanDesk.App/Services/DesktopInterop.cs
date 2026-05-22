using System.Runtime.InteropServices;
using System.Text;
using CleanDesk.App.Models;

namespace CleanDesk.App.Services;

public static class DesktopInterop
{
    private const int LvmFirst = 0x1000;
    private const int LvmGetItemCount = LvmFirst + 4;
    private const int LvmGetItemPosition = LvmFirst + 16;
    private const int LvmSetItemPosition32 = LvmFirst + 49;
    private const int LvmGetItemTextW = LvmFirst + 115;
    private const uint LvifText = 0x0001;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int SwShow = 5;
    private const int SmtoNormal = 0;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;

    public static IntPtr FindDesktopListView()
    {
        var defView = FindDesktopDefView();
        if (defView == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
        if (listView == IntPtr.Zero)
        {
            listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        }

        return listView;
    }

    public static IntPtr FindDesktopHostWindow()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero && FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
        {
            return progman;
        }

        var host = IntPtr.Zero;
        EnumWindows((topHandle, _) =>
        {
            if (!WindowClassEquals(topHandle, "WorkerW"))
            {
                return true;
            }

            var defView = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
            {
                return true;
            }

            host = topHandle;
            return false;
        }, IntPtr.Zero);

        return host == IntPtr.Zero ? progman : host;
    }

    public static void SetDesktopIconsVisible(bool visible)
    {
        var listView = FindDesktopListView();
        if (listView != IntPtr.Zero)
        {
            ShowWindow(listView, visible ? SwShow : SwHide);
        }
    }

    public static bool AreDesktopIconsVisible()
    {
        var listView = FindDesktopListView();
        return listView == IntPtr.Zero || IsWindowVisibleNative(listView);
    }

    public static bool IsWindowCurrentlyVisible(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero && IsWindowVisibleNative(hWnd);
    }

    public static bool IsDesktopForeground()
    {
        var foreground = GetForegroundWindow();
        for (var depth = 0; foreground != IntPtr.Zero && depth < 8; depth++, foreground = GetParent(foreground))
        {
            var className = GetWindowClassName(foreground);
            if (className is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32")
            {
                return true;
            }
        }

        return false;
    }

    public static Dictionary<string, DesktopPoint> GetIconPositions()
    {
        var result = new Dictionary<string, DesktopPoint>(StringComparer.OrdinalIgnoreCase);
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            return result;
        }

        var count = (int)SendMessage(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0)
        {
            return result;
        }

        GetWindowThreadProcessId(listView, out var processId);
        var process = OpenProcess(ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, processId);
        if (process == IntPtr.Zero)
        {
            return result;
        }

        var remote = IntPtr.Zero;
        try
        {
            var lvItemSize = Marshal.SizeOf<NativeLvItem>();
            remote = VirtualAllocEx(process, IntPtr.Zero, 4096, MemCommit | MemReserve, PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                return result;
            }

            var remoteText = IntPtr.Add(remote, lvItemSize + 16);
            for (var i = 0; i < count; i++)
            {
                var name = ReadListViewItemText(listView, process, remote, remoteText, i);
                var point = ReadListViewItemPosition(listView, process, remote, i);
                if (!string.IsNullOrWhiteSpace(name) && point is not null)
                {
                    result[name] = point;
                }
            }
        }
        finally
        {
            if (remote != IntPtr.Zero)
            {
                VirtualFreeEx(process, remote, UIntPtr.Zero, MemRelease);
            }

            CloseHandle(process);
        }

        return result;
    }

    public static void RestoreIconPositions(IEnumerable<BackupDesktopItem> items)
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            return;
        }

        var indexByName = ReadListViewIndexByName(listView);
        foreach (var item in items)
        {
            if (item.OriginalPosition is null)
            {
                continue;
            }

            if (!TryFindIndex(indexByName, item, out var index))
            {
                continue;
            }

            var lParam = MakeLParam(item.OriginalPosition.X, item.OriginalPosition.Y);
            SendMessage(listView, LvmSetItemPosition32, (IntPtr)index, lParam);
        }
    }

    public static void MakeToolWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hWnd, GwlExStyle).ToInt64();
        var updatedStyle = style | WsExToolWindow;
        updatedStyle &= ~WsExAppWindow;
        if (updatedStyle != style)
        {
            SetWindowLongPtr(hWnd, GwlExStyle, new IntPtr(updatedStyle));
        }
    }

    public static bool IsAttachedToDesktop(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        var host = FindDesktopHostWindow();
        return host != IntPtr.Zero && GetParent(hWnd) == host;
    }

    public static IntPtr GetParentWindow(IntPtr hWnd)
    {
        return hWnd == IntPtr.Zero ? IntPtr.Zero : GetParent(hWnd);
    }

    public static bool TryAttachToDesktop(IntPtr hWnd)
    {
        return TryAttachToDesktop(hWnd, out _);
    }

    public static bool TryAttachToDesktop(IntPtr hWnd, out IntPtr host)
    {
        if (hWnd == IntPtr.Zero)
        {
            host = IntPtr.Zero;
            return false;
        }

        EnsureWorkerW();
        host = FindDesktopHostWindow();
        if (host != IntPtr.Zero)
        {
            if (GetParent(hWnd) == host)
            {
                return true;
            }

            SetParent(hWnd, host);
            return GetParent(hWnd) == host;
        }

        return false;
    }

    public static void ShowNoActivate(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero)
        {
            ShowWindow(hWnd, SwShowNoActivate);
        }
    }

    private static void EnsureWorkerW()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 1000, out _);
        }
    }

    private static IntPtr FindDesktopDefView()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                return defView;
            }
        }

        var found = IntPtr.Zero;
        EnumWindows((topHandle, _) =>
        {
            if (!WindowClassEquals(topHandle, "WorkerW"))
            {
                return true;
            }

            var candidate = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (candidate == IntPtr.Zero)
            {
                return true;
            }

            found = candidate;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static bool WindowClassEquals(IntPtr hWnd, string className)
    {
        return string.Equals(GetWindowClassName(hWnd), className, StringComparison.Ordinal);
    }

    private static string GetWindowClassName(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(hWnd, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString(0, length);
    }

    private static Dictionary<string, int> ReadListViewIndexByName(IntPtr listView)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var count = (int)SendMessage(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero);
        GetWindowThreadProcessId(listView, out var processId);
        var process = OpenProcess(ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, processId);
        if (process == IntPtr.Zero)
        {
            return result;
        }

        var remote = IntPtr.Zero;
        try
        {
            var lvItemSize = Marshal.SizeOf<NativeLvItem>();
            remote = VirtualAllocEx(process, IntPtr.Zero, 4096, MemCommit | MemReserve, PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                return result;
            }

            var remoteText = IntPtr.Add(remote, lvItemSize + 16);
            for (var i = 0; i < count; i++)
            {
                var name = ReadListViewItemText(listView, process, remote, remoteText, i);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result[name] = i;
                }
            }
        }
        finally
        {
            if (remote != IntPtr.Zero)
            {
                VirtualFreeEx(process, remote, UIntPtr.Zero, MemRelease);
            }

            CloseHandle(process);
        }

        return result;
    }

    private static bool TryFindIndex(Dictionary<string, int> indexByName, BackupDesktopItem item, out int index)
    {
        if (indexByName.TryGetValue(item.DisplayName, out index))
        {
            return true;
        }

        if (indexByName.TryGetValue(item.Name, out index))
        {
            return true;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(item.Name);
        return !string.IsNullOrWhiteSpace(nameWithoutExtension) && indexByName.TryGetValue(nameWithoutExtension, out index);
    }

    private static string ReadListViewItemText(IntPtr listView, IntPtr process, IntPtr remoteItem, IntPtr remoteText, int index)
    {
        var item = new NativeLvItem
        {
            mask = LvifText,
            iItem = index,
            iSubItem = 0,
            pszText = remoteText,
            cchTextMax = 260
        };

        var itemBytes = StructureToBytes(item);
        if (!WriteProcessMemory(process, remoteItem, itemBytes, itemBytes.Length, out _))
        {
            return "";
        }

        SendMessage(listView, LvmGetItemTextW, (IntPtr)index, remoteItem);

        var buffer = new byte[520];
        if (!ReadProcessMemory(process, remoteText, buffer, buffer.Length, out _))
        {
            return "";
        }

        var text = Encoding.Unicode.GetString(buffer);
        var nullIndex = text.IndexOf('\0');
        return nullIndex >= 0 ? text[..nullIndex] : text;
    }

    private static DesktopPoint? ReadListViewItemPosition(IntPtr listView, IntPtr process, IntPtr remotePoint, int index)
    {
        SendMessage(listView, LvmGetItemPosition, (IntPtr)index, remotePoint);

        var buffer = new byte[Marshal.SizeOf<NativePoint>()];
        if (!ReadProcessMemory(process, remotePoint, buffer, buffer.Length, out _))
        {
            return null;
        }

        var point = BytesToStructure<NativePoint>(buffer);
        return new DesktopPoint(point.X, point.Y);
    }

    private static IntPtr MakeLParam(int low, int high)
    {
        var value = (high << 16) | (low & 0xffff);
        return new IntPtr(value);
    }

    private static byte[] StructureToBytes<T>(T structure)
    {
        var size = Marshal.SizeOf<T>();
        var buffer = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(structure!, ptr, false);
            Marshal.Copy(ptr, buffer, 0, size);
            return buffer;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static T BytesToStructure<T>(byte[] buffer)
    {
        var ptr = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, ptr, buffer.Length);
            return Marshal.PtrToStructure<T>(ptr)!;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeLvItem
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, int flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsWindowVisibleNative(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, int dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out nuint lpNumberOfBytesWritten);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);
}
