using CleanDesk.App.Services;
using CleanDesk.App.UI;

namespace CleanDesk.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        ExceptionGuard.Install();

        if (ShellContextMenuHost.TryRunHelper(args))
        {
            return;
        }

        using var mutex = new Mutex(true, @"Local\CleanDesk.App.SingleInstance", out var ownsMutex);
        var command = StartupCommand.Parse(args);

        if (!ownsMutex)
        {
            CommandPipeClient.TrySend(command);
            return;
        }

        var app = new CleanDeskApplication(command);
        app.Run();
    }
}
