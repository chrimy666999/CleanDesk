using CleanDesk.App.Models;
using CleanDesk.App.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace CleanDesk.App.Services;

public sealed class DialogAccessService : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint GaRoot = 2;
    private const uint WmSetText = 0x000C;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkReturn = 0x0D;

    private readonly Dispatcher _dispatcher;
    private readonly CleanDeskController _controller;
    private readonly WinEventProc _callback;
    private readonly DispatcherTimer _pollTimer;
    private MagneticAccessWindow? _window;
    private IntPtr _hook;
    private IntPtr _showHook;
    private IntPtr _activeDialog;
    private IntPtr _suppressedDialog;
    private bool _disposed;

    public DialogAccessService(Dispatcher dispatcher, CleanDeskController controller)
    {
        _dispatcher = dispatcher;
        _controller = controller;
        _callback = HandleWinEvent;
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        _showHook = SetWinEventHook(
            EventObjectShow,
            EventObjectShow,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _pollTimer.Tick += (_, _) => EvaluateForeground(GetForegroundWindow());
        _pollTimer.Start();

        _dispatcher.BeginInvoke(new Action(() => EvaluateForeground(GetForegroundWindow())), DispatcherPriority.Background);
    }

    public void RefreshContent()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(RefreshContent), DispatcherPriority.Background);
            return;
        }

        if (_window is { IsVisible: true })
        {
            _window.RefreshContent();
            PositionAccessWindow();
        }
    }

    public void ActivatePath(DeskItem item)
    {
        if (item.IsDirectory)
        {
            JumpToDirectory(item.Path);
            return;
        }

        if (!TrySetDialogPath(item.Path, submit: false))
        {
            ShellOperations.Open(item.Path);
        }
    }

    public void JumpToBoxDirectory(BoxModel box)
    {
        var directory = _controller.GetImportDirectory(box);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        JumpToDirectory(directory);
    }

    public void JumpToDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var targetDirectory = Path.GetFullPath(directory);
        if (!targetDirectory.EndsWith(Path.DirectorySeparatorChar) &&
            !targetDirectory.EndsWith(Path.AltDirectorySeparatorChar))
        {
            targetDirectory += Path.DirectorySeparatorChar;
        }

        if (!TrySetDialogPath(targetDirectory, submit: true))
        {
            ShellOperations.Open(targetDirectory);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        if (_showHook != IntPtr.Zero)
        {
            UnhookWinEvent(_showHook);
            _showHook = IntPtr.Zero;
        }

        _pollTimer.Stop();

        if (_window is not null)
        {
            _window.CloseForShutdown();
            _window = null;
        }
    }

    public void NotifyWindowClosedByUser()
    {
        _suppressedDialog = _activeDialog;
    }

    private void HandleWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(new Action(() => EvaluateForeground(hwnd)), DispatcherPriority.Background);
    }

    private void EvaluateForeground(IntPtr hwnd)
    {
        if (_disposed)
        {
            return;
        }

        var foreground = hwnd == IntPtr.Zero ? GetForegroundWindow() : hwnd;
        if (_window?.IsOwnWindow(foreground) == true)
        {
            if (_activeDialog != IntPtr.Zero && IsWindow(_activeDialog) && IsWindowVisible(_activeDialog))
            {
                PositionAccessWindow(force: false);
            }

            return;
        }

        if (_window?.IsUserInteracting == true &&
            _activeDialog != IntPtr.Zero &&
            IsWindow(_activeDialog) &&
            IsWindowVisible(_activeDialog))
        {
            return;
        }

        var dialog = FindOpenSaveDialog(foreground);
        if (dialog == IntPtr.Zero)
        {
            dialog = FindOpenSaveDialog(GetForegroundWindow());
        }

        if (dialog == IntPtr.Zero)
        {
            dialog = FindVisibleOpenSaveDialogNearForeground(GetForegroundWindow());
        }

        if (dialog == IntPtr.Zero)
        {
            if (_activeDialog != IntPtr.Zero && IsWindow(_activeDialog) && IsWindowVisible(_activeDialog))
            {
                return;
            }

            if (_suppressedDialog != IntPtr.Zero && IsWindow(_suppressedDialog) && IsWindowVisible(_suppressedDialog))
            {
                HideAccessWindow(clearActiveDialog: false);
                return;
            }

            _suppressedDialog = IntPtr.Zero;
            HideAccessWindow();
            return;
        }

        var dialogChanged = _activeDialog != dialog;
        if (dialogChanged && _suppressedDialog != dialog)
        {
            _suppressedDialog = IntPtr.Zero;
        }

        if (_suppressedDialog == dialog && _window is not null && !_window.IsVisible)
        {
            return;
        }

        _activeDialog = dialog;
        _window ??= new MagneticAccessWindow(_controller, this);
        var wasVisible = _window.IsVisible;
        if (dialogChanged || !wasVisible)
        {
            _window.RefreshContent();
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        var shouldForcePosition = dialogChanged || (!wasVisible && !_window.IsUserAdjusted);
        PositionAccessWindow(force: shouldForcePosition);
    }

    private void PositionAccessWindow(bool force = false)
    {
        if (_window is null || _activeDialog == IntPtr.Zero || !TryGetWindowRect(_activeDialog, out var dialogRect))
        {
            return;
        }

        _window.PositionNear(dialogRect, force);
    }

    private void HideAccessWindow(bool clearActiveDialog = true)
    {
        if (clearActiveDialog)
        {
            _activeDialog = IntPtr.Zero;
        }

        if (_window is { IsVisible: true })
        {
            _window.Hide();
        }
    }

    private bool TrySetDialogPath(string path, bool submit)
    {
        if (!TryEnsureActiveDialog())
        {
            return false;
        }

        var edit = FindBestEdit(_activeDialog);
        if (edit == IntPtr.Zero)
        {
            return false;
        }

        SetForegroundWindow(_activeDialog);
        SetFocus(edit);
        SendMessage(edit, WmSetText, IntPtr.Zero, path);
        if (submit)
        {
            PostMessage(edit, WmKeyDown, new IntPtr(VkReturn), IntPtr.Zero);
            PostMessage(edit, WmKeyUp, new IntPtr(VkReturn), IntPtr.Zero);
        }

        return true;
    }

    private bool TryEnsureActiveDialog()
    {
        if (_activeDialog != IntPtr.Zero && IsWindow(_activeDialog) && IsWindowVisible(_activeDialog))
        {
            return true;
        }

        var dialog = FindOpenSaveDialog(GetForegroundWindow());
        if (dialog == IntPtr.Zero)
        {
            dialog = FindAnyVisibleOpenSaveDialog();
        }

        if (dialog == IntPtr.Zero)
        {
            return false;
        }

        _activeDialog = dialog;
        if (_suppressedDialog == dialog)
        {
            _suppressedDialog = IntPtr.Zero;
        }

        return true;
    }

    private static IntPtr FindOpenSaveDialog(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var root = GetAncestor(hwnd, GaRoot);
        if (root == IntPtr.Zero)
        {
            root = hwnd;
        }

        if (LooksLikeOpenSaveDialog(root))
        {
            return root;
        }

        return LooksLikeOpenSaveDialog(hwnd) ? hwnd : IntPtr.Zero;
    }

    private static IntPtr FindVisibleOpenSaveDialogNearForeground(IntPtr foreground)
    {
        var foregroundProcessId = 0u;
        if (foreground != IntPtr.Zero)
        {
            GetWindowThreadProcessId(foreground, out foregroundProcessId);
        }

        var found = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            if (!LooksLikeOpenSaveDialog(candidate))
            {
                return true;
            }

            if (foregroundProcessId != 0)
            {
                GetWindowThreadProcessId(candidate, out var candidateProcessId);
                if (candidateProcessId != foregroundProcessId)
                {
                    return true;
                }
            }

            found = candidate;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static IntPtr FindAnyVisibleOpenSaveDialog()
    {
        var found = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            if (!LooksLikeOpenSaveDialog(candidate))
            {
                return true;
            }

            found = candidate;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static bool LooksLikeOpenSaveDialog(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
        {
            return false;
        }

        var className = GetClassNameText(hwnd);
        if (!className.Equals("#32770", StringComparison.Ordinal))
        {
            return false;
        }

        var title = GetWindowTitle(hwnd);
        if (title.Equals("CleanDesk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ContainsDialogKeyword(title))
        {
            return true;
        }

        return HasChildClass(hwnd, "DirectUIHWND") &&
               (HasChildClass(hwnd, "Edit") || HasChildClass(hwnd, "ComboBoxEx32"));
    }

    private static bool ContainsDialogKeyword(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalized = title.Trim();
        return normalized.Contains("打开", StringComparison.CurrentCultureIgnoreCase) ||
               normalized.Contains("保存", StringComparison.CurrentCultureIgnoreCase) ||
               normalized.Contains("另存为", StringComparison.CurrentCultureIgnoreCase) ||
               normalized.Contains("选择", StringComparison.CurrentCultureIgnoreCase) ||
               normalized.Contains("Open", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("Browse", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("Select", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasChildClass(IntPtr parent, string className)
    {
        var found = false;
        EnumChildWindows(parent, (child, _) =>
        {
            if (GetClassNameText(child).Equals(className, StringComparison.Ordinal))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr FindBestEdit(IntPtr dialog)
    {
        var edits = new List<(IntPtr Handle, NativeRect Rect)>();
        EnumChildWindows(dialog, (child, _) =>
        {
            if (IsWindowVisible(child) &&
                GetClassNameText(child).Equals("Edit", StringComparison.Ordinal) &&
                GetWindowRectNative(child, out var rect) &&
                rect.Right > rect.Left &&
                rect.Bottom > rect.Top)
            {
                edits.Add((child, rect));
            }

            return true;
        }, IntPtr.Zero);

        return edits
            .OrderByDescending(edit => edit.Rect.Top)
            .ThenByDescending(edit => edit.Rect.Right - edit.Rect.Left)
            .Select(edit => edit.Handle)
            .FirstOrDefault();
    }

    private static bool TryGetWindowRect(IntPtr hwnd, out Rect rect)
    {
        rect = Rect.Empty;
        if (!GetWindowRectNative(hwnd, out var native))
        {
            return false;
        }

        rect = new Rect(native.Left, native.Top, Math.Max(1, native.Right - native.Left), Math.Max(1, native.Bottom - native.Top));
        return true;
    }

    private static string GetClassNameText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString(0, length);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate void WinEventProc(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRectNative(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
