using CleanDesk.App.Models;

namespace CleanDesk.App.Services;

public sealed class BackupService
{
    public LayoutBackup Create(AppSettings settings, string reason)
    {
        var backup = new LayoutBackup
        {
            Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),
            CreatedUtc = DateTime.UtcNow,
            Reason = reason,
            Items = settings.DesktopItems.Select(item => new BackupDesktopItem
            {
                Path = item.Path,
                Name = item.Name,
                DisplayName = item.DisplayName,
                OriginalPosition = item.OriginalPosition,
                BoxId = item.BoxId,
                Category = item.Category,
                ExistsAtBackup = ShellOperations.Exists(item.Path)
            }).ToList()
        };

        JsonStore.Save(GetPath(backup.Id), backup);
        return backup;
    }

    public LayoutBackup? Load(string id)
    {
        return string.IsNullOrWhiteSpace(id) ? null : JsonStore.Load<LayoutBackup>(GetPath(id));
    }

    public IReadOnlyList<LayoutBackup> List()
    {
        if (!Directory.Exists(PortablePaths.BackupsRoot))
        {
            return [];
        }

        return Directory.EnumerateFiles(PortablePaths.BackupsRoot, "*.json")
            .Select(path =>
            {
                try
                {
                    return JsonStore.Load<LayoutBackup>(path);
                }
                catch
                {
                    return null;
                }
            })
            .Where(backup => backup is not null)
            .Cast<LayoutBackup>()
            .OrderByDescending(backup => backup.CreatedUtc)
            .ToList();
    }

    public string GetPath(string id)
    {
        return Path.Combine(PortablePaths.BackupsRoot, id + ".json");
    }
}
