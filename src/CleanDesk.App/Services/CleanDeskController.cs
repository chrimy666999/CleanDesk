using CleanDesk.App.Models;
using CleanDesk.App.UI;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CleanDesk.App.Services;

public sealed class CleanDeskController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DesktopScanner _scanner = new();
    private readonly BackupService _backups = new();
    private readonly ShellIconCache _icons = new();
    private readonly AdaptiveBoxLayoutService _adaptiveLayout = new();
    private readonly Dictionary<string, BoxWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private BoxLayoutService _layout = null!;
    private TrayService? _tray;
    private CommandPipeServer? _pipe;
    private FileWatcherService? _watcher;
    private SettingsWindow? _settingsWindow;

    public AppSettings Settings { get; private set; } = new();
    public ShellIconCache Icons => _icons;

    public CleanDeskController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Initialize(StartupCommand startupCommand)
    {
        PortablePaths.Ensure();
        Settings = JsonStore.Load<AppSettings>(PortablePaths.SettingsPath) ?? new AppSettings();
        Settings.AutoStart = AutoStartService.IsEnabled();
        _layout = new BoxLayoutService(Settings);

        HandleUnsafePreviousExit();
        Settings.LastSessionCleanExit = false;
        SaveSettings();

        RegistryContextMenuInstaller.Install();
        _scanner.Scan(Settings);
        _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force: false);
        SaveSettings();

        _tray = new TrayService(this);
        _pipe = new CommandPipeServer(_dispatcher, HandleCommand);
        _watcher = new FileWatcherService(_dispatcher, Settings, _scanner.DesktopRoots, OnDesktopChanged);

        if (startupCommand.Kind != StartupCommandKind.None)
        {
            HandleCommand(startupCommand);
        }
        else if (Settings.AutoOrganizeOnStartup && !Settings.PauseTakeover)
        {
            OrganizeDesktop("Startup");
        }
        else
        {
            ShowBoxes();
        }
    }

    public void HandleCommand(StartupCommand command)
    {
        switch (command.Kind)
        {
            case StartupCommandKind.CreateBox:
                CreateBox();
                break;
            case StartupCommandKind.CreateMappedBox:
                CreateMappedBox();
                break;
            case StartupCommandKind.Organize:
                OrganizeDesktop("Command");
                break;
            case StartupCommandKind.Restore:
                RestoreDesktop();
                break;
            case StartupCommandKind.Settings:
                ShowSettings();
                break;
            case StartupCommandKind.Pause:
                TogglePause();
                break;
            case StartupCommandKind.Exit:
                RequestExit();
                break;
            case StartupCommandKind.Show:
            case StartupCommandKind.None:
            default:
                ShowBoxes();
                break;
        }
    }

    public void OrganizeDesktop(string reason)
    {
        try
        {
            _scanner.Scan(Settings);
            _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force: reason is "Startup" or "Command");
            var backup = _backups.Create(Settings, reason);
            if (string.IsNullOrWhiteSpace(Settings.OriginalBackupId) || !Settings.DesktopTakeoverActive)
            {
                Settings.OriginalBackupId = backup.Id;
            }

            Settings.DesktopTakeoverActive = true;
            Settings.LastTakeoverUtc = DateTime.UtcNow;
            Settings.PauseTakeover = false;
            if (Settings.HideScatteredDesktopIcons)
            {
                DesktopInterop.SetDesktopIconsVisible(false);
            }

            ShowBoxes();
            SaveSettings();
            _tray?.ShowBalloon("CleanDesk", "桌面图标已收纳到盒子中。");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Organize desktop failed.");
            Forms.MessageBox.Show("自动整理失败，CleanDesk 已避免移动或删除任何桌面文件。详情请查看 portable-data\\logs。", "CleanDesk", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            DesktopInterop.SetDesktopIconsVisible(true);
        }
    }

    public void RestoreDesktop()
    {
        try
        {
            HideBoxes();
            DesktopInterop.SetDesktopIconsVisible(true);
            var backup = _backups.Load(Settings.OriginalBackupId);
            if (backup is not null)
            {
                DesktopInterop.RestoreIconPositions(backup.Items);
            }

            Settings.DesktopTakeoverActive = false;
            Settings.PauseTakeover = false;
            Settings.OriginalBackupId = "";
            _scanner.Scan(Settings);
            _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force: false);
            SaveSettings();
            _tray?.ShowBalloon("CleanDesk", "桌面已恢复，文件未被删除或移动。");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Restore desktop failed.");
            DesktopInterop.SetDesktopIconsVisible(true);
            Forms.MessageBox.Show("恢复过程中遇到问题，但真实桌面图标已重新显示。详情请查看 portable-data\\logs。", "CleanDesk", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }
    }

    public void ShowBoxes()
    {
        Settings.AllBoxesVisible = true;
        PurgeClosedWindows();
        foreach (var box in Settings.Boxes.Where(box => box.IsVisible))
        {
            var window = GetOrCreateWindow(box);

            try
            {
                window.ApplyModelBounds();
                window.RefreshItems();
                if (!window.IsVisible)
                {
                    window.Show();
                }
            }
            catch (InvalidOperationException ex)
            {
                Logger.Error(ex, "Box window was invalid and will be recreated.");
                _windows.Remove(box.Id);
                window = GetOrCreateWindow(box);
                window.RefreshItems();
                window.Show();
            }
        }

        SaveSettings();
    }

    public void HideBoxes()
    {
        Settings.AllBoxesVisible = false;
        foreach (var pair in _windows.ToArray())
        {
            if (pair.Value.IsClosed)
            {
                _windows.Remove(pair.Key);
                continue;
            }

            try
            {
                pair.Value.Hide();
            }
            catch (InvalidOperationException ex)
            {
                Logger.Error(ex, "Failed to hide a closed box window.");
                _windows.Remove(pair.Key);
            }
        }

        SaveSettings();
    }

    public void ToggleAllBoxes()
    {
        PurgeClosedWindows();
        if (_windows.Values.Any(window => !window.IsClosed && window.IsVisible))
        {
            HideBoxes();
        }
        else
        {
            ShowBoxes();
        }
    }

    public void ShowSettings()
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(this);
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void CreateBox()
    {
        var box = new BoxModel
        {
            Name = "新盒子",
            Bounds = NextBoxBounds(),
            Opacity = Settings.GlobalOpacity,
            HasUserLayout = true,
            DisplayMode = Settings.DefaultDisplayMode
        };
        Settings.Boxes.Add(box);
        SaveSettings();
        ShowBoxes();
    }

    public void CreateMappedBox()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择要映射到桌面盒子的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var name = Path.GetFileName(dialog.SelectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var box = new BoxModel
        {
            Name = string.IsNullOrWhiteSpace(name) ? "映射盒子" : name,
            Kind = BoxKind.Mapped,
            MappedPath = dialog.SelectedPath,
            CurrentPath = dialog.SelectedPath,
            Bounds = NextBoxBounds(),
            Opacity = Settings.GlobalOpacity,
            HasUserLayout = true,
            DisplayMode = BoxDisplayMode.List
        };
        Settings.Boxes.Add(box);
        SaveSettings();
        ShowBoxes();
    }

    public void RemoveBox(BoxModel box)
    {
        if (box.Kind == BoxKind.Normal && box.ItemPaths.Count > 0)
        {
            var result = Forms.MessageBox.Show("解散盒子只会释放显示分组，不会删除文件。是否继续？", "CleanDesk", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question);
            if (result != Forms.DialogResult.Yes)
            {
                return;
            }
        }

        if (_windows.Remove(box.Id, out var window))
        {
            window.CloseForReal();
        }

        Settings.Boxes.Remove(box);
        _scanner.Scan(Settings);
        _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force: false);
        SaveSettings();
        ShowBoxes();
    }

    public IReadOnlyList<DeskItem> GetItemsForBox(BoxModel box)
    {
        if (box.Kind == BoxKind.Mapped)
        {
            return EnumerateMappedItems(box);
        }

        if (box.Name.Equals("最近常用", StringComparison.CurrentCultureIgnoreCase))
        {
            return Settings.DesktopItems
                .Where(item => item.OpenCount > 0 || item.LastAccessUtc > DateTime.UtcNow.AddDays(-14))
                .OrderByDescending(item => item.OpenCount)
                .ThenByDescending(item => item.LastOpenedUtc ?? item.LastAccessUtc)
                .Take(40)
                .ToList();
        }

        if (box.Name.Equals("今日文件", StringComparison.CurrentCultureIgnoreCase))
        {
            var today = DateTime.Today;
            return Settings.DesktopItems
                .Where(item => item.CreatedUtc.ToLocalTime().Date == today || item.LastWriteUtc.ToLocalTime().Date == today)
                .OrderByDescending(item => item.LastWriteUtc)
                .ToList();
        }

        return Settings.DesktopItems
            .Where(item => item.BoxId == box.Id || box.ItemPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void OpenItem(DeskItem item)
    {
        ShellOperations.Open(item.Path);
        var match = Settings.DesktopItems.FirstOrDefault(existing => existing.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            match.OpenCount++;
            match.LastOpenedUtc = DateTime.UtcNow;
            SaveSettings();
        }
    }

    public void RefreshAfterShellChange()
    {
        _scanner.Scan(Settings);
        _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force: false);
        PurgeClosedWindows();
        foreach (var window in _windows.Values.Where(window => !window.IsClosed))
        {
            window.ApplyModelBounds();
            window.RefreshItems();
        }

        SaveSettings();
    }

    public DesktopRect Snap(BoxModel box, DesktopRect candidate, bool resize)
    {
        return _layout.Snap(box.Id, candidate, resize);
    }

    public void ApplyLayoutPreset(string presetId, bool force)
    {
        AdaptiveBoxLayoutService.EnsurePresets(Settings);
        if (Settings.LayoutPresets.All(preset => preset.Id != presetId))
        {
            return;
        }

        Settings.ActiveLayoutPresetId = presetId;
        if (force)
        {
            foreach (var box in Settings.Boxes)
            {
                box.HasUserLayout = false;
            }
        }

        _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force);
        PurgeClosedWindows();
        foreach (var window in _windows.Values.Where(window => !window.IsClosed))
        {
            window.ApplyModelBounds();
            window.RefreshItems();
        }

        SaveSettings();
    }

    public BoxLayoutPreset AddLayoutPreset(string name, BoxLayoutAlignment alignment)
    {
        AdaptiveBoxLayoutService.EnsurePresets(Settings);
        var preset = new BoxLayoutPreset
        {
            Name = string.IsNullOrWhiteSpace(name) ? "自定义排列" : name.Trim(),
            Alignment = alignment,
            Gap = Settings.BoxGap,
            AutoSize = true,
            CollapseEmptyBoxes = true
        };
        Settings.LayoutPresets.Add(preset);
        SaveSettings();
        return preset;
    }

    public bool DeleteLayoutPreset(string presetId)
    {
        if (presetId is "left" or "right" or "top" or "bottom")
        {
            return false;
        }

        var preset = Settings.LayoutPresets.FirstOrDefault(item => item.Id == presetId);
        if (preset is null)
        {
            return false;
        }

        Settings.LayoutPresets.Remove(preset);
        if (Settings.ActiveLayoutPresetId == presetId)
        {
            Settings.ActiveLayoutPresetId = "left";
        }

        AdaptiveBoxLayoutService.EnsurePresets(Settings);
        SaveSettings();
        return true;
    }

    public void SaveSettings()
    {
        JsonStore.Save(PortablePaths.SettingsPath, Settings);
    }

    public void RequestExit()
    {
        if (Settings.DesktopTakeoverActive)
        {
            var result = Forms.MessageBox.Show(
                "当前桌面处于 CleanDesk 收纳状态。\n\n是：保持当前收纳状态并退出\n否：恢复原桌面布局后退出\n取消：取消退出",
                "退出 CleanDesk",
                Forms.MessageBoxButtons.YesNoCancel,
                Forms.MessageBoxIcon.Question,
                Forms.MessageBoxDefaultButton.Button2);

            if (result == Forms.DialogResult.Cancel)
            {
                return;
            }

            if (result == Forms.DialogResult.No)
            {
                RestoreDesktop();
            }
        }

        Settings.LastSessionCleanExit = true;
        SaveSettings();
        System.Windows.Application.Current.Shutdown();
    }

    public void MarkCleanExit()
    {
        try
        {
            Settings.LastSessionCleanExit = true;
            SaveSettings();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to mark clean exit.");
        }
    }

    public void HandleRecoverableException(Exception exception)
    {
        try
        {
            SaveSettings();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed while handling recoverable exception.");
        }
    }

    public void Dispose()
    {
        try
        {
            Settings.LastSessionCleanExit = true;
            SaveSettings();
        }
        catch
        {
            // ignored
        }

        _watcher?.Dispose();
        _pipe?.Dispose();
        _tray?.Dispose();
    }

    private void TogglePause()
    {
        Settings.PauseTakeover = !Settings.PauseTakeover;
        if (Settings.PauseTakeover)
        {
            DesktopInterop.SetDesktopIconsVisible(true);
        }
        else if (Settings.DesktopTakeoverActive && Settings.HideScatteredDesktopIcons)
        {
            DesktopInterop.SetDesktopIconsVisible(false);
        }

        SaveSettings();
    }

    private void OnDesktopChanged()
    {
        try
        {
            if (Settings.PauseTakeover)
            {
                return;
            }

            _scanner.Scan(Settings);
            _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force: false);
            if (Settings.DesktopTakeoverActive && Settings.HideScatteredDesktopIcons)
            {
                DesktopInterop.SetDesktopIconsVisible(false);
            }

            PurgeClosedWindows();
            foreach (var window in _windows.Values.Where(window => !window.IsClosed))
            {
                window.ApplyModelBounds();
                window.RefreshItems();
            }

            SaveSettings();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Desktop watcher refresh failed.");
        }
    }

    private void HandleUnsafePreviousExit()
    {
        if (Settings.LastSessionCleanExit || !Settings.DesktopTakeoverActive)
        {
            return;
        }

        var result = Forms.MessageBox.Show(
            "CleanDesk 上次可能异常退出。\n\n是：继续使用 CleanDesk 布局\n否：恢复原始桌面布局\n取消：安全模式启动",
            "CleanDesk 异常恢复",
            Forms.MessageBoxButtons.YesNoCancel,
            Forms.MessageBoxIcon.Warning,
            Forms.MessageBoxDefaultButton.Button2);

        if (result == Forms.DialogResult.No)
        {
            var backup = _backups.Load(Settings.OriginalBackupId);
            DesktopInterop.SetDesktopIconsVisible(true);
            if (backup is not null)
            {
                DesktopInterop.RestoreIconPositions(backup.Items);
            }

            Settings.DesktopTakeoverActive = false;
            Settings.OriginalBackupId = "";
        }
        else if (result == Forms.DialogResult.Cancel)
        {
            Settings.PauseTakeover = true;
            DesktopInterop.SetDesktopIconsVisible(true);
        }
    }

    private DesktopRect NextBoxBounds()
    {
        var work = GetPrimaryWorkArea();
        var width = Math.Clamp(Settings.DefaultBoxWidth, Settings.MinBoxWidth, Settings.MaxBoxWidth);
        var height = Math.Clamp(Settings.DefaultBoxHeight, Math.Max(Settings.MinBoxHeight, 96), Settings.MaxBoxHeight);
        var gap = Math.Clamp(Settings.BoxGap <= 0 ? 8 : Settings.BoxGap, 6, 10);
        var existing = Settings.Boxes.Where(box => box.IsVisible).Select(box => box.Bounds).ToList();

        for (var row = 0; row < 24; row++)
        {
            for (var col = 0; col < 24; col++)
            {
                var x = work.Left + gap + col * (width + gap);
                var y = work.Top + gap + row * (height + gap);
                var candidate = new DesktopRect { X = x, Y = y, Width = width, Height = height };
                if (FitsInWorkArea(candidate, work) && !IntersectsAny(candidate, existing, gap))
                {
                    return candidate;
                }
            }
        }

        return new DesktopRect
        {
            X = work.Left + gap,
            Y = work.Top + gap,
            Width = width,
            Height = height
        };
    }

    private static bool FitsInWorkArea(DesktopRect rect, WorkArea work)
    {
        return rect.X >= work.Left &&
               rect.Y >= work.Top &&
               rect.X + rect.Width <= work.Right &&
               rect.Y + rect.Height <= work.Bottom;
    }

    private static bool IntersectsAny(DesktopRect candidate, IEnumerable<DesktopRect> existing, int gap)
    {
        foreach (var rect in existing)
        {
            if (candidate.X < rect.X + rect.Width + gap &&
                candidate.X + candidate.Width + gap > rect.X &&
                candidate.Y < rect.Y + rect.Height + gap &&
                candidate.Y + candidate.Height + gap > rect.Y)
            {
                return true;
            }
        }

        return false;
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
            // Fall back to WinForms only when WPF work-area metrics are unavailable.
        }

        var fallback = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(20, 20, 1200, 800);
        return new WorkArea(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    private readonly record struct WorkArea(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    private int GetItemCountForLayout(BoxModel box)
    {
        return GetItemsForBox(box).Count;
    }

    private BoxWindow GetOrCreateWindow(BoxModel box)
    {
        if (_windows.TryGetValue(box.Id, out var window) && !window.IsClosed)
        {
            return window;
        }

        window = new BoxWindow(this, box);
        _windows[box.Id] = window;
        return window;
    }

    private void PurgeClosedWindows()
    {
        foreach (var pair in _windows.Where(pair => pair.Value.IsClosed).ToArray())
        {
            _windows.Remove(pair.Key);
        }
    }

    private static IReadOnlyList<DeskItem> EnumerateMappedItems(BoxModel box)
    {
        var current = string.IsNullOrWhiteSpace(box.CurrentPath) ? box.MappedPath : box.CurrentPath;
        if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current))
        {
            return [];
        }

        var items = new List<DeskItem>();
        foreach (var path in Directory.EnumerateFileSystemEntries(current))
        {
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
                items.Add(new DeskItem
                {
                    Path = path,
                    Name = name,
                    DisplayName = ShellInterop.GetDisplayName(path),
                    Extension = isDirectory ? "" : Path.GetExtension(path).ToLowerInvariant(),
                    IsDirectory = isDirectory,
                    IsShortcut = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase),
                    CreatedUtc = isDirectory ? dirInfo!.CreationTimeUtc : info!.CreationTimeUtc,
                    LastAccessUtc = isDirectory ? dirInfo!.LastAccessTimeUtc : info!.LastAccessTimeUtc,
                    LastWriteUtc = isDirectory ? dirInfo!.LastWriteTimeUtc : info!.LastWriteTimeUtc
                });
            }
            catch
            {
                // Ignore transient files.
            }
        }

        return items.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
