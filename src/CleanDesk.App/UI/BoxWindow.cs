using CleanDesk.App.Models;
using CleanDesk.App.Services;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CleanDesk.App.UI;

public sealed class BoxWindow : Window
{
    private const int TitleHeight = 38;
    private const int CompactTitleHeight = 30;
    private readonly CleanDeskController _controller;
    private readonly BoxModel _box;
    private readonly Border _root;
    private readonly DockPanel _layout;
    private TextBlock _title = null!;
    private WpfButton _collapseButton = null!;
    private readonly ScrollViewer _scroll;
    private readonly WrapPanel _iconPanel;
    private readonly StackPanel _listPanel;
    private readonly Border _snapHint;
    private bool _dragging;
    private bool _closingForReal;
    private NativePoint _dragOrigin;
    private NativePoint _resizeOrigin;
    private double _originLeft;
    private double _originTop;
    private double _originWidth;
    private double _originHeight;

    public bool IsClosed { get; private set; }

    public BoxWindow(CleanDeskController controller, BoxModel box)
    {
        _controller = controller;
        _box = box;

        WindowStyle = WindowStyle.None;
        Icon = AppIconService.CreateWindowIcon();
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        Left = box.Bounds.X;
        Top = box.Bounds.Y;
        Width = box.Bounds.Width;
        Height = box.IsCollapsed ? CurrentTitleHeight() : box.Bounds.Height;
        MinWidth = 180;
        MinHeight = 38;

        _root = new Border
        {
            CornerRadius = new CornerRadius(_controller.Settings.EnableBoxCornerRadius ? Math.Clamp(_controller.Settings.BoxCornerRadius, 0, 24) : 0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(105, 255, 255, 255)),
            Background = CreateGlassBrush(),
            SnapsToDevicePixels = true
        };

        _layout = new DockPanel { LastChildFill = true };
        _root.Child = _layout;

        var titleBar = BuildTitleBar();
        DockPanel.SetDock(titleBar, Dock.Top);
        _layout.Children.Add(titleBar);

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 8, 12, 12),
            Background = Brushes.Transparent
        };

        _iconPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 4) };
        _listPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(4) };
        _scroll.Content = _iconPanel;
        _layout.Children.Add(_scroll);

        var resizeGrip = new Thumb
        {
            Width = 14,
            Height = 14,
            Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = 0.38,
            Background = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255))
        };
        _snapHint = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Child = new Grid
            {
                Children = { _root, resizeGrip }
            }
        };
        resizeGrip.DragStarted += OnResizeStarted;
        resizeGrip.DragDelta += OnResizeDelta;
        resizeGrip.DragCompleted += (_, _) =>
        {
            _snapHint.BorderBrush = Brushes.Transparent;
            _box.HasUserLayout = true;
            PersistBounds();
        };

        Content = _snapHint;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += (_, _) => IsClosed = true;
        Loaded += (_, _) => ApplyCollapsedState();
    }

    public void RefreshItems()
    {
        _title.Text = BuildTitle();
        _collapseButton.Content = _box.IsCollapsed ? ">" : "v";
        ApplyVisualStyle();

        _iconPanel.Children.Clear();
        _listPanel.Children.Clear();
        _scroll.Content = UsesListMode() ? _listPanel : _iconPanel;

        if (_box.Kind == BoxKind.Mapped)
        {
            AddMappedNavigation();
        }

        foreach (var item in _controller.GetItemsForBox(_box))
        {
            if (UsesListMode())
            {
                _listPanel.Children.Add(BuildListItem(item));
            }
            else
            {
                _iconPanel.Children.Add(BuildIconItem(item));
            }
        }

        ApplyCollapsedState();
    }

    public void CloseForReal()
    {
        _closingForReal = true;
        Close();
    }

    public void ApplyModelBounds()
    {
        Left = _box.Bounds.X;
        Top = _box.Bounds.Y;
        Width = Math.Max(MinWidth, _box.Bounds.Width);
        Height = _box.IsCollapsed ? CurrentTitleHeight() : Math.Max(96, _box.Bounds.Height);
        ApplyCollapsedState();
    }

    private DockPanel BuildTitleBar()
    {
        var titleBar = new DockPanel
        {
            Height = CurrentTitleHeight(),
            LastChildFill = true,
            Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(_controller.Settings.TitleBarOpacity, 0.05, 0.35) * 255), 255, 255, 255)),
            Cursor = Cursors.SizeAll
        };
        titleBar.MouseLeftButtonDown += OnTitleMouseDown;
        titleBar.MouseMove += OnTitleMouseMove;
        titleBar.MouseLeftButtonUp += OnTitleMouseUp;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(buttons, Dock.Right);
        titleBar.Children.Add(buttons);

        _collapseButton = BuildTitleButton("v");
        _collapseButton.Click += (_, _) =>
        {
            _box.IsCollapsed = !_box.IsCollapsed;
            ApplyCollapsedState();
            _controller.SaveSettings();
        };
        buttons.Children.Add(_collapseButton);

        var settingsButton = BuildTitleButton("•");
        settingsButton.ToolTip = "盒子设置";
        settingsButton.Click += (_, _) => OpenBoxSettingsMenu(settingsButton);
        buttons.Children.Add(settingsButton);

        var menuButton = BuildTitleButton("⋯");
        menuButton.ToolTip = "菜单";
        menuButton.Click += (_, _) => OpenBoxMenu(menuButton);
        buttons.Children.Add(menuButton);

        _title = new TextBlock
        {
            Text = BuildTitle(),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(TryParseColor(_controller.Settings.BoxTitleColor, Color.FromRgb(248, 250, 252))),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleBar.Children.Add(_title);

        return titleBar;
    }

    private WpfButton BuildTitleButton(string text)
    {
        return new WpfButton
        {
            Content = text,
            Width = 28,
            Height = _controller.Settings.CompactTitleBar ? 20 : 24,
            Margin = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(0),
            FontSize = 12,
            Foreground = new SolidColorBrush(TryParseColor(_controller.Settings.BoxTitleColor, Colors.White)),
            Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(58, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
    }

    private UIElement BuildIconItem(DeskItem item)
    {
        var iconSize = GetEffectiveIconSize();
        var width = _controller.Settings.ShowFileNames && _box.DisplayMode != BoxDisplayMode.IconOnly
            ? Math.Max(78, iconSize + 48)
            : Math.Max(48, iconSize + 22);
        var panel = new StackPanel
        {
            Width = width,
            MinHeight = _controller.Settings.ShowFileNames && _box.DisplayMode != BoxDisplayMode.IconOnly ? iconSize + 50 : iconSize + 18,
            Margin = new Thickness(5, 4, 5, 6),
            Tag = item,
            Cursor = Cursors.Hand
        };

        var image = new Image
        {
            Source = _controller.Icons.GetIcon(item.Path, iconSize),
            Width = iconSize,
            Height = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 4),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        panel.Children.Add(image);

        if (_controller.Settings.ShowFileNames && _box.DisplayMode != BoxDisplayMode.IconOnly)
        {
            panel.Children.Add(new TextBlock
            {
                Text = item.DisplayName,
                FontSize = 12,
                Foreground = new SolidColorBrush(TryParseColor(_controller.Settings.BoxTextColor, Colors.White)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        AttachItemEvents(panel, item);
        return panel;
    }

    private UIElement BuildListItem(DeskItem item)
    {
        var panel = new DockPanel
        {
            Height = 36,
            Margin = new Thickness(2, 1, 2, 1),
            LastChildFill = true,
            Tag = item,
            Cursor = Cursors.Hand
        };

        var image = new Image
        {
            Source = _controller.Icons.GetIcon(item.Path, 24),
            Width = 24,
            Height = 24,
            Margin = new Thickness(4, 4, 8, 4),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        DockPanel.SetDock(image, Dock.Left);
        panel.Children.Add(image);

        panel.Children.Add(new TextBlock
        {
            Text = item.DisplayName,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(TryParseColor(_controller.Settings.BoxTextColor, Colors.White)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        AttachItemEvents(panel, item);
        return panel;
    }

    private void AttachItemEvents(FrameworkElement element, DeskItem item)
    {
        element.ToolTip = item.Path;
        element.MouseEnter += (_, _) => element.Opacity = 0.78;
        element.MouseLeave += (_, _) => element.Opacity = 1;
        element.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                if (_box.Kind == BoxKind.Mapped && item.IsDirectory)
                {
                    _box.CurrentPath = item.Path;
                    _controller.SaveSettings();
                    RefreshItems();
                }
                else
                {
                    _controller.OpenItem(item);
                }
            }
        };
        element.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowShellContextMenu(element, item, e);
        };
    }

    private void ShowShellContextMenu(FrameworkElement element, DeskItem item, MouseButtonEventArgs e)
    {
        var screenPoint = element.PointToScreen(e.GetPosition(element));
        var shown = ShellContextMenuHost.ShowOutOfProcess(item.Path, (int)screenPoint.X, (int)screenPoint.Y);

        if (!shown)
        {
            var fallback = BuildItemMenu(item);
            fallback.Placement = PlacementMode.AbsolutePoint;
            fallback.HorizontalOffset = screenPoint.X;
            fallback.VerticalOffset = screenPoint.Y;
            fallback.IsOpen = true;
        }

        _ = Task.Delay(800).ContinueWith(_ =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (!IsClosed)
                {
                    _controller.RefreshAfterShellChange();
                    RefreshItems();
                }
            });
        });
    }

    private ContextMenu BuildItemMenu(DeskItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("打开", () =>
        {
            if (_box.Kind == BoxKind.Mapped && item.IsDirectory)
            {
                _box.CurrentPath = item.Path;
                _controller.SaveSettings();
                RefreshItems();
            }
            else
            {
                _controller.OpenItem(item);
            }
        }));
        menu.Items.Add(MenuItem("打开文件所在位置", () => ShellOperations.OpenContainingFolder(item.Path)));
        menu.Items.Add(MenuItem("复制路径", () => ShellOperations.CopyPath(item.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("复制", () => ShellOperations.CopyFileDrop(item.Path, false)));
        menu.Items.Add(MenuItem("剪切", () => ShellOperations.CopyFileDrop(item.Path, true)));
        menu.Items.Add(MenuItem("重命名", () =>
        {
            var newName = Microsoft.VisualBasic.Interaction.InputBox("输入新的文件名：", "重命名", item.Name);
            if (ShellOperations.Rename(item.Path, newName))
            {
                _controller.RefreshAfterShellChange();
                RefreshItems();
            }
        }));
        menu.Items.Add(MenuItem("删除到回收站", () =>
        {
            var result = Forms.MessageBox.Show("删除会调用 Windows 回收站，不会直接永久删除。是否继续？", "CleanDesk", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question);
            if (result == Forms.DialogResult.Yes)
            {
                ShellOperations.DeleteToRecycleBin(item.Path);
                _controller.RefreshAfterShellChange();
                RefreshItems();
            }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("属性", () => ShellOperations.ShowProperties(item.Path)));
        return menu;
    }

    private void AddMappedNavigation()
    {
        var current = string.IsNullOrWhiteSpace(_box.CurrentPath) ? _box.MappedPath : _box.CurrentPath;
        if (string.IsNullOrWhiteSpace(current) || current.Equals(_box.MappedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var parent = Directory.GetParent(current);
        if (parent is null || !parent.FullName.StartsWith(_box.MappedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var back = new DockPanel
        {
            Height = 34,
            Margin = new Thickness(2),
            Cursor = Cursors.Hand
        };
        back.Children.Add(new TextBlock
        {
            Text = "< 返回上一级",
            Foreground = new SolidColorBrush(TryParseColor(_controller.Settings.BoxTextColor, Colors.White)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        });
        back.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 1)
            {
                _box.CurrentPath = parent.FullName;
                _controller.SaveSettings();
                RefreshItems();
            }
        };
        _listPanel.Children.Add(back);
    }

    private static MenuItem MenuItem(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenBoxSettingsMenu(WpfButton anchor)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(_box.IsLocked ? "解除锁定" : "锁定盒子", () =>
        {
            _box.IsLocked = !_box.IsLocked;
            _controller.SaveSettings();
        }));
        menu.Items.Add(new Separator());
        foreach (var mode in Enum.GetValues<BoxDisplayMode>())
        {
            menu.Items.Add(MenuItem("显示模式：" + DisplayModeName(mode), () =>
            {
                _box.DisplayMode = mode;
                _controller.SaveSettings();
                RefreshItems();
            }));
        }
        menu.Items.Add(new Separator());
        foreach (var opacity in new[] { 0.45, 0.6, 0.72, 0.85 })
        {
            menu.Items.Add(MenuItem($"透明度 {opacity:P0}", () =>
            {
                _box.Opacity = opacity;
                _controller.SaveSettings();
                RefreshItems();
            }));
        }
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private void OpenBoxMenu(WpfButton anchor)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("重命名盒子", () =>
        {
            var name = Microsoft.VisualBasic.Interaction.InputBox("输入盒子名称：", "重命名盒子", _box.Name);
            if (!string.IsNullOrWhiteSpace(name))
            {
                _box.Name = name.Trim();
                _controller.SaveSettings();
                RefreshItems();
            }
        }));
        if (_box.Kind == BoxKind.Mapped && Directory.Exists(_box.MappedPath))
        {
            menu.Items.Add(MenuItem("打开真实文件夹", () => ShellOperations.Open(_box.MappedPath)));
        }
        menu.Items.Add(MenuItem("解散盒子", () => _controller.RemoveBox(_box)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("创建盒子", _controller.CreateBox));
        menu.Items.Add(MenuItem("创建映射盒子", _controller.CreateMappedBox));
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_box.IsLocked || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dragging = true;
        GetCursorPos(out _dragOrigin);
        _originLeft = Left;
        _originTop = Top;
        Mouse.Capture((IInputElement)sender);
    }

    private void OnTitleMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_dragging || _box.IsLocked)
        {
            return;
        }

        GetCursorPos(out var current);
        var candidate = _box.Bounds.Clone();
        candidate.X = _originLeft + current.X - _dragOrigin.X;
        candidate.Y = _originTop + current.Y - _dragOrigin.Y;
        ApplyBounds(_controller.Snap(_box, candidate, false), true);
    }

    private void OnTitleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Mouse.Capture(null);
        _snapHint.BorderBrush = Brushes.Transparent;
        _box.HasUserLayout = true;
        PersistBounds();
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (_box.IsLocked || _box.IsCollapsed)
        {
            return;
        }

        GetCursorPos(out var current);
        var candidate = _box.Bounds.Clone();
        candidate.Width = Math.Max(MinWidth, _originWidth + current.X - _resizeOrigin.X);
        candidate.Height = Math.Max(96, _originHeight + current.Y - _resizeOrigin.Y);
        ApplyBounds(_controller.Snap(_box, candidate, true), true);
    }

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        GetCursorPos(out _resizeOrigin);
        _originWidth = ActualWidth;
        _originHeight = ActualHeight;
    }

    private void ApplyBounds(DesktopRect bounds, bool snapFeedback)
    {
        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = _box.IsCollapsed ? CurrentTitleHeight() : bounds.Height;
        _box.Bounds = bounds;
        if (!_box.IsCollapsed)
        {
            _box.LastExpandedWidth = bounds.Width;
            _box.LastExpandedHeight = bounds.Height;
        }

        _snapHint.BorderBrush = snapFeedback ? new SolidColorBrush(Color.FromArgb(145, 125, 211, 252)) : Brushes.Transparent;
    }

    private void PersistBounds()
    {
        _box.HasUserLayout = true;
        _box.Bounds.X = Left;
        _box.Bounds.Y = Top;
        _box.Bounds.Width = Width;
        if (!_box.IsCollapsed)
        {
            _box.Bounds.Height = Height;
            _box.LastExpandedHeight = Height;
            _box.LastExpandedWidth = Width;
        }

        _controller.SaveSettings();
    }

    private void ApplyCollapsedState()
    {
        _scroll.Visibility = _box.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _collapseButton.Content = _box.IsCollapsed ? ">" : "v";
        if (_box.IsCollapsed)
        {
            Height = CurrentTitleHeight();
            Width = Math.Max(MinWidth, _box.Bounds.Width);
        }
        else
        {
            Height = Math.Max(96, _box.Bounds.Height);
            Width = Math.Max(MinWidth, _box.Bounds.Width);
            _box.Bounds.Height = Height;
            _box.Bounds.Width = Width;
            _box.LastExpandedWidth = Width;
            _box.LastExpandedHeight = Height;
        }
    }

    private bool UsesListMode()
    {
        return _box.DisplayMode is BoxDisplayMode.List or BoxDisplayMode.MultiColumn or BoxDisplayMode.Recent || _box.Kind == BoxKind.Mapped;
    }

    private string BuildTitle()
    {
        if (_box.Kind != BoxKind.Mapped)
        {
            return _box.Name;
        }

        var current = string.IsNullOrWhiteSpace(_box.CurrentPath) ? _box.MappedPath : _box.CurrentPath;
        return string.IsNullOrWhiteSpace(current) ? _box.Name : $"{_box.Name}  {current}";
    }

    private int CurrentTitleHeight()
    {
        return _controller.Settings.CompactTitleBar ? CompactTitleHeight : TitleHeight;
    }

    private WpfBrush CreateGlassBrush()
    {
        var opacity = Math.Clamp(_box.Opacity <= 0 ? _controller.Settings.GlobalOpacity : _box.Opacity, 0.25, 0.95);
        var theme = _controller.Settings.ThemeMode?.ToLowerInvariant() ?? "glass";
        var accent = TryParseColor(_controller.Settings.BoxAccentColor, Color.FromRgb(125, 211, 252));
        var background = TryParseColor(_controller.Settings.BoxBackgroundColor, Color.FromRgb(34, 48, 58));

        if (theme == "transparent")
        {
            return new SolidColorBrush(Color.FromArgb((byte)(opacity * 90), background.R, background.G, background.B));
        }

        if (theme == "solid")
        {
            return new SolidColorBrush(Color.FromArgb((byte)(opacity * 195), background.R, background.G, background.B));
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(opacity * 138), background.R, background.G, background.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(opacity * 92), accent.R, accent.G, accent.B), 0.58));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(opacity * 126), 15, 23, 32), 1));
        return brush;
    }

    private int GetEffectiveIconSize()
    {
        var size = _controller.Settings.IconSize;
        if (_controller.Settings.MatchDesktopIconSize)
        {
            size = Environment.OSVersion.Version.Major >= 10 ? 32 : 24;
        }

        return Math.Clamp(size, 16, 64);
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
            // ignore invalid custom colors
        }

        return fallback;
    }

    private void ApplyVisualStyle()
    {
        var accentColor = TryParseColor(_controller.Settings.BoxAccentColor, Color.FromRgb(125, 211, 252));
        var titleColor = TryParseColor(_controller.Settings.BoxTitleColor, Color.FromRgb(248, 250, 252));

        _root.CornerRadius = new CornerRadius(_controller.Settings.EnableBoxCornerRadius ? Math.Clamp(_controller.Settings.BoxCornerRadius, 0, 24) : 0);
        _root.BorderThickness = _controller.Settings.ShowBoxBorder ? new Thickness(1) : new Thickness(0);
        _root.BorderBrush = new SolidColorBrush(Color.FromArgb(105, accentColor.R, accentColor.G, accentColor.B));
        _root.Background = CreateGlassBrush();

        if (_layout.Children.OfType<DockPanel>().FirstOrDefault() is { } titleBar)
        {
            titleBar.Height = CurrentTitleHeight();
            titleBar.Background = new LinearGradientBrush(
                Color.FromArgb((byte)(Math.Clamp(_controller.Settings.TitleBarOpacity, 0.05, 0.35) * 255), 255, 255, 255),
                Color.FromArgb((byte)(Math.Clamp(_controller.Settings.TitleBarOpacity * 0.65, 0.03, 0.24) * 255), accentColor.R, accentColor.G, accentColor.B),
                new Point(0, 0),
                new Point(1, 0));
        }

        _title.Foreground = new SolidColorBrush(titleColor);
        _title.FontSize = _controller.Settings.CompactTitleBar ? 12 : 13;
        _collapseButton.Height = _controller.Settings.CompactTitleBar ? 20 : 24;
    }

    private static string DisplayModeName(BoxDisplayMode mode)
    {
        return mode switch
        {
            BoxDisplayMode.Icon => "图标",
            BoxDisplayMode.IconOnly => "仅图标",
            BoxDisplayMode.List => "列表",
            BoxDisplayMode.MultiColumn => "多列列表",
            BoxDisplayMode.Auto => "自动排列",
            BoxDisplayMode.Manual => "手动排列",
            BoxDisplayMode.Recent => "最近常用",
            _ => mode.ToString()
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        DesktopInterop.MakeToolWindow(handle);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingForReal)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
}
