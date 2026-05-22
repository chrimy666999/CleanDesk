using CleanDesk.App.Models;
using CleanDesk.App.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;

namespace CleanDesk.App.UI;

public sealed class SettingsWindow : Window
{
    private readonly CleanDeskController _controller;
    private readonly ContentControl _content = new();
    private readonly string[] _tabs = ["常规", "分类", "主题", "盒子", "映射", "备份", "高级", "关于"];

    public SettingsWindow(CleanDeskController controller)
    {
        _controller = controller;
        Title = "CleanDesk 设置中心";
        Icon = AppIconService.CreateWindowIcon();
        Width = 920;
        Height = 640;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = root;

        var nav = new StackPanel
        {
            Background = new LinearGradientBrush(Color.FromRgb(25, 35, 47), Color.FromRgb(40, 58, 73), new Point(0, 0), new Point(0, 1)),
            Margin = new Thickness(0)
        };
        Grid.SetColumn(nav, 0);
        root.Children.Add(nav);

        var title = new TextBlock
        {
            Text = "CleanDesk",
            Foreground = Brushes.White,
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(18, 22, 18, 18)
        };
        nav.Children.Add(title);

        foreach (var tab in _tabs)
        {
            var button = new WpfButton
            {
                Content = tab,
                Height = 42,
                Margin = new Thickness(12, 3, 12, 3),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 0, 0, 0),
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255))
            };
            button.Click += (_, _) => Select(tab);
            nav.Children.Add(button);
        }

        Grid.SetColumn(_content, 1);
        root.Children.Add(_content);
        Select("常规");
    }

    private void Select(string tab)
    {
        _content.Content = tab switch
        {
            "常规" => PageGeneral(),
            "分类" => PageRules(),
            "主题" => PageTheme(),
            "盒子" => PageBoxes(),
            "映射" => PageMappings(),
            "备份" => PageBackups(),
            "高级" => PageAdvanced(),
            "关于" => PageAbout(),
            _ => PageGeneral()
        };
    }

    private ScrollViewer PageGeneral()
    {
        var panel = Page("常规");
        panel.Children.Add(Check("开机自启动", _controller.Settings.AutoStart, value =>
        {
            _controller.Settings.AutoStart = value;
            AutoStartService.SetEnabled(value);
        }));
        panel.Children.Add(Check("启动后自动整理桌面", _controller.Settings.AutoOrganizeOnStartup, value => _controller.Settings.AutoOrganizeOnStartup = value));
        panel.Children.Add(Check("新文件自动整理", _controller.Settings.AutoOrganizeNewFiles, value => _controller.Settings.AutoOrganizeNewFiles = value));
        panel.Children.Add(Check("实时整理", _controller.Settings.RealtimeOrganize, value => _controller.Settings.RealtimeOrganize = value));
        panel.Children.Add(Check("整理时隐藏散落桌面图标", _controller.Settings.HideScatteredDesktopIcons, value => _controller.Settings.HideScatteredDesktopIcons = value));
        panel.Children.Add(Check("整理完成后自动重排图标", _controller.Settings.AutoReflowAfterOrganize, value => _controller.Settings.AutoReflowAfterOrganize = value));
        panel.Children.Add(ActionRow(("显示 / 隐藏所有盒子", _controller.ToggleAllBoxes), ("自动整理桌面", () => _controller.OrganizeDesktop("Settings")), ("一键恢复桌面", _controller.RestoreDesktop)));
        panel.Children.Add(ActionRow((_controller.Settings.PauseTakeover ? "继续接管桌面" : "暂停 CleanDesk 接管桌面", () =>
        {
            _controller.HandleCommand(new StartupCommand { Kind = StartupCommandKind.Pause });
            Select("常规");
        })));
        return Wrap(panel);
    }

    private ScrollViewer PageRules()
    {
        var panel = Page("分类");
        panel.Children.Add(Text("默认分类规则和自定义规则按顺序匹配，命中后整理到指定盒子。"));
        var list = new ListBox { Height = 260, Margin = new Thickness(0, 12, 0, 12) };
        foreach (var rule in _controller.Settings.Rules)
        {
            list.Items.Add($"{(rule.Enabled ? "[启用]" : "[禁用]")} {rule.Name}  {rule.Type}  {rule.Pattern} -> {rule.TargetBoxName}");
        }
        panel.Children.Add(list);
        panel.Children.Add(ActionRow(("添加通配符规则", () =>
        {
            var pattern = Microsoft.VisualBasic.Interaction.InputBox("例如 *.pdf 或 project_*：", "添加通配符规则", "*.pdf");
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return;
            }

            var target = Microsoft.VisualBasic.Interaction.InputBox("整理到哪个盒子：", "目标盒子", "文档");
            _controller.Settings.Rules.Insert(0, new CategoryRule
            {
                Name = "自定义通配符",
                Type = CategoryRuleType.Wildcard,
                Pattern = pattern,
                TargetBoxName = string.IsNullOrWhiteSpace(target) ? "其他" : target.Trim()
            });
            _controller.RefreshAfterShellChange();
            Select("分类");
        }), ("重置默认规则", () =>
        {
            _controller.Settings.Rules = AppSettings.CreateDefaultRules();
            _controller.RefreshAfterShellChange();
            Select("分类");
        })));
        panel.Children.Add(Text("最近常用盒子根据打开次数、最近打开时间和最近访问时间排序。"));
        return Wrap(panel);
    }

    private ScrollViewer PageTheme()
    {
        var panel = Page("主题");
        panel.Children.Add(ActionRow(("玻璃", () => ApplyThemePreset("glass")), ("纯透明", () => ApplyThemePreset("transparent")), ("纯色", () => ApplyThemePreset("solid"))));
        panel.Children.Add(Slider("全局透明度", _controller.Settings.GlobalOpacity, 0.05, 1.0, value =>
        {
            _controller.Settings.GlobalOpacity = value;
            foreach (var box in _controller.Settings.Boxes)
            {
                box.Opacity = value;
            }
            _controller.RefreshBoxVisuals();
        }));
        panel.Children.Add(Slider("图标大小", _controller.Settings.IconSize, 24, 72, value =>
        {
            _controller.Settings.IconSize = (int)value;
            _controller.ShowBoxes();
        }));
        panel.Children.Add(Check("跟随原始桌面图标尺寸", _controller.Settings.MatchDesktopIconSize, value =>
        {
            _controller.Settings.MatchDesktopIconSize = value;
            _controller.ShowBoxes();
        }));
        panel.Children.Add(Check("显示文件名", _controller.Settings.ShowFileNames, value =>
        {
            _controller.Settings.ShowFileNames = value;
            _controller.ShowBoxes();
        }));
        panel.Children.Add(ColorRow("盒子背景色", _controller.Settings.BoxBackgroundColor, value => { _controller.Settings.BoxBackgroundColor = value; _controller.ShowBoxes(); }));
        panel.Children.Add(ColorRow("盒子强调色", _controller.Settings.BoxAccentColor, value => { _controller.Settings.BoxAccentColor = value; _controller.ShowBoxes(); }));
        panel.Children.Add(ColorRow("标题文字色", _controller.Settings.BoxTitleColor, value => { _controller.Settings.BoxTitleColor = value; _controller.ShowBoxes(); }));
        panel.Children.Add(ColorRow("文件文字色", _controller.Settings.BoxTextColor, value => { _controller.Settings.BoxTextColor = value; _controller.ShowBoxes(); }));
        panel.Children.Add(Check("显示盒子边框", _controller.Settings.ShowBoxBorder, value => { _controller.Settings.ShowBoxBorder = value; _controller.ShowBoxes(); }));
        panel.Children.Add(Check("启用圆角", _controller.Settings.EnableBoxCornerRadius, value => { _controller.Settings.EnableBoxCornerRadius = value; _controller.ShowBoxes(); }));
        panel.Children.Add(Slider("圆角半径", _controller.Settings.BoxCornerRadius, 0, 28, value =>
        {
            _controller.Settings.BoxCornerRadius = (int)value;
            _controller.ShowBoxes();
        }));
        panel.Children.Add(Slider("标题栏透明度", _controller.Settings.TitleBarOpacity, 0.02, 0.65, value =>
        {
            _controller.Settings.TitleBarOpacity = value;
            _controller.RefreshBoxVisuals();
        }));
        panel.Children.Add(Slider("磁力访问窗背景透明度", _controller.Settings.MagneticAccessOpacity, 0.05, 0.95, value =>
        {
            _controller.Settings.MagneticAccessOpacity = value;
        }));
        panel.Children.Add(ActionRow(("访问窗列表模式", () => SetMagneticAccessDisplayMode("List")), ("访问窗紧凑模式", () => SetMagneticAccessDisplayMode("Compact")), ("访问窗图标模式", () => SetMagneticAccessDisplayMode("Icon"))));
        panel.Children.Add(Check("紧凑标题栏", _controller.Settings.CompactTitleBar, value =>
        {
            _controller.Settings.CompactTitleBar = value;
            _controller.ShowBoxes();
        }));
        panel.Children.Add(Check("盒子默认隐藏，鼠标悬停时展开", _controller.Settings.AutoHideBoxes, value =>
        {
            _controller.Settings.AutoHideBoxes = value;
            _controller.ApplyLayoutPreset(_controller.Settings.ActiveLayoutPresetId, true);
        }));
        panel.Children.Add(Text("主题模式已扩展为可调玻璃、纯透明和纯色三种，颜色和圆角会直接作用于盒子视觉层。"));
        return Wrap(panel);
    }

    private ScrollViewer PageBoxes()
    {
        var panel = Page("盒子");
        var list = new ListBox { Height = 300, Margin = new Thickness(0, 12, 0, 12) };
        foreach (var box in _controller.Settings.Boxes)
        {
            list.Items.Add($"{box.Name}  {box.Kind}  {box.DisplayMode}  {box.Bounds.Width:0}x{box.Bounds.Height:0}");
        }
        panel.Children.Add(list);
        panel.Children.Add(ActionRow(("创建盒子", _controller.CreateBox), ("创建映射盒子", _controller.CreateMappedBox), ("恢复默认三边界布局", _controller.ResetDefaultBoundaryLayout)));
        panel.Children.Add(Text("默认布局已改为上、左、右三边界隐藏标题栏；旧版排列预设不再作为主要入口。"));
        panel.Children.Add(Slider("磁吸距离", _controller.Settings.SnapDistance, 4, 32, value => _controller.Settings.SnapDistance = (int)value));
        panel.Children.Add(Slider("盒子间距", _controller.Settings.BoxGap, 12, 28, value => _controller.Settings.BoxGap = (int)value));
        panel.Children.Add(Slider("默认盒子宽度", _controller.Settings.DefaultBoxWidth, 180, 520, value => _controller.Settings.DefaultBoxWidth = (int)value));
        panel.Children.Add(Slider("默认盒子高度", _controller.Settings.DefaultBoxHeight, 96, 420, value => _controller.Settings.DefaultBoxHeight = (int)value));
        panel.Children.Add(Slider("自动排列网格", _controller.Settings.GridSize, 4, 64, value => _controller.Settings.GridSize = (int)value));
        panel.Children.Add(Text("盒子标题栏支持搜索、锁定、终端入口和悬停展开；边界盒子会按当前工作区重新约束在屏幕内。"));
        return Wrap(panel);
    }

    private ScrollViewer PageMappings()
    {
        var panel = Page("映射");
        var mappings = _controller.Settings.Boxes.Where(box => box.Kind == BoxKind.Mapped).ToList();
        panel.Children.Add(Text(mappings.Count == 0 ? "当前没有映射盒子。" : "当前映射路径："));
        foreach (var box in mappings)
        {
            panel.Children.Add(Text($"{box.Name}: {box.MappedPath}"));
        }
        panel.Children.Add(ActionRow(("创建映射盒子", _controller.CreateMappedBox), ("打开第一个真实文件夹", () =>
        {
            var first = mappings.FirstOrDefault();
            if (first is not null && Directory.Exists(first.MappedPath))
            {
                ShellOperations.Open(first.MappedPath);
            }
        })));
        return Wrap(panel);
    }

    private ScrollViewer PageBackups()
    {
        var service = new BackupService();
        var panel = Page("备份");
        var backups = service.List();
        var list = new ListBox { Height = 280, Margin = new Thickness(0, 12, 0, 12) };
        foreach (var backup in backups)
        {
            list.Items.Add($"{backup.Id}  {backup.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {backup.Reason}  {backup.Items.Count} 项");
        }
        panel.Children.Add(list);
        panel.Children.Add(ActionRow(("手动备份布局", () =>
        {
            _controller.RefreshAfterShellChange();
            service.Create(_controller.Settings, "Manual");
            Select("备份");
        }), ("从原始布局恢复", _controller.RestoreDesktop), ("导出选中备份", () =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= backups.Count)
            {
                return;
            }

            using var dialog = new Forms.SaveFileDialog { Filter = "CleanDesk Backup (*.json)|*.json", FileName = backups[list.SelectedIndex].Id + ".json" };
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                File.Copy(service.GetPath(backups[list.SelectedIndex].Id), dialog.FileName, true);
            }
        })));
        return Wrap(panel);
    }

    private ScrollViewer PageAdvanced()
    {
        var panel = Page("高级");
        panel.Children.Add(Text("性能模式：文件变化使用 FileSystemWatcher 事件监听，图标使用内存缓存，拖拽和调整大小只更新当前盒子。"));
        panel.Children.Add(Text("日志目录：" + PortablePaths.LogsRoot));
        panel.Children.Add(ActionRow(("重建图标缓存", () =>
        {
            _controller.Icons.ClearMissing([]);
            _controller.ShowBoxes();
        }), ("安全模式启动", () =>
        {
            _controller.Settings.PauseTakeover = true;
            DesktopInterop.SetDesktopIconsVisible(true);
            _controller.SaveSettings();
        }), ("重置所有设置", () =>
        {
            var result = Forms.MessageBox.Show("重置设置不会删除桌面文件。是否继续？", "CleanDesk", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Warning);
            if (result == Forms.DialogResult.Yes)
            {
                _controller.RestoreDesktop();
                _controller.Settings.Boxes.Clear();
                _controller.Settings.Rules = AppSettings.CreateDefaultRules();
                _controller.RefreshAfterShellChange();
                Select("高级");
            }
        })));
        panel.Children.Add(Text("桌面接管方式：CleanDesk 记录 Explorer 桌面图标位置，然后隐藏桌面 ListView，用透明盒子代理真实桌面文件和快捷方式。恢复时重新显示 ListView 并按备份位置回放。"));
        return Wrap(panel);
    }

    private ScrollViewer PageAbout()
    {
        var panel = Page("关于");
        panel.Children.Add(Text("软件名称：CleanDesk"));
        panel.Children.Add(Text("当前版本：" + _controller.Settings.Version));
        panel.Children.Add(Text("便携版说明：配置、日志、备份和缓存都保存在 exe 同级 portable-data 目录。"));
        panel.Children.Add(Text("项目路径：" + Directory.GetCurrentDirectory()));
        panel.Children.Add(Text("版权信息：为客户定制的桌面整理工具。"));
        return Wrap(panel);
    }

    private static StackPanel Page(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(30, 26, 30, 30) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(22, 30, 42)),
            Margin = new Thickness(0, 0, 0, 14)
        });
        return panel;
    }

    private static ScrollViewer Wrap(StackPanel panel)
    {
        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 251))
        };
    }

    private static TextBlock Text(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 5)
        };
    }

    private void ApplyThemePreset(string mode)
    {
        _controller.Settings.ThemeMode = mode;
        _controller.ShowBoxes();
    }

    private void SetMagneticAccessDisplayMode(string mode)
    {
        _controller.Settings.MagneticAccessDisplayMode = mode;
        _controller.SaveSettings();
    }

    private FrameworkElement ColorRow(string label, string value, Action<string> changed)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 8, 0, 8), LastChildFill = true };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Width = 140,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });

        var sample = new Border
        {
            Width = 34,
            Height = 20,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(210, 215, 223)),
            Background = TryParseColor(value, Color.FromRgb(34, 48, 58)) is var color
                ? new SolidColorBrush(color)
                : Brushes.Transparent,
            Margin = new Thickness(0, 0, 8, 0)
        };
        DockPanel.SetDock(sample, Dock.Right);
        panel.Children.Add(sample);

        var button = new WpfButton
        {
            Content = value,
            Height = 32,
            MinWidth = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 0, 12, 0),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(208, 216, 226))
        };
        button.Click += (_, _) =>
        {
            var input = Microsoft.VisualBasic.Interaction.InputBox("输入颜色值（例如 #22303A）：", label, value);
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            changed(input.Trim());
            button.Content = input.Trim();
            sample.Background = new SolidColorBrush(TryParseColor(input.Trim(), Color.FromRgb(34, 48, 58)));
        };
        panel.Children.Add(button);
        return panel;
    }

    private static Color TryParseColor(string value, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return (Color)ColorConverter.ConvertFromString(value)!;
            }
        }
        catch
        {
            // Ignore invalid custom colors and keep the current UI usable.
        }

        return fallback;
    }

    private System.Windows.Controls.CheckBox Check(string label, bool value, Action<bool> changed)
    {
        var check = new System.Windows.Controls.CheckBox
        {
            Content = label,
            IsChecked = value,
            FontSize = 14,
            Margin = new Thickness(0, 7, 0, 7),
            Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
        };
        check.Checked += (_, _) => { changed(true); _controller.SaveSettings(); };
        check.Unchecked += (_, _) => { changed(false); _controller.SaveSettings(); };
        return check;
    }

    private FrameworkElement Slider(string label, double value, double min, double max, Action<double> changed)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 10, 0, 12) };
        var text = Text($"{label}: {FormatSliderValue(label, value)}");
        DockPanel.SetDock(text, Dock.Top);
        panel.Children.Add(text);
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            TickFrequency = (max - min) / 8,
            IsSnapToTickEnabled = false,
            Height = 26
        };
        slider.ValueChanged += (_, _) =>
        {
            changed(slider.Value);
            text.Text = $"{label}: {FormatSliderValue(label, slider.Value)}";
            _controller.SaveSettings();
        };
        panel.Children.Add(slider);
        return panel;
    }

    private static string FormatSliderValue(string label, double value)
    {
        return label.Contains("透明度", StringComparison.Ordinal) ? $"{value:P0}" : $"{value:0.##}";
    }

    private static FrameworkElement ActionRow(params (string Text, Action Action)[] actions)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        foreach (var action in actions)
        {
            var button = new WpfButton
            {
                Content = action.Text,
                Height = 34,
                MinWidth = 116,
                Margin = new Thickness(0, 0, 10, 8),
                Padding = new Thickness(14, 0, 14, 0),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(202, 211, 224)),
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            };
            button.Click += (_, _) => action.Action();
            row.Children.Add(button);
        }
        return row;
    }
}
