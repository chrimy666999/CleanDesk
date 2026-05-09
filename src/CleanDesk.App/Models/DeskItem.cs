namespace CleanDesk.App.Models;

public sealed class DeskItem
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string DesktopRoot { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsShortcut { get; set; }
    public bool IsCommonDesktop { get; set; }
    public string Category { get; set; } = "其他";
    public string BoxId { get; set; } = "";
    public DesktopPoint? OriginalPosition { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime LastAccessUtc { get; set; }
    public DateTime? LastOpenedUtc { get; set; }
    public int OpenCount { get; set; }
}
