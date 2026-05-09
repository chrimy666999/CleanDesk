namespace CleanDesk.App.Models;

public enum CategoryRuleType
{
    Extension,
    Keyword,
    Wildcard,
    Folder,
    Shortcut,
    Recent,
    Fallback
}

public sealed class CategoryRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public CategoryRuleType Type { get; set; }
    public string Pattern { get; set; } = "";
    public string TargetBoxName { get; set; } = "其他";
}
