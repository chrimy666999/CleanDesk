namespace CleanDesk.App.Services;

public static class Logger
{
    private static readonly object Gate = new();

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(Exception exception, string context)
    {
        Write("ERROR", context + Environment.NewLine + exception);
    }

    private static void Write(string level, string message)
    {
        try
        {
            PortablePaths.Ensure();
            var path = Path.Combine(PortablePaths.LogsRoot, DateTime.Now.ToString("yyyyMMdd") + ".log");
            lock (Gate)
            {
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never interfere with desktop recovery.
        }
    }
}
