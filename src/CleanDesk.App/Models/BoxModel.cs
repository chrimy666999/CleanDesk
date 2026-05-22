namespace CleanDesk.App.Models;

public enum BoxKind
{
    Normal,
    Mapped,
    Virtual,
    Combined
}

public enum BoxDisplayMode
{
    Icon,
    IconOnly,
    List,
    MultiColumn,
    Auto,
    Manual,
    Recent
}

public enum BoxLayoutAlignment
{
    Left,
    Right,
    Top,
    Bottom
}

public enum BoxDockEdge
{
    Top,
    Left,
    Right
}

public sealed class BoxLayoutPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "自定义";
    public BoxLayoutAlignment Alignment { get; set; } = BoxLayoutAlignment.Left;
    public int Gap { get; set; } = 18;
    public bool AutoSize { get; set; } = true;
    public bool CollapseEmptyBoxes { get; set; } = true;

    public static List<BoxLayoutPreset> CreateDefaults()
    {
        return
        [
            new BoxLayoutPreset { Id = "left", Name = "全部向左对齐", Alignment = BoxLayoutAlignment.Left },
            new BoxLayoutPreset { Id = "right", Name = "全部向右对齐", Alignment = BoxLayoutAlignment.Right },
            new BoxLayoutPreset { Id = "top", Name = "全部向上对齐", Alignment = BoxLayoutAlignment.Top },
            new BoxLayoutPreset { Id = "bottom", Name = "全部向下对齐", Alignment = BoxLayoutAlignment.Bottom }
        ];
    }
}

public sealed class BoxTabModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string SourceBoxId { get; set; } = "";
}

public sealed class BoxModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新盒子";
    public BoxKind Kind { get; set; } = BoxKind.Normal;
    public DesktopRect Bounds { get; set; } = new();
    public double Opacity { get; set; } = 0.05;
    public bool IsCollapsed { get; set; }
    public bool HasUserLayout { get; set; }
    public bool IsLocked { get; set; }
    public bool IsVisible { get; set; } = true;
    public BoxDockEdge DockEdge { get; set; } = BoxDockEdge.Top;
    public BoxDisplayMode DisplayMode { get; set; } = BoxDisplayMode.Icon;
    public string MappedPath { get; set; } = "";
    public string CurrentPath { get; set; } = "";
    public double TitleLength { get; set; } = 220;
    public double LastExpandedHeight { get; set; } = 260;
    public double LastExpandedWidth { get; set; } = 360;
    public List<string> ItemPaths { get; set; } = [];
    public List<string> ManualItemPaths { get; set; } = [];
    public List<BoxTabModel> Tabs { get; set; } = [];
}
