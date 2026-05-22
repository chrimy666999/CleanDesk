using CleanDesk.App.Models;

namespace CleanDesk.App.Services;

public sealed class SearchResult
{
    public DeskItem Item { get; init; } = new();
    public string Path => Item.Path;
    public string DisplayName => string.IsNullOrWhiteSpace(Item.DisplayName) ? Item.Name : Item.DisplayName;
    public string BoxId { get; init; } = "";
    public string BoxName { get; init; } = "";
    public string TypeName { get; init; } = "";
    public DateTime LastUsedUtc { get; init; }
    public string LastUsedDisplay => LastUsedUtc == DateTime.MinValue ? "" : LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public int Score { get; init; }
}

public static class SearchIndexService
{
    private const int MaxMappedSearchItemsPerBox = 5000;

    public static IReadOnlyList<SearchResult> SearchAll(CleanDeskController controller, string query, int limit = 120)
    {
        var terms = SplitTerms(query);
        var candidates = new List<SearchResult>();
        foreach (var box in controller.VisibleBoxes)
        {
            candidates.AddRange(SearchBox(controller, box, query, Math.Max(limit, 160)));
        }

        return candidates
            .GroupBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => MergeDuplicatePathResults(group, terms))
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.LastUsedUtc)
            .ThenBy(result => result.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToList();
    }

    public static IReadOnlyList<SearchResult> SearchBox(CleanDeskController controller, BoxModel box, string query, int limit = 120)
    {
        var terms = SplitTerms(query);
        var items = EnumerateSearchItems(controller, box, recursiveMapped: terms.Count > 0)
            .Where(item => terms.Count == 0 || Matches(item, box.Name, terms))
            .Select(item => CreateResult(item, box, terms))
            .GroupBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(result => result.Score)
                .ThenByDescending(result => result.LastUsedUtc)
                .First())
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.LastUsedUtc)
            .ThenByDescending(result => result.Item.IsDirectory)
            .ThenBy(result => result.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToList();

        return items;
    }

    public static bool ItemMatches(DeskItem item, string boxName, string query)
    {
        var terms = SplitTerms(query);
        return terms.Count == 0 || Matches(item, boxName, terms);
    }

    private static SearchResult MergeDuplicatePathResults(IGrouping<string, SearchResult> group, IReadOnlyList<string> terms)
    {
        var ordered = group
            .OrderByDescending(result => IsPrimaryBoxName(result.BoxName))
            .ThenByDescending(result => result.Score)
            .ThenByDescending(result => result.LastUsedUtc)
            .ToList();
        var best = ordered.First();
        var boxNames = ordered
            .Select(result => result.BoxName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(4)
            .ToList();

        return new SearchResult
        {
            Item = best.Item,
            BoxId = best.BoxId,
            BoxName = boxNames.Count == 0 ? best.BoxName : string.Join(" / ", boxNames),
            TypeName = best.TypeName,
            LastUsedUtc = ordered.Max(result => result.LastUsedUtc),
            Score = ordered.Max(result => result.Score) + (terms.Count == 0 ? 0 : Math.Min(40, ordered.Count * 6))
        };
    }

    private static IEnumerable<DeskItem> EnumerateSearchItems(CleanDeskController controller, BoxModel box, bool recursiveMapped)
    {
        if (box.Kind == BoxKind.Mapped && recursiveMapped)
        {
            return EnumerateMappedItemsRecursive(box);
        }

        return controller.GetItemsForBox(box);
    }

    private static IEnumerable<DeskItem> EnumerateMappedItemsRecursive(BoxModel box)
    {
        var root = Directory.Exists(box.MappedPath)
            ? box.MappedPath
            : string.IsNullOrWhiteSpace(box.CurrentPath) ? "" : box.CurrentPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var results = new List<DeskItem>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && results.Count < MaxMappedSearchItemsPerBox)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var path in entries)
            {
                if (results.Count >= MaxMappedSearchItemsPerBox)
                {
                    break;
                }

                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.System) != 0 && (attributes & FileAttributes.Hidden) != 0)
                    {
                        continue;
                    }

                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var name = Path.GetFileName(path);
                    var info = isDirectory ? null : new FileInfo(path);
                    var dirInfo = isDirectory ? new DirectoryInfo(path) : null;
                    results.Add(new DeskItem
                    {
                        Path = path,
                        Name = name,
                        DisplayName = ShellInterop.GetDisplayName(path),
                        Extension = isDirectory ? "" : Path.GetExtension(path).ToLowerInvariant(),
                        IsDirectory = isDirectory,
                        IsShortcut = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase),
                        Category = box.Name,
                        BoxId = box.Id,
                        CreatedUtc = isDirectory ? dirInfo!.CreationTimeUtc : info!.CreationTimeUtc,
                        LastAccessUtc = isDirectory ? dirInfo!.LastAccessTimeUtc : info!.LastAccessTimeUtc,
                        LastWriteUtc = isDirectory ? dirInfo!.LastWriteTimeUtc : info!.LastWriteTimeUtc
                    });

                    if (isDirectory)
                    {
                        pending.Push(path);
                    }
                }
                catch
                {
                    // Ignore transient or inaccessible items.
                }
            }
        }

        return results;
    }

    private static SearchResult CreateResult(DeskItem item, BoxModel box, IReadOnlyList<string> terms)
    {
        return new SearchResult
        {
            Item = item,
            BoxId = box.Id,
            BoxName = box.Name,
            TypeName = GetTypeName(item),
            LastUsedUtc = GetRecentUsageUtc(item),
            Score = GetScore(item, box.Name, terms)
        };
    }

    private static bool Matches(DeskItem item, string boxName, IReadOnlyList<string> terms)
    {
        var haystack = string.Join(" ",
            item.DisplayName,
            item.Name,
            item.Extension,
            item.Path,
            boxName,
            item.IsDirectory ? "folder directory 文件夹 目录" : "file 文件",
            item.IsShortcut ? "shortcut 快捷方式" : "");

        return terms.All(term => haystack.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    private static int GetScore(DeskItem item, string boxName, IReadOnlyList<string> terms)
    {
        var score = item.OpenCount * 18 + item.ClickCount * 6;
        var lastUsed = GetRecentUsageUtc(item);
        if (lastUsed > DateTime.MinValue)
        {
            var days = Math.Max(0, (DateTime.UtcNow - lastUsed).TotalDays);
            score += (int)Math.Max(0, 120 - days * 6);
        }

        if (terms.Count == 0)
        {
            return score;
        }

        var name = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Name : item.DisplayName;
        foreach (var term in terms)
        {
            if (name.Equals(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score += 1000;
            }
            else if (name.StartsWith(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score += 760;
            }
            else if (name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score += 540;
            }

            if (!string.IsNullOrWhiteSpace(item.Extension) &&
                item.Extension.TrimStart('.').Equals(term.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            {
                score += 260;
            }

            if (boxName.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score += 160;
            }

            if (item.Path.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score += 90;
            }
        }

        return score;
    }

    private static DateTime GetRecentUsageUtc(DeskItem item)
    {
        var recent = Max(item.LastAccessUtc, item.LastWriteUtc);
        if (item.LastClickedUtc is { } clicked)
        {
            recent = Max(recent, clicked);
        }

        if (item.LastOpenedUtc is { } opened)
        {
            recent = Max(recent, opened);
        }

        return recent;
    }

    private static DateTime Max(DateTime left, DateTime right)
    {
        return left > right ? left : right;
    }

    private static string GetTypeName(DeskItem item)
    {
        if (item.IsDirectory)
        {
            return "文件夹";
        }

        if (item.IsShortcut)
        {
            return "快捷方式";
        }

        var extension = item.Extension.Trim().TrimStart('.');
        return string.IsNullOrWhiteSpace(extension) ? "文件" : extension.ToUpperInvariant();
    }

    private static bool IsPrimaryBoxName(string name)
    {
        return !name.Equals("最近常用", StringComparison.CurrentCultureIgnoreCase) &&
               !name.Equals("今日文件", StringComparison.CurrentCultureIgnoreCase);
    }

    private static IReadOnlyList<string> SplitTerms(string query)
    {
        return (query ?? "")
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
