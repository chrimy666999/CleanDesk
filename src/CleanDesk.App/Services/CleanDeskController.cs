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
    private const int TitleButtonCount = 6;
    private const double SideTitleButtonStride = 26;
    private const double SideTitleChromePadding = 40;
    private const double SideTitleCharacterHeight = 16;
    private readonly Dictionary<string, BoxWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private DesktopForegroundWatcher? _foregroundWatcher;
    private BoxLayoutService _layout = null!;
    private TrayService? _tray;
    private CommandPipeServer? _pipe;
    private FileWatcherService? _watcher;
    private DialogAccessService? _dialogAccess;
    private SettingsWindow? _settingsWindow;
    private GlobalSearchWindow? _globalSearchWindow;
    private GlobalHotkeyService? _globalHotkey;
    private bool _recentUsageRefreshQueued;

    public AppSettings Settings { get; private set; } = new();
    public ShellIconCache Icons => _icons;
    public IReadOnlyList<string> DesktopRoots => _scanner.DesktopRoots;
    public IReadOnlyList<BoxModel> VisibleBoxes => Settings.Boxes.Where(box => box.IsVisible).ToList();

    public CleanDeskController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Initialize(StartupCommand startupCommand)
    {
        PortablePaths.Ensure();
        Settings = JsonStore.Load<AppSettings>(PortablePaths.SettingsPath) ?? new AppSettings();
        var shouldMigrateDockPlan = !string.Equals(Settings.Version, AppSettings.CurrentVersion, StringComparison.OrdinalIgnoreCase);
        if (shouldMigrateDockPlan)
        {
            MigrateTemporaryWorkspaceName();
            MigrateMagneticAccessBoxOrderFlag();
        }

        Settings.Version = AppSettings.CurrentVersion;
        if (shouldMigrateDockPlan)
        {
            ApplyMinimumDefaultOpacity();
        }

        Settings.AutoStart = AutoStartService.IsEnabled();
        _layout = new BoxLayoutService(Settings);

        HandleUnsafePreviousExit();
        Settings.LastSessionCleanExit = false;
        SaveSettings();

        RegistryContextMenuInstaller.Install();
        _scanner.Scan(Settings);
        _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force: shouldMigrateDockPlan);
        _layout.ResolveOverlaps("");
        if (shouldMigrateDockPlan)
        {
            ApplyDefaultBoundaryDockPositions();
        }

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

        _foregroundWatcher = new DesktopForegroundWatcher(_dispatcher, OnForegroundWindowChanged);
        _dialogAccess = new DialogAccessService(_dispatcher, this);
        _globalHotkey = new GlobalHotkeyService(_dispatcher, ShowGlobalSearch);
        _globalHotkey.Register();
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
            _layout.ResolveOverlaps("");
            if (reason is "Startup" or "Command")
            {
                ApplyDefaultBoundaryDockPositions();
            }

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
            DesktopInterop.SetDesktopIconsVisible(true);
            var backup = _backups.Load(Settings.OriginalBackupId);
            if (backup is not null)
            {
                DesktopInterop.RestoreIconPositions(backup.Items);
            }

            HideBoxes();
            Settings.DesktopTakeoverActive = false;
            Settings.PauseTakeover = false;
            Settings.OriginalBackupId = "";
            _scanner.Scan(Settings);
            _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force: false);
            _layout.ResolveOverlaps("");
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

                window.EnsureDesktopPlacement();
            }
            catch (InvalidOperationException ex)
            {
                Logger.Error(ex, "Box window was invalid and will be recreated.");
                _windows.Remove(box.Id);
                window = GetOrCreateWindow(box);
                window.RefreshItems();
                window.Show();
                window.EnsureDesktopPlacement();
            }
        }

        SaveSettings();
        _dialogAccess?.RefreshContent();
    }

    public void ResetDefaultBoundaryLayout()
    {
        ApplyDefaultBoundaryDockPositions();
        ShowBoxes();
        SaveSettings();
    }

    private void OnForegroundWindowChanged()
    {
        if (DesktopInterop.IsDesktopForeground())
        {
            ClearAllBoxSelections();
            EnsureDesktopBoxesVisible(allowNativeShow: false);
        }
    }

    private void ClearAllBoxSelections()
    {
        PurgeClosedWindows();
        foreach (var window in _windows.Values.Where(window => !window.IsClosed))
        {
            try
            {
                window.ClearSelectionAndCollapseIfEmpty();
            }
            catch (InvalidOperationException ex)
            {
                Logger.Error(ex, "Failed to clear box selection.");
            }
        }
    }

    private void EnsureDesktopBoxesVisible(bool allowNativeShow)
    {
        if (!Settings.AllBoxesVisible)
        {
            return;
        }

        PurgeClosedWindows();
        foreach (var window in _windows.Values.Where(window => !window.IsClosed))
        {
            try
            {
                if (!window.IsVisible)
                {
                    window.Show();
                }

                window.EnsureDesktopPlacement(allowNativeShow);
            }
            catch (InvalidOperationException ex)
            {
                Logger.Error(ex, "Failed to keep a box window attached to desktop.");
            }
        }
    }

    public void RefreshBoxVisuals()
    {
        PurgeClosedWindows();
        foreach (var window in _windows.Values.Where(window => !window.IsClosed))
        {
            try
            {
                window.RefreshVisualStyle();
            }
            catch (InvalidOperationException ex)
            {
                Logger.Error(ex, "Failed to refresh box visual style.");
            }
        }
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
        _dialogAccess?.RefreshContent();
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

    public void ShowGlobalSearch()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(ShowGlobalSearch), DispatcherPriority.Normal);
            return;
        }

        if (_globalSearchWindow is null || !_globalSearchWindow.IsLoaded)
        {
            _globalSearchWindow = new GlobalSearchWindow(this);
        }

        _globalSearchWindow.ShowSearch();
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
                .Where(item => item.OpenCount > 0 || item.ClickCount > 0 || item.LastAccessUtc > DateTime.UtcNow.AddDays(-14))
                .OrderByDescending(GetRecentUsageScore)
                .ThenByDescending(GetRecentUsageUtc)
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
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
            QueueRecentUsageRefresh();
        }
    }

    public void OpenSearchResult(SearchResult result)
    {
        OpenItem(result.Item);
    }

    public void OpenSearchResultFolder(SearchResult result)
    {
        if (Directory.Exists(result.Path))
        {
            ShellOperations.Open(result.Path);
            return;
        }

        ShellOperations.OpenContainingFolder(result.Path);
    }

    public void RevealSearchResult(SearchResult result)
    {
        if (string.IsNullOrWhiteSpace(result.BoxId))
        {
            OpenSearchResultFolder(result);
            return;
        }

        var box = Settings.Boxes.FirstOrDefault(item => item.Id.Equals(result.BoxId, StringComparison.OrdinalIgnoreCase));
        if (box is null)
        {
            OpenSearchResultFolder(result);
            return;
        }

        ShowBoxes();
        if (_windows.TryGetValue(box.Id, out var window) && !window.IsClosed)
        {
            window.RevealPath(result.Path);
        }
    }

    public void RecordItemClick(DeskItem item)
    {
        var match = Settings.DesktopItems.FirstOrDefault(existing => existing.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        match.ClickCount++;
        match.LastClickedUtc = DateTime.UtcNow;
        SaveSettings();
        QueueRecentUsageRefresh();
    }

    private static int GetRecentUsageScore(DeskItem item)
    {
        return item.OpenCount * 5 + item.ClickCount;
    }

    private static DateTime GetRecentUsageUtc(DeskItem item)
    {
        var recent = item.LastAccessUtc;
        if (item.LastClickedUtc is { } clicked && clicked > recent)
        {
            recent = clicked;
        }

        if (item.LastOpenedUtc is { } opened && opened > recent)
        {
            recent = opened;
        }

        return recent;
    }

    private void QueueRecentUsageRefresh()
    {
        if (_recentUsageRefreshQueued)
        {
            return;
        }

        _recentUsageRefreshQueued = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _recentUsageRefreshQueued = false;
            RefreshRecentUsageBoxes();
        }), DispatcherPriority.Background);
    }

    private void RefreshRecentUsageBoxes()
    {
        PurgeClosedWindows();
        foreach (var box in Settings.Boxes.Where(IsRecentUsageBox))
        {
            if (_windows.TryGetValue(box.Id, out var window) && !window.IsClosed)
            {
                window.RefreshItems();
            }
        }
    }

    private static bool IsRecentUsageBox(BoxModel box)
    {
        return box.Name.Equals("最近常用", StringComparison.CurrentCultureIgnoreCase);
    }

    public bool ImportDroppedFiles(BoxModel targetBox, IReadOnlyList<string> paths, bool move)
    {
        if (targetBox.Kind is not (BoxKind.Normal or BoxKind.Mapped) || paths.Count == 0)
        {
            return false;
        }

        var targetDirectory = ResolveImportDirectory(targetBox);
        if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
        {
            return false;
        }

        var importedPaths = new List<string>();
        foreach (var sourcePath in paths.Where(ShellOperations.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var imported = ImportOnePath(sourcePath, targetDirectory, move);
                if (!string.IsNullOrWhiteSpace(imported))
                {
                    importedPaths.Add(imported);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to import dropped item: {sourcePath}");
            }
        }

        if (importedPaths.Count == 0)
        {
            return false;
        }

        if (targetBox.Kind == BoxKind.Normal)
        {
            Settings.ItemBoxOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in importedPaths)
            {
                Settings.ItemBoxOverrides[path] = targetBox.Id;
            }
        }

        _scanner.Scan(Settings);
        if (targetBox.Kind == BoxKind.Normal)
        {
            foreach (var path in importedPaths)
            {
                Settings.ItemBoxOverrides[path] = targetBox.Id;
                var item = Settings.DesktopItems.FirstOrDefault(existing => existing.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                {
                    item.BoxId = targetBox.Id;
                    item.Category = targetBox.Name;
                }

                if (!targetBox.ItemPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    targetBox.ItemPaths.Add(path);
                }
            }
        }

        SaveSettings();
        return true;
    }

    public string GetImportDirectory(BoxModel targetBox)
    {
        return ResolveImportDirectory(targetBox);
    }

    public bool IsTemporaryWorkspaceBox(BoxModel box)
    {
        return IsTemporaryWorkspaceName(box.Name);
    }

    public void RefreshAfterShellChange(bool reflowLayout = false, bool forceRefresh = false)
    {
        var before = CreateDesktopVisualSignature();
        _scanner.Scan(Settings);
        var changed = !string.Equals(before, CreateDesktopVisualSignature(), StringComparison.Ordinal);
        if (!forceRefresh && !changed && !reflowLayout)
        {
            return;
        }

        if (reflowLayout)
        {
            _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force: false);
            _layout.ResolveOverlaps("");
        }

        PurgeClosedWindows();
        foreach (var window in _windows.Values.Where(window => !window.IsClosed))
        {
            window.ApplyModelBounds();
            window.RefreshItems();
        }

        SaveSettings();
        _dialogAccess?.RefreshContent();
    }

    public DesktopRect Snap(BoxModel box, DesktopRect candidate, bool resize)
    {
        return _layout.Snap(box.Id, candidate, resize);
    }

    public void ResolveBoxOverlaps(BoxModel changedBox)
    {
        _layout.ResolveOverlaps(changedBox.Id);
        PurgeClosedWindows();
        foreach (var window in _windows.Values.Where(window => !window.IsClosed))
        {
            window.ApplyModelBounds();
        }

        SaveSettings();
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
        _layout.ResolveOverlaps("");
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
        _globalHotkey?.Dispose();
        _foregroundWatcher?.Dispose();
        _dialogAccess?.Dispose();
        _globalSearchWindow?.CloseForShutdown();
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

            var before = CreateDesktopVisualSignature();
            _scanner.Scan(Settings);
            if (string.Equals(before, CreateDesktopVisualSignature(), StringComparison.Ordinal))
            {
                return;
            }

            _adaptiveLayout.Apply(Settings, GetItemCountForLayout, force: false);
            _layout.ResolveOverlaps("");
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
            _dialogAccess?.RefreshContent();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Desktop watcher refresh failed.");
        }
    }

    private void ApplyMinimumDefaultOpacity()
    {
        Settings.GlobalOpacity = 0.05;
        foreach (var box in Settings.Boxes)
        {
            box.Opacity = 0.05;
        }
    }

    private void MigrateTemporaryWorkspaceName()
    {
        Settings.ItemBoxOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var workspace = Settings.Boxes.FirstOrDefault(box => IsTemporaryWorkspaceName(box.Name));
        if (workspace is not null)
        {
            workspace.Name = "临时工作区";
        }

        foreach (var duplicate in Settings.Boxes
                     .Where(box => !ReferenceEquals(box, workspace) && IsTemporaryWorkspaceName(box.Name))
                     .ToList())
        {
            if (workspace is null)
            {
                duplicate.Name = "临时工作区";
                workspace = duplicate;
                continue;
            }

            foreach (var path in duplicate.ItemPaths)
            {
                if (!workspace.ItemPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    workspace.ItemPaths.Add(path);
                }
            }

            foreach (var path in Settings.ItemBoxOverrides
                         .Where(pair => pair.Value.Equals(duplicate.Id, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToList())
            {
                Settings.ItemBoxOverrides[path] = workspace.Id;
            }

            Settings.Boxes.Remove(duplicate);
        }

        foreach (var rule in Settings.Rules.Where(rule => IsTemporaryWorkspaceName(rule.TargetBoxName)))
        {
            rule.TargetBoxName = "临时工作区";
        }

        foreach (var item in Settings.DesktopItems.Where(item => IsTemporaryWorkspaceName(item.Category)))
        {
            item.Category = "临时工作区";
        }
    }

    private void MigrateMagneticAccessBoxOrderFlag()
    {
        if (Settings.MagneticAccessBoxOrderCustomized || Settings.MagneticAccessBoxOrder.Count == 0)
        {
            return;
        }

        var visibleBoxes = Settings.Boxes.Where(box => box.IsVisible).ToList();
        var byId = visibleBoxes.ToDictionary(box => box.Id, StringComparer.OrdinalIgnoreCase);
        var savedOrder = Settings.MagneticAccessBoxOrder
            .Where(id => byId.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (savedOrder.Count == 0)
        {
            return;
        }

        var previousDefaultOrder = visibleBoxes
            .OrderBy(GetMagneticAccessDockGroup)
            .ThenBy(GetMagneticAccessAxisPosition)
            .ThenBy(box => box.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(box => box.Id)
            .ToList();
        var preferredDefaultOrder = visibleBoxes
            .OrderBy(GetPreferredMagneticAccessOrderRank)
            .ThenBy(GetMagneticAccessDockGroup)
            .ThenBy(GetMagneticAccessAxisPosition)
            .ThenBy(box => box.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(box => box.Id)
            .ToList();

        if (!savedOrder.SequenceEqual(previousDefaultOrder, StringComparer.OrdinalIgnoreCase) &&
            !savedOrder.SequenceEqual(preferredDefaultOrder, StringComparer.OrdinalIgnoreCase))
        {
            Settings.MagneticAccessBoxOrderCustomized = true;
        }
    }

    private void ApplyDefaultBoundaryDockPositions()
    {
        var recent = Settings.Boxes.FirstOrDefault(box => box.Name.Equals("最近常用", StringComparison.CurrentCultureIgnoreCase));
        var shortcuts = Settings.Boxes.FirstOrDefault(box => box.Name.Equals("快捷方式", StringComparison.CurrentCultureIgnoreCase));
        var directories = Settings.Boxes.FirstOrDefault(box => box.Name.Equals("目录", StringComparison.CurrentCultureIgnoreCase));
        var documents = Settings.Boxes.FirstOrDefault(box => box.Name.Equals("文档", StringComparison.CurrentCultureIgnoreCase));
        var image = Settings.Boxes.FirstOrDefault(box => box.Name.Equals("图片", StringComparison.CurrentCultureIgnoreCase));
        var media = Settings.Boxes.FirstOrDefault(box => box.Name.Equals("音乐视频", StringComparison.CurrentCultureIgnoreCase));
        var archive = Settings.Boxes.FirstOrDefault(box => box.Name.Equals("压缩包", StringComparison.CurrentCultureIgnoreCase));
        var today = Settings.Boxes.FirstOrDefault(box => box.Name.Equals("今日文件", StringComparison.CurrentCultureIgnoreCase));
        var workspace = Settings.Boxes.FirstOrDefault(box => box.Name.Equals("临时工作区", StringComparison.CurrentCultureIgnoreCase));
        var other = Settings.Boxes.FirstOrDefault(box => box.Name.Equals("其他", StringComparison.CurrentCultureIgnoreCase));
        if (today is null || workspace is null || other is null)
        {
            return;
        }

        var work = GetPrimaryWorkArea();
        var gap = Math.Clamp(Settings.BoxGap <= 0 ? 18 : Settings.BoxGap, 12, 28);
        var defaultTopHeight = Math.Clamp(Math.Max(220, Settings.DefaultBoxHeight), 180, Math.Max(180, work.Height));

        foreach (var box in new[] { recent, shortcuts, directories, documents }.Where(box => box is not null).Cast<BoxModel>())
        {
            box.DockEdge = BoxDockEdge.Top;
            box.IsCollapsed = true;
            box.HasUserLayout = false;
            if (box.Bounds.Height < defaultTopHeight || box.LastExpandedHeight < defaultTopHeight)
            {
                box.Bounds.Height = defaultTopHeight;
                box.LastExpandedHeight = defaultTopHeight;
            }
        }

        foreach (var box in new[] { image, media, archive }.Where(box => box is not null).Cast<BoxModel>())
        {
            box.DockEdge = BoxDockEdge.Left;
            box.IsCollapsed = true;
            box.HasUserLayout = false;
            SetSideTitleLength(box, ClampSideTitleLength(
                Math.Max(EstimateSideTitleLength(box.Name), Math.Max(box.TitleLength, Math.Max(box.Bounds.Height, box.LastExpandedHeight))),
                work));
            box.Bounds.X = work.Left;
        }

        today.DockEdge = BoxDockEdge.Right;
        workspace.DockEdge = BoxDockEdge.Right;
        other.DockEdge = BoxDockEdge.Right;
        today.IsCollapsed = true;
        workspace.IsCollapsed = true;
        other.IsCollapsed = true;
        today.HasUserLayout = false;
        workspace.HasUserLayout = false;
        other.HasUserLayout = false;

        var todayLength = ClampSideTitleLength(
            Math.Max(EstimateSideTitleLength(today.Name), Math.Max(today.TitleLength, Math.Max(today.Bounds.Height, today.LastExpandedHeight))),
            work);
        var otherLength = ClampSideTitleLength(
            Math.Max(EstimateSideTitleLength(other.Name), Math.Max(other.TitleLength, Math.Max(other.Bounds.Height, other.LastExpandedHeight))),
            work);
        var workspaceLength = ClampSideTitleLength(
            Math.Max(EstimateSideTitleLength(workspace.Name), otherLength),
            work);

        SetSideTitleLength(today, todayLength);
        SetSideTitleLength(workspace, workspaceLength);
        SetSideTitleLength(other, workspaceLength);

        if (archive is not null)
        {
            archive.Bounds.Y = Math.Clamp(work.Bottom - archive.Bounds.Height, work.Top, Math.Max(work.Top, work.Bottom - archive.Bounds.Height));
            if (media is not null)
            {
                media.Bounds.Y = Math.Clamp(archive.Bounds.Y - gap - media.Bounds.Height, work.Top, Math.Max(work.Top, work.Bottom - media.Bounds.Height));
            }

            if (image is not null)
            {
                var anchor = media ?? archive;
                image.Bounds.Y = Math.Clamp(anchor.Bounds.Y - gap - image.Bounds.Height, work.Top, Math.Max(work.Top, work.Bottom - image.Bounds.Height));
            }
        }

        other.Bounds.X = Math.Max(work.Left, work.Right - Math.Max(other.Bounds.Width, other.LastExpandedWidth));
        workspace.Bounds.X = Math.Max(work.Left, work.Right - Math.Max(workspace.Bounds.Width, workspace.LastExpandedWidth));
        today.Bounds.X = Math.Max(work.Left, work.Right - Math.Max(today.Bounds.Width, today.LastExpandedWidth));

        other.Bounds.Y = Math.Clamp(work.Bottom - other.Bounds.Height, work.Top, Math.Max(work.Top, work.Bottom - other.Bounds.Height));
        workspace.Bounds.Y = Math.Clamp(other.Bounds.Y - gap - workspace.Bounds.Height, work.Top, Math.Max(work.Top, work.Bottom - workspace.Bounds.Height));
        today.Bounds.Y = Math.Clamp(workspace.Bounds.Y - gap - today.Bounds.Height, work.Top, Math.Max(work.Top, work.Bottom - today.Bounds.Height));
    }

    private static void SetSideTitleLength(BoxModel box, double length)
    {
        box.TitleLength = length;
        box.Bounds.Height = length;
        box.LastExpandedHeight = length;
    }

    private static double ClampSideTitleLength(double length, WorkArea work)
    {
        return Math.Clamp(Math.Max(120, length), 120, Math.Max(120, work.Height));
    }

    private static double EstimateSideTitleLength(string title)
    {
        var text = string.IsNullOrWhiteSpace(title) ? "盒子" : title.Trim();
        return Math.Clamp(
            TitleButtonCount * SideTitleButtonStride + SideTitleChromePadding + text.Length * SideTitleCharacterHeight,
            244,
            9000);
    }

    private static bool IsTemporaryWorkspaceName(string? name)
    {
        return name?.Trim() is "临时收纳区" or "临时工作区";
    }

    private static int GetPreferredMagneticAccessOrderRank(BoxModel box)
    {
        var preferred = Array.IndexOf(PreferredMagneticAccessBoxNames, box.Name);
        return preferred >= 0 ? preferred : int.MaxValue;
    }

    private static int GetMagneticAccessDockGroup(BoxModel box)
    {
        return box.DockEdge switch
        {
            BoxDockEdge.Top => 0,
            BoxDockEdge.Left => 1,
            BoxDockEdge.Right => 2,
            _ => 3
        };
    }

    private static double GetMagneticAccessAxisPosition(BoxModel box)
    {
        return box.DockEdge == BoxDockEdge.Top ? box.Bounds.X : box.Bounds.Y;
    }

    private static readonly string[] PreferredMagneticAccessBoxNames =
    [
        "临时工作区",
        "今日文件",
        "文档",
        "目录",
        "图片",
        "音乐视频",
        "压缩包",
        "最近常用",
        "快捷方式",
        "其他"
    ];

    private string CreateDesktopVisualSignature()
    {
        var itemSignature = Settings.DesktopItems
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => string.Join('\u001f',
                item.Path,
                item.DisplayName,
                item.Category,
                item.BoxId,
                item.IsDirectory ? "D" : "F",
                item.IsShortcut ? "L" : "-"));
        var boxSignature = Settings.Boxes
            .OrderBy(box => box.Id, StringComparer.OrdinalIgnoreCase)
            .Select(box => string.Join('\u001f',
                box.Id,
                box.Name,
                box.Kind,
                box.DisplayMode,
                box.DockEdge,
                box.IsVisible,
                box.IsCollapsed,
                string.Join('\u001e', box.ItemPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))));

        return string.Join('\u001d', boxSignature) + "\u001c" + string.Join('\u001d', itemSignature);
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

    private string ResolveImportDirectory(BoxModel targetBox)
    {
        if (targetBox.Kind == BoxKind.Mapped)
        {
            var current = string.IsNullOrWhiteSpace(targetBox.CurrentPath) ? targetBox.MappedPath : targetBox.CurrentPath;
            if (Directory.Exists(current))
            {
                return current;
            }

            return Directory.Exists(targetBox.MappedPath) ? targetBox.MappedPath : "";
        }

        return _scanner.DesktopRoots.FirstOrDefault(Directory.Exists)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private static string ImportOnePath(string sourcePath, string targetDirectory, bool move)
    {
        var fileName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "";
        }

        var targetPath = Path.Combine(targetDirectory, fileName);
        if (PathsEqual(sourcePath, targetPath))
        {
            return sourcePath;
        }

        targetPath = GetUniqueImportPath(targetPath);
        if (Directory.Exists(sourcePath))
        {
            if (move)
            {
                Directory.Move(sourcePath, targetPath);
            }
            else
            {
                CopyDirectory(sourcePath, targetPath);
            }
        }
        else
        {
            if (move)
            {
                File.Move(sourcePath, targetPath);
            }
            else
            {
                File.Copy(sourcePath, targetPath);
            }
        }

        return targetPath;
    }

    private static string GetUniqueImportPath(string preferredPath)
    {
        if (!ShellOperations.Exists(preferredPath))
        {
            return preferredPath;
        }

        var directory = Path.GetDirectoryName(preferredPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(preferredPath);
        var extension = Path.GetExtension(preferredPath);
        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!ShellOperations.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var targetFile = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private DesktopRect NextBoxBounds()
    {
        var work = GetPrimaryWorkArea();
        var width = Math.Clamp(Settings.DefaultBoxWidth, Settings.MinBoxWidth, Settings.MaxBoxWidth);
        var height = Math.Clamp(Settings.DefaultBoxHeight, Math.Max(Settings.MinBoxHeight, 96), Settings.MaxBoxHeight);
        var gap = Math.Clamp(Settings.BoxGap <= 0 ? 18 : Settings.BoxGap, 12, 28);
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
