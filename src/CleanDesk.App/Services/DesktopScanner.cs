using System.Text.RegularExpressions;
using CleanDesk.App.Models;

namespace CleanDesk.App.Services;

public sealed class DesktopScanner
{
    private static readonly string[] DefaultBoxNames =
    [
        "最近常用",
        "快捷方式",
        "目录",
        "文档",
        "图片",
        "音乐视频",
        "压缩包",
        "今日文件",
        "临时工作区",
        "其他"
    ];

    public IReadOnlyList<string> DesktopRoots { get; } =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    ];

    public List<DeskItem> Scan(AppSettings settings)
    {
        var previous = settings.DesktopItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var positions = DesktopInterop.GetIconPositions();
        var items = new List<DeskItem>();

        foreach (var root in DesktopRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch
                {
                    continue;
                }

                if ((attributes & FileAttributes.System) != 0 && (attributes & FileAttributes.Hidden) != 0)
                {
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var displayName = ShellInterop.GetDisplayName(entry);
                previous.TryGetValue(entry, out var old);
                var info = isDirectory ? null : new FileInfo(entry);
                var dirInfo = isDirectory ? new DirectoryInfo(entry) : null;
                var item = new DeskItem
                {
                    Path = entry,
                    Name = name,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                    Extension = isDirectory ? "" : Path.GetExtension(entry).ToLowerInvariant(),
                    DesktopRoot = root,
                    IsDirectory = isDirectory,
                    IsShortcut = IsShortcut(entry),
                    IsCommonDesktop = root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), StringComparison.OrdinalIgnoreCase),
                    CreatedUtc = isDirectory ? dirInfo!.CreationTimeUtc : info!.CreationTimeUtc,
                    LastAccessUtc = isDirectory ? dirInfo!.LastAccessTimeUtc : info!.LastAccessTimeUtc,
                    LastWriteUtc = isDirectory ? dirInfo!.LastWriteTimeUtc : info!.LastWriteTimeUtc,
                    LastClickedUtc = old?.LastClickedUtc,
                    LastOpenedUtc = old?.LastOpenedUtc,
                    ClickCount = old?.ClickCount ?? 0,
                    OpenCount = old?.OpenCount ?? 0
                };

                item.OriginalPosition = ResolvePosition(positions, item);
                item.Category = Classify(item, settings.Rules);
                items.Add(item);
            }
        }

        EnsureDefaultBoxes(settings);
        AssignItemsToBoxes(settings, items);
        settings.DesktopItems = items.OrderBy(item => item.Category).ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        return settings.DesktopItems;
    }

    public void EnsureDefaultBoxes(AppSettings settings)
    {
        AdaptiveBoxLayoutService.EnsurePresets(settings);
        var workArea = GetPrimaryWorkArea();
        var existing = settings.Boxes.ToDictionary(box => box.Name, StringComparer.CurrentCultureIgnoreCase);
        var width = settings.DefaultBoxWidth;
        var height = settings.DefaultBoxHeight;
        var gap = Math.Clamp(settings.BoxGap <= 0 ? 18 : settings.BoxGap, 12, 28);
        var placementColumns = Math.Max(1, Math.Min(3, (int)Math.Floor((workArea.Width + gap) / (width + gap))));
        while (placementColumns > 1)
        {
            var placementRows = (int)Math.Ceiling(DefaultBoxNames.Length / (double)placementColumns);
            var neededHeight = placementRows * height + Math.Max(0, placementRows - 1) * gap;
            if (neededHeight <= workArea.Height - gap * 2)
            {
                break;
            }

            placementColumns--;
        }
        placementColumns = Math.Max(1, placementColumns);
        var addedIndex = 0;

        for (var i = 0; i < DefaultBoxNames.Length; i++)
        {
            var name = DefaultBoxNames[i];
            if (existing.ContainsKey(name))
            {
                continue;
            }

            settings.Boxes.Add(new BoxModel
            {
                Name = name,
                Kind = name is "最近常用" or "今日文件" ? BoxKind.Virtual : BoxKind.Normal,
                Bounds = new DesktopRect
                {
                    X = workArea.Left + gap + (addedIndex % placementColumns) * (width + gap),
                    Y = workArea.Top + gap + (addedIndex / placementColumns) * (height + gap),
                    Width = width,
                    Height = height
                },
                LastExpandedWidth = width,
                LastExpandedHeight = height,
                Opacity = settings.GlobalOpacity,
                IsCollapsed = true,
                DockEdge = BoxLayoutService.GetDefaultDockEdge(name),
                DisplayMode = settings.DefaultDisplayMode
            });
            addedIndex++;
        }
    }

    private static WorkArea GetPrimaryWorkArea()
    {
        try
        {
            var work = System.Windows.SystemParameters.WorkArea;
            if (work.Width > 1 && work.Height > 1)
            {
                return new WorkArea(work.Left, work.Top, work.Width, work.Height);
            }
        }
        catch
        {
            // Fall through to a conservative default.
        }

        return new WorkArea(0, 0, 1400, 900);
    }

    private static void AssignItemsToBoxes(AppSettings settings, List<DeskItem> items)
    {
        var boxByName = settings.Boxes.ToDictionary(box => box.Name, StringComparer.CurrentCultureIgnoreCase);
        var boxById = settings.Boxes.ToDictionary(box => box.Id, StringComparer.OrdinalIgnoreCase);
        settings.ItemBoxOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visiblePaths = items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in settings.ItemBoxOverrides.Keys.ToArray())
        {
            if (!visiblePaths.Contains(path) ||
                !boxById.TryGetValue(settings.ItemBoxOverrides[path], out var overrideBox) ||
                overrideBox.Kind != BoxKind.Normal)
            {
                settings.ItemBoxOverrides.Remove(path);
            }
        }
        foreach (var box in settings.Boxes.Where(box => box.Kind == BoxKind.Normal))
        {
            box.ItemPaths.Clear();
        }

        foreach (var item in items)
        {
            if (settings.ItemBoxOverrides.TryGetValue(item.Path, out var overrideBoxId) &&
                boxById.TryGetValue(overrideBoxId, out var box) &&
                box.Kind == BoxKind.Normal)
            {
                item.Category = box.Name;
            }
            else if (!boxByName.TryGetValue(item.Category, out box))
            {
                box = boxByName.TryGetValue("其他", out var fallback) ? fallback : settings.Boxes.First();
            }

            item.BoxId = box.Id;
            if (box.Kind == BoxKind.Normal && !box.ItemPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            {
                box.ItemPaths.Add(item.Path);
            }
        }
    }

    private static string Classify(DeskItem item, IEnumerable<CategoryRule> rules)
    {
        foreach (var rule in rules.Where(rule => rule.Enabled && rule.Type != CategoryRuleType.Fallback))
        {
            if (Matches(rule, item))
            {
                return rule.TargetBoxName;
            }
        }

        return rules.FirstOrDefault(rule => rule.Enabled && rule.Type == CategoryRuleType.Fallback)?.TargetBoxName ?? "其他";
    }

    private static bool Matches(CategoryRule rule, DeskItem item)
    {
        return rule.Type switch
        {
            CategoryRuleType.Folder => item.IsDirectory,
            CategoryRuleType.Shortcut => item.IsShortcut || Split(rule.Pattern).Contains(item.Extension, StringComparer.OrdinalIgnoreCase),
            CategoryRuleType.Extension => Split(rule.Pattern).Contains(item.Extension, StringComparer.OrdinalIgnoreCase),
            CategoryRuleType.Keyword => !string.IsNullOrWhiteSpace(rule.Pattern) && item.DisplayName.Contains(rule.Pattern, StringComparison.CurrentCultureIgnoreCase),
            CategoryRuleType.Wildcard => WildcardMatches(rule.Pattern, item.Name),
            CategoryRuleType.Recent => item.OpenCount > 0 || item.ClickCount > 0,
            _ => false
        };
    }

    private static bool WildcardMatches(string pattern, string value)
    {
        return Split(pattern).Any(part =>
        {
            var escaped = Regex.Escape(part).Replace("\\*", ".*").Replace("\\?", ".");
            return Regex.IsMatch(value, "^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        });
    }

    private static IEnumerable<string> Split(string pattern)
    {
        return pattern.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.StartsWith('.') ? part.ToLowerInvariant() : part);
    }

    private static bool IsShortcut(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    private static DesktopPoint? ResolvePosition(Dictionary<string, DesktopPoint> positions, DeskItem item)
    {
        if (positions.TryGetValue(item.DisplayName, out var point))
        {
            return point;
        }

        if (positions.TryGetValue(item.Name, out point))
        {
            return point;
        }

        var stem = Path.GetFileNameWithoutExtension(item.Name);
        return !string.IsNullOrWhiteSpace(stem) && positions.TryGetValue(stem, out point) ? point : null;
    }

    private readonly record struct WorkArea(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }
}
