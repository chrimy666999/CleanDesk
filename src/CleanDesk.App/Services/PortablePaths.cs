namespace CleanDesk.App.Services;

public static class PortablePaths
{
    public static string AppBase => AppContext.BaseDirectory;
    public static string DataRoot => Path.Combine(AppBase, "portable-data");
    public static string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public static string BackupsRoot => Path.Combine(DataRoot, "backups");
    public static string LogsRoot => Path.Combine(DataRoot, "logs");
    public static string CacheRoot => Path.Combine(DataRoot, "cache");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(CacheRoot);
    }
}
