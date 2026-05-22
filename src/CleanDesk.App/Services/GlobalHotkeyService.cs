using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CleanDesk.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x4344;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint VkSpace = 0x20;

    private readonly Dispatcher _dispatcher;
    private readonly Action _callback;
    private HwndSource? _source;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkeyService(Dispatcher dispatcher, Action callback)
    {
        _dispatcher = dispatcher;
        _callback = callback;
    }

    public void Register()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(Register), DispatcherPriority.Background);
            return;
        }

        if (_source is null)
        {
            var parameters = new HwndSourceParameters("CleanDesk.GlobalHotkeys")
            {
                Width = 0,
                Height = 0,
                WindowStyle = unchecked((int)0x80000000)
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }

        if (_source.Handle == IntPtr.Zero || _registered)
        {
            return;
        }

        _registered = RegisterHotKey(_source.Handle, HotkeyId, ModControl, VkSpace);
        if (!_registered)
        {
            Logger.Error(new InvalidOperationException("Ctrl+Space is already registered by another application."), "Failed to register global search hotkey.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(Dispose), DispatcherPriority.Background);
            return;
        }

        if (_source is not null)
        {
            if (_registered)
            {
                UnregisterHotKey(_source.Handle, HotkeyId);
                _registered = false;
            }

            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _callback();
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
