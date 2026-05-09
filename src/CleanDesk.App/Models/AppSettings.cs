namespace CleanDesk.App.Models;

public sealed class AppSettings
{
    public string Version { get; set; } = "1.0.0";
    public bool AutoStart { get; set; }
    public bool AutoOrganizeOnStartup { get; set; } = true;
    public bool AutoOrganizeNewFiles { get; set; } = true;
    public bool RealtimeOrganize { get; set; } = true;
    public bool HideScatteredDesktopIcons { get; set; } = true;
    public bool AutoReflowAfterOrganize { get; set; } = true;
    public bool AllBoxesVisible { get; set; } = true;
    public bool PauseTakeover { get; set; }
    public bool DesktopTakeoverActive { get; set; }
    public bool LastSessionCleanExit { get; set; } = true;
    public string OriginalBackupId { get; set; } = "";
    public DateTime? LastTakeoverUtc { get; set; }
    public int SnapDistance { get; set; } = 14;
    public int GridSize { get; set; } = 16;
    public int BoxGap { get; set; } = 8;
    public int MinBoxWidth { get; set; } = 240;
    public int MinBoxHeight { get; set; } = 38;
    public int MaxBoxWidth { get; set; } = 460;
    public int MaxBoxHeight { get; set; } = 380;
    public int DefaultBoxWidth { get; set; } = 280;
    public int DefaultBoxHeight { get; set; } = 180;
    public string ActiveLayoutPresetId { get; set; } = "left";
    public int IconSize { get; set; } = 32;
    public bool MatchDesktopIconSize { get; set; } = true;
    public bool ShowFileNames { get; set; } = true;
    public double GlobalOpacity { get; set; } = 0.72;
    public string ThemeMode { get; set; } = "glass";
    public string BoxBackgroundColor { get; set; } = "#22303A";
    public string BoxAccentColor { get; set; } = "#7DD3FC";
    public string BoxTextColor { get; set; } = "#FFFFFF";
    public string BoxTitleColor { get; set; } = "#F8FAFC";
    public bool ShowBoxBorder { get; set; } = true;
    public bool EnableBoxCornerRadius { get; set; } = true;
    public int BoxCornerRadius { get; set; } = 12;
    public double TitleBarOpacity { get; set; } = 0.18;
    public bool CompactTitleBar { get; set; }
    public BoxDisplayMode DefaultDisplayMode { get; set; } = BoxDisplayMode.Icon;
    public List<BoxLayoutPreset> LayoutPresets { get; set; } = BoxLayoutPreset.CreateDefaults();
    public List<CategoryRule> Rules { get; set; } = CreateDefaultRules();
    public List<BoxModel> Boxes { get; set; } = [];
    public List<DeskItem> DesktopItems { get; set; } = [];

    public static List<CategoryRule> CreateDefaultRules()
    {
        return
        [
            new CategoryRule { Name = "快捷方式", Type = CategoryRuleType.Shortcut, Pattern = ".lnk,.url", TargetBoxName = "快捷方式" },
            new CategoryRule { Name = "目录", Type = CategoryRuleType.Folder, TargetBoxName = "目录" },
            new CategoryRule { Name = "文档", Type = CategoryRuleType.Extension, Pattern = ".doc,.docx,.pdf,.txt,.md,.ppt,.pptx,.xls,.xlsx", TargetBoxName = "文档" },
            new CategoryRule { Name = "图片", Type = CategoryRuleType.Extension, Pattern = ".jpg,.jpeg,.png,.bmp,.gif,.webp,.svg", TargetBoxName = "图片" },
            new CategoryRule { Name = "音乐视频", Type = CategoryRuleType.Extension, Pattern = ".mp3,.wav,.mp4,.avi,.mov,.mkv", TargetBoxName = "音乐视频" },
            new CategoryRule { Name = "压缩包", Type = CategoryRuleType.Extension, Pattern = ".zip,.rar,.7z,.tar,.gz", TargetBoxName = "压缩包" },
            new CategoryRule { Name = "其他", Type = CategoryRuleType.Fallback, TargetBoxName = "其他" }
        ];
    }
}
