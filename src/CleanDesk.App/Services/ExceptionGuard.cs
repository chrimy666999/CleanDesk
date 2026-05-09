using Forms = System.Windows.Forms;

namespace CleanDesk.App.Services;

public static class ExceptionGuard
{
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown fatal exception.");
            Logger.Error(exception, "Fatal unhandled exception.");
            EmergencyShowDesktopIcons();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error(e.Exception, "Unobserved task exception.");
            e.SetObserved();
        };

        Forms.Application.ThreadException += (_, e) =>
        {
            Logger.Error(e.Exception, "WinForms thread exception.");
        };
    }

    public static void EmergencyShowDesktopIcons()
    {
        try
        {
            DesktopInterop.SetDesktopIconsVisible(true);
        }
        catch
        {
            // Last-resort recovery must not throw.
        }
    }
}
