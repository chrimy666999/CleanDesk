namespace CleanDesk.App.Models;

public sealed class LayoutBackup
{
    public string Id { get; set; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = "";
    public List<BackupDesktopItem> Items { get; set; } = [];
}

public sealed class BackupDesktopItem
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DesktopPoint? OriginalPosition { get; set; }
    public string BoxId { get; set; } = "";
    public string Category { get; set; } = "";
    public bool ExistsAtBackup { get; set; }
}
