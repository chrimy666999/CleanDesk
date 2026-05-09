using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;

namespace CleanDesk.App.Services;

public sealed class CommandPipeServer : IDisposable
{
    private const string PipeName = "CleanDesk.CommandPipe";
    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _dispatcher;
    private readonly Action<StartupCommand> _handler;

    public CommandPipeServer(Dispatcher dispatcher, Action<StartupCommand> handler)
    {
        _dispatcher = dispatcher;
        _handler = handler;
        _ = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var line = await reader.ReadLineAsync(_cts.Token);
                var command = StartupCommand.Parse(line is null ? [] : ["--" + ToKebab(line)]);
                _ = _dispatcher.BeginInvoke(() => _handler(command));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Command pipe failed.");
            }
        }
    }

    private static string ToKebab(string value)
    {
        return value switch
        {
            nameof(StartupCommandKind.CreateBox) => "create-box",
            nameof(StartupCommandKind.CreateMappedBox) => "create-mapped-box",
            nameof(StartupCommandKind.Organize) => "organize",
            nameof(StartupCommandKind.Restore) => "restore",
            nameof(StartupCommandKind.Settings) => "settings",
            nameof(StartupCommandKind.Pause) => "pause",
            nameof(StartupCommandKind.Exit) => "exit",
            _ => "show"
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

public static class CommandPipeClient
{
    private const string PipeName = "CleanDesk.CommandPipe";

    public static void TrySend(StartupCommand command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(350);
            using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(command.Kind.ToString());
        }
        catch
        {
            // If the first instance is shutting down, failing silently is safer than spawning duplicates.
        }
    }
}
