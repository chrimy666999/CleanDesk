using System.Windows.Media;

namespace CleanDesk.App.Services;

public sealed class ShellIconCache
{
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource GetIcon(string path, int size)
    {
        var key = $"{size}|{path}";
        if (_cache.TryGetValue(key, out var icon))
        {
            return icon;
        }

        icon = ShellInterop.GetIcon(path, size);
        _cache[key] = icon;
        return icon;
    }

    public void ClearMissing(IEnumerable<string> activePaths)
    {
        var active = activePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _cache.Keys.ToArray())
        {
            var split = key.IndexOf('|');
            if (split < 0 || !active.Contains(key[(split + 1)..]))
            {
                _cache.Remove(key);
            }
        }
    }
}
