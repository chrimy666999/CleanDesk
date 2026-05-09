using CleanDesk.App.Services;

namespace CleanDesk.App.UI;

public sealed class CleanDeskApplication : System.Windows.Application
{
    private readonly StartupCommand _startupCommand;
    private CleanDeskController? _controller;

    public CleanDeskApplication(StartupCommand startupCommand)
    {
        _startupCommand = startupCommand;
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SessionEnding += (_, _) => _controller?.MarkCleanExit();
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        _controller = new CleanDeskController(Dispatcher);
        _controller.Initialize(_startupCommand);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error(e.Exception, "WPF dispatcher exception.");
        e.Handled = true;
        _controller?.HandleRecoverableException(e.Exception);
    }
}
