namespace CleanDesk.App.Services;

public enum StartupCommandKind
{
    None,
    Show,
    CreateBox,
    CreateMappedBox,
    Organize,
    Restore,
    Settings,
    Pause,
    Exit
}

public sealed class StartupCommand
{
    public StartupCommandKind Kind { get; init; }

    public static StartupCommand None { get; } = new();

    public static StartupCommand Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return None;
        }

        var arg = args[0].Trim().ToLowerInvariant();
        return new StartupCommand
        {
            Kind = arg switch
            {
                "--show" => StartupCommandKind.Show,
                "--create-box" => StartupCommandKind.CreateBox,
                "--create-mapped-box" => StartupCommandKind.CreateMappedBox,
                "--organize" => StartupCommandKind.Organize,
                "--restore" => StartupCommandKind.Restore,
                "--settings" => StartupCommandKind.Settings,
                "--pause" => StartupCommandKind.Pause,
                "--exit" => StartupCommandKind.Exit,
                _ => StartupCommandKind.Show
            }
        };
    }

    public override string ToString()
    {
        return Kind.ToString();
    }
}
