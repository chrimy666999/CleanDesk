using CleanDesk.App.Models;
using CleanDesk.App.Services;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CleanDesk.App.UI;

public sealed class BoxWindow : Window
{
    private const int TitleHeight = 38;
    private const int CompactTitleHeight = 30;
    private const int TitleButtonCount = 6;
    private const double HorizontalTitleButtonStride = 26;
    private const double HorizontalTitleChromePadding = 48;
    private const double VerticalTitleButtonStride = 26;
    private const double VerticalTitleChromePadding = 40;
    private const double VerticalTitleCharacterHeight = 16;
    private readonly CleanDeskController _controller;
    private readonly BoxModel _box;
    private readonly Border _root;
    private readonly DockPanel _layout;
    private Grid _titleBar = null!;
    private TextBlock _title = null!;
    private StackPanel _titleButtons = null!;
    private WpfButton _searchButton = null!;
    private TextBox _searchBox = null!;
    private Popup _searchPopup = null!;
    private WpfButton _terminalButton = null!;
    private WpfButton _lockButton = null!;
    private WpfButton _collapseButton = null!;
    private readonly ScrollViewer _scroll;
    private readonly WrapPanel _iconPanel;
    private readonly StackPanel _listPanel;
    private readonly Border _snapHint;
    private readonly Thumb _resizeGrip;
    private readonly DispatcherTimer _autoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly HashSet<string> _selectedItemPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _itemElements = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _desktopParent = IntPtr.Zero;
    private string _lastRenderSignature = "";
    private bool _hasRenderedItems;
    private string _searchQuery = "";
    private bool _dragging;
    private bool _closingForReal;
    private bool _hoverExpanded;
    private bool _itemDragActive;
    private Point _itemDragStart;
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
        Focusable = true;
        Left = box.Bounds.X;
        Top = box.Bounds.Y;
        Width = box.Bounds.Width;
        Height = box.IsCollapsed ? CurrentTitleHeight() : box.Bounds.Height;
        MinWidth = CurrentTitleHeight();
        MinHeight = CurrentTitleHeight();

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

        _iconPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 1, 2, 2), Background = Brushes.Transparent };
        _listPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(4), Background = Brushes.Transparent };
        _iconPanel.MouseLeftButtonDown += OnItemsBackgroundMouseDown;
        _listPanel.MouseLeftButtonDown += OnItemsBackgroundMouseDown;
        _scroll.PreviewMouseLeftButtonDown += OnItemsBackgroundMouseDown;
        _scroll.MouseLeftButtonDown += OnItemsBackgroundMouseDown;
        _scroll.Content = _iconPanel;
        _layout.Children.Add(_scroll);
        ConfigureFileDropTarget();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, OnPasteCommand, CanPasteCommand));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopyCommand, CanSelectedFileCommand));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, OnCutCommand, CanSelectedFileCommand));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, OnDeleteCommand, CanSelectedFileCommand));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, OnSelectAllCommand, CanSelectAllCommand));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Paste, Key.V, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Copy, Key.C, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Cut, Key.X, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Delete, Key.Delete, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(ApplicationCommands.SelectAll, Key.A, ModifierKeys.Control));

        _resizeGrip = new Thumb
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
                Children = { _root, _resizeGrip }
            }
        };
        _resizeGrip.DragStarted += OnResizeStarted;
        _resizeGrip.DragDelta += OnResizeDelta;
        _resizeGrip.DragCompleted += (_, _) =>
        {
            _snapHint.BorderBrush = Brushes.Transparent;
            _box.HasUserLayout = true;
            PersistBounds();
            _controller.ResolveBoxOverlaps(_box);
        };

        Content = _snapHint;
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            CollapseHoverExpansionIfReady();
        };
        MouseEnter += OnWindowMouseEnter;
        MouseLeave += OnWindowMouseLeave;
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += (_, _) => IsClosed = true;
        Loaded += (_, _) =>
        {
            ApplyLockState();
            ApplyCollapsedState();
        };
    }

    public void RefreshItems()
    {
        var title = BuildTitle();
        _searchQuery = _searchBox.Text.Trim();
        ApplyLockState();
        ApplyVisualStyle();
        var items = GetFilteredItems();
        var usesListMode = UsesListMode();
        var renderSignature = BuildRenderSignature(title, usesListMode, items);
        if (_hasRenderedItems && string.Equals(_lastRenderSignature, renderSignature, StringComparison.Ordinal))
        {
            PruneSelection(items);
            RefreshSelectionVisuals();
            return;
        }

        _iconPanel.Children.Clear();
        _listPanel.Children.Clear();
        _itemElements.Clear();
        _scroll.Content = usesListMode ? _listPanel : _iconPanel;

        if (_box.Kind == BoxKind.Mapped)
        {
            AddMappedNavigation();
        }

        PruneSelection(items);
        foreach (var item in items)
        {
            if (usesListMode)
            {
                _listPanel.Children.Add(BuildListItem(item));
            }
            else
            {
                _iconPanel.Children.Add(BuildIconItem(item));
            }
        }

        if (items.Count == 0)
        {
            AddEmptyState();
        }

        _lastRenderSignature = renderSignature;
        _hasRenderedItems = true;
        ApplyCollapsedState();
    }

    public void CloseForReal()
    {
        _closingForReal = true;
        Close();
    }

    public void ApplyModelBounds()
    {
        ApplyTitleDock();
        ApplyCollapsedState();
    }

    public void RefreshVisualStyle()
    {
        ApplyVisualStyle();
        ApplyTitleDock();
    }

    public void ClearSelectionAndCollapseIfEmpty()
    {
        ClearSelection(collapseWhenEmpty: true);
    }

    public void RevealPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _autoHideTimer.Stop();
        _hoverExpanded = false;
        _box.IsCollapsed = false;
        _box.HasUserLayout = true;

        if (_box.Kind == BoxKind.Mapped)
        {
            var targetDirectory = Directory.Exists(path)
                ? Directory.GetParent(path)?.FullName
                : Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(targetDirectory) &&
                Directory.Exists(targetDirectory) &&
                !string.IsNullOrWhiteSpace(_box.MappedPath) &&
                targetDirectory.StartsWith(_box.MappedPath, StringComparison.OrdinalIgnoreCase))
            {
                _box.CurrentPath = targetDirectory;
            }
        }

        ApplyCollapsedState(animate: true);
        RefreshItems();
        _selectedItemPaths.Clear();
        if (_itemElements.ContainsKey(path))
        {
            _selectedItemPaths.Add(path);
        }

        RefreshSelectionVisuals();
        Focus();
        Keyboard.Focus(this);
        _controller.SaveSettings();
    }

    public void EnsureDesktopPlacement(bool allowNativeShow = true)
    {
        if (IsClosed)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        DesktopInterop.MakeToolWindow(handle);
        var currentParent = DesktopInterop.GetParentWindow(handle);
        var isAttached = _desktopParent != IntPtr.Zero && currentParent == _desktopParent;
        if (!isAttached)
        {
            var host = DesktopInterop.FindDesktopHostWindow();
            if (host != IntPtr.Zero && currentParent == host)
            {
                _desktopParent = host;
                isAttached = true;
            }
            else if (DesktopInterop.TryAttachToDesktop(handle, out var attachedHost))
            {
                _desktopParent = attachedHost;
                isAttached = true;
            }
        }

        if (isAttached && allowNativeShow)
        {
            DesktopInterop.ShowNoActivate(handle);
        }
    }

    private Grid BuildTitleBar()
    {
        _titleBar = new Grid
        {
            Height = CurrentTitleHeight(),
            Background = new SolidColorBrush(Color.FromArgb(AlphaFromOpacity(_controller.Settings.TitleBarOpacity, 8, 190), 255, 255, 255)),
            Cursor = _box.IsLocked ? Cursors.Arrow : Cursors.SizeAll
        };
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleBar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _titleBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _titleBar.MouseLeftButtonDown += OnTitleMouseDown;
        _titleBar.MouseMove += OnTitleMouseMove;
        _titleBar.MouseLeftButtonUp += OnTitleMouseUp;

        _title = new TextBlock
        {
            Text = BuildTitle(),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(TryParseColor(_controller.Settings.BoxTitleColor, Color.FromRgb(248, 250, 252))),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
            TextTrimming = TextTrimming.None,
            TextWrapping = TextWrapping.NoWrap
        };
        Grid.SetColumn(_title, 0);
        _titleBar.Children.Add(_title);

        _titleButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_titleButtons, 1);
        _titleBar.Children.Add(_titleButtons);

        _searchButton = BuildTitleButton("⌕");
        _searchButton.ToolTip = "搜索盒内文件";
        _searchButton.Click += (_, _) => ToggleSearchPopup();
        _titleButtons.Children.Add(_searchButton);

        _terminalButton = BuildTitleButton(">_");
        _terminalButton.ToolTip = "在此盒子目录打开 PowerShell";
        _terminalButton.Click += (_, _) => OpenTerminalForBox();
        _titleButtons.Children.Add(_terminalButton);

        _lockButton = BuildTitleButton("");
        _lockButton.Click += (_, _) => ToggleLock();
        _titleButtons.Children.Add(_lockButton);

        _collapseButton = BuildTitleButton(GetToggleGlyph());
        _collapseButton.Click += (_, _) =>
        {
            _autoHideTimer.Stop();
            _hoverExpanded = false;
            _box.IsCollapsed = !_box.IsCollapsed;
            _box.HasUserLayout = true;
            ApplyCollapsedState(animate: true);
            _controller.SaveSettings();
        };
        _titleButtons.Children.Add(_collapseButton);

        var settingsButton = BuildTitleButton("•");
        settingsButton.ToolTip = "盒子设置";
        settingsButton.Click += (_, _) => OpenBoxSettingsMenu(settingsButton);
        _titleButtons.Children.Add(settingsButton);

        var menuButton = BuildTitleButton("⋯");
        menuButton.ToolTip = "菜单";
        menuButton.Click += (_, _) => OpenBoxMenu(menuButton);
        _titleButtons.Children.Add(menuButton);

        BuildSearchPopup();
        return _titleBar;
    }

    private void BuildSearchPopup()
    {
        _searchBox = new TextBox
        {
            Width = 210,
            Height = 30,
            Padding = new Thickness(9, 3, 9, 3),
            FontSize = 12.5,
            Foreground = new SolidColorBrush(TryParseColor(_controller.Settings.BoxTextColor, Colors.White)),
            Background = new SolidColorBrush(Color.FromArgb(210, 36, 58, 68)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            ToolTip = "搜索盒内文件",
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _searchBox.TextChanged += OnSearchTextChanged;
        _searchBox.GotKeyboardFocus += (_, _) => ExpandFromHover();
        _searchBox.LostKeyboardFocus += (_, _) => ScheduleAutoCollapse();
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _searchBox.Clear();
                _searchPopup.IsOpen = false;
                e.Handled = true;
            }
        };

        _searchPopup = new Popup
        {
            PlacementTarget = _searchButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(225, 25, 40, 48)),
                Padding = new Thickness(6),
                Child = _searchBox
            }
        };
        _searchPopup.Closed += (_, _) => ScheduleAutoCollapse();
    }

    private WpfButton BuildTitleButton(string text)
    {
        return new WpfButton
        {
            Content = text,
            Width = 24,
            Height = _controller.Settings.CompactTitleBar ? 20 : 24,
            Margin = new Thickness(2, 0, 0, 0),
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
            Margin = new Thickness(0)
        };

        var image = new Image
        {
            Source = _controller.Icons.GetIcon(item.Path, iconSize),
            Width = iconSize,
            Height = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 2),
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

        var frame = new Border
        {
            Width = width + 6,
            MinHeight = panel.MinHeight + 5,
            Margin = new Thickness(2, 2, 2, 3),
            Padding = new Thickness(3, 2, 3, 3),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Tag = item,
            Cursor = Cursors.Hand,
            Focusable = true,
            Child = panel
        };

        AttachItemEvents(frame, item);
        return frame;
    }

    private UIElement BuildListItem(DeskItem item)
    {
        var panel = new DockPanel
        {
            Height = 36,
            LastChildFill = true,
            Margin = new Thickness(0)
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

        var frame = new Border
        {
            Height = 38,
            Margin = new Thickness(2, 1, 2, 1),
            Padding = new Thickness(2, 1, 2, 1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Tag = item,
            Cursor = Cursors.Hand,
            Focusable = true,
            Child = panel
        };

        AttachItemEvents(frame, item);
        return frame;
    }

    private void AttachItemEvents(FrameworkElement element, DeskItem item)
    {
        element.ToolTip = item.Path;
        _itemElements[item.Path] = element;
        ApplyItemSelectionVisual(element, _selectedItemPaths.Contains(item.Path));
        element.MouseEnter += (_, _) => element.Opacity = 0.86;
        element.MouseLeave += (_, _) => element.Opacity = 1;
        element.MouseLeftButtonDown += (_, e) =>
        {
            SelectItem(item, Keyboard.Modifiers);
            element.Focus();
            Keyboard.Focus(element);
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

                _itemDragActive = false;
                e.Handled = true;
                return;
            }

            _itemDragStart = e.GetPosition(this);
            _itemDragActive = true;
            Mouse.Capture(element);
            e.Handled = true;
        };
        element.MouseMove += (_, e) =>
        {
            if (!_itemDragActive || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var current = e.GetPosition(this);
            if (Math.Abs(current.X - _itemDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _itemDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _itemDragActive = false;
            Mouse.Capture(null);
            BeginItemDrag(item);
            e.Handled = true;
        };
        element.MouseLeftButtonUp += (_, _) =>
        {
            _itemDragActive = false;
            Mouse.Capture(null);
        };
        element.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!_selectedItemPaths.Contains(item.Path))
            {
                SelectItem(item, ModifierKeys.None);
            }

            ShowShellContextMenu(element, item, e);
        };
    }

    private void OnItemsBackgroundMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsItemElement(e.OriginalSource))
        {
            return;
        }

        Focus();
        Keyboard.Focus(this);
        ClearSelection(collapseWhenEmpty: true);
    }

    private bool IsItemElement(object source)
    {
        if (source is not DependencyObject dependencyObject)
        {
            return false;
        }

        var current = dependencyObject;
        while (current is not null)
        {
            if (_itemElements.Values.Any(element => ReferenceEquals(element, current)))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void SelectItem(DeskItem item, ModifierKeys modifiers)
    {
        _controller.RecordItemClick(item);
        ExpandPersistentlyFromSelection();
        var noRangeModifier = (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.None;
        if (noRangeModifier && _selectedItemPaths.Count == 1 && _selectedItemPaths.Contains(item.Path))
        {
            _selectedItemPaths.Clear();
        }
        else if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (!_selectedItemPaths.Add(item.Path))
            {
                _selectedItemPaths.Remove(item.Path);
            }
        }
        else if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            _selectedItemPaths.Add(item.Path);
        }
        else
        {
            _selectedItemPaths.Clear();
            _selectedItemPaths.Add(item.Path);
        }

        RefreshSelectionVisuals();
        if (_selectedItemPaths.Count == 0)
        {
            CollapseAfterSelectionCleared();
        }
    }

    private void ExpandPersistentlyFromSelection()
    {
        if (!_box.IsCollapsed && !_hoverExpanded)
        {
            return;
        }

        _autoHideTimer.Stop();
        _hoverExpanded = false;
        _box.IsCollapsed = false;
        _box.HasUserLayout = true;
        ApplyCollapsedState(animate: true);
        _controller.SaveSettings();
    }

    private void PruneSelection(IEnumerable<DeskItem> items)
    {
        var visiblePaths = items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedItemPaths.RemoveWhere(path => !visiblePaths.Contains(path));
    }

    private void ClearSelection(bool collapseWhenEmpty)
    {
        if (_selectedItemPaths.Count == 0)
        {
            return;
        }

        _selectedItemPaths.Clear();
        RefreshSelectionVisuals();
        if (collapseWhenEmpty)
        {
            CollapseAfterSelectionCleared();
        }
    }

    private void CollapseAfterSelectionCleared()
    {
        if (_selectedItemPaths.Count > 0 || _box.IsCollapsed || IsClosed)
        {
            return;
        }

        _autoHideTimer.Stop();
        _hoverExpanded = false;
        _box.IsCollapsed = true;
        _box.HasUserLayout = true;
        ApplyCollapsedState(animate: true);
        _controller.SaveSettings();
    }

    private void RefreshSelectionVisuals()
    {
        foreach (var pair in _itemElements)
        {
            ApplyItemSelectionVisual(pair.Value, _selectedItemPaths.Contains(pair.Key));
        }
    }

    private static void ApplyItemSelectionVisual(FrameworkElement element, bool selected)
    {
        if (element is not Border border)
        {
            return;
        }

        border.Background = selected
            ? new SolidColorBrush(Color.FromArgb(120, 59, 130, 246))
            : Brushes.Transparent;
        border.BorderBrush = selected
            ? new SolidColorBrush(Color.FromArgb(180, 191, 219, 254))
            : Brushes.Transparent;
    }

    private void BeginItemDrag(DeskItem item)
    {
        var paths = _selectedItemPaths.Contains(item.Path)
            ? _selectedItemPaths.Where(ShellOperations.Exists).ToArray()
            : [item.Path];
        if (paths.Length == 0)
        {
            return;
        }

        var fileDropList = new StringCollection();
        fileDropList.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(fileDropList);
        data.SetData(DataFormats.FileDrop, paths);
        var preferredEffect = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Move));
        data.SetData("Preferred DropEffect", preferredEffect);
        var result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        if (result != DragDropEffects.None)
        {
            _controller.RefreshAfterShellChange();
            RefreshItems();
        }
    }

    private IReadOnlyList<DeskItem> GetSelectedItems()
    {
        var selected = _selectedItemPaths
            .Where(ShellOperations.Exists)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            return [];
        }

        return GetFilteredItems()
            .Where(item => selected.Contains(item.Path))
            .ToList();
    }

    private string[] GetSelectedExistingPaths()
    {
        return GetSelectedItems()
            .Select(item => item.Path)
            .Where(ShellOperations.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void CanSelectedFileCommand(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_searchBox.IsKeyboardFocusWithin && GetSelectedExistingPaths().Length > 0;
        e.Handled = e.CanExecute;
    }

    private void OnCopyCommand(object sender, ExecutedRoutedEventArgs e)
    {
        CopySelectedItems(cut: false);
        e.Handled = true;
    }

    private void OnCutCommand(object sender, ExecutedRoutedEventArgs e)
    {
        CopySelectedItems(cut: true);
        e.Handled = true;
    }

    private void OnDeleteCommand(object sender, ExecutedRoutedEventArgs e)
    {
        DeleteSelectedItems();
        e.Handled = true;
    }

    private void CanSelectAllCommand(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_searchBox.IsKeyboardFocusWithin && GetFilteredItems().Count > 0;
        e.Handled = e.CanExecute;
    }

    private void OnSelectAllCommand(object sender, ExecutedRoutedEventArgs e)
    {
        _selectedItemPaths.Clear();
        foreach (var item in GetFilteredItems())
        {
            if (ShellOperations.Exists(item.Path))
            {
                _selectedItemPaths.Add(item.Path);
            }
        }

        RefreshSelectionVisuals();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_searchBox.IsKeyboardFocusWithin)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            CopySelectedItems(cut: false);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.X)
        {
            CopySelectedItems(cut: true);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            PasteIntoBox();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.A)
        {
            _selectedItemPaths.Clear();
            foreach (var item in GetFilteredItems())
            {
                if (ShellOperations.Exists(item.Path))
                {
                    _selectedItemPaths.Add(item.Path);
                }
            }

            RefreshSelectionVisuals();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && modifiers == ModifierKeys.None)
        {
            DeleteSelectedItems();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && modifiers == ModifierKeys.None)
        {
            OpenSelectedItems();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && modifiers == ModifierKeys.None)
        {
            RenameSelectedItem();
            e.Handled = true;
        }
    }

    private void CopySelectedItems(bool cut)
    {
        var paths = GetSelectedExistingPaths();
        if (paths.Length == 0)
        {
            return;
        }

        ShellOperations.CopyFileDrop(paths, cut);
    }

    private void DeleteSelectedItems()
    {
        var paths = GetSelectedExistingPaths();
        if (paths.Length == 0)
        {
            return;
        }

        var message = BuildDeleteConfirmationMessage(paths);
        var result = Forms.MessageBox.Show(message, "CleanDesk", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question);
        if (result != Forms.DialogResult.Yes)
        {
            return;
        }

        foreach (var path in paths)
        {
            ShellOperations.DeleteToRecycleBin(path);
        }

        _selectedItemPaths.Clear();
        RefreshAfterFileOperation(force: true);
    }

    private static string BuildDeleteConfirmationMessage(IReadOnlyList<string> paths)
    {
        var preview = paths
            .Select(GetDeleteDisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(12)
            .Select(name => "  - " + name)
            .ToList();
        var omitted = Math.Max(0, paths.Count - preview.Count);
        if (omitted > 0)
        {
            preview.Add($"  - 另外 {omitted} 个项目...");
        }

        var header = paths.Count == 1
            ? "将把以下项目移入 Windows 回收站："
            : $"将把以下 {paths.Count} 个项目移入 Windows 回收站：";
        return string.Join(Environment.NewLine,
            header,
            "",
            string.Join(Environment.NewLine, preview),
            "",
            "不会直接永久删除。是否继续？");
    }

    private static string GetDeleteDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private void OpenSelectedItems()
    {
        var items = GetSelectedItems();
        if (items.Count == 0)
        {
            return;
        }

        if (items.Count == 1 && _box.Kind == BoxKind.Mapped && items[0].IsDirectory)
        {
            _box.CurrentPath = items[0].Path;
            _controller.SaveSettings();
            RefreshItems();
            return;
        }

        foreach (var item in items)
        {
            _controller.OpenItem(item);
        }
    }

    private void RenameSelectedItem()
    {
        var item = GetSelectedItems().FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var newName = Microsoft.VisualBasic.Interaction.InputBox("输入新的文件名：", "重命名", item.Name);
        if (ShellOperations.Rename(item.Path, newName))
        {
            RefreshAfterFileOperation(force: true);
        }
    }

    private void RefreshAfterFileOperation(bool force)
    {
        _controller.RefreshAfterShellChange(forceRefresh: force);
        RefreshItems();
    }

    private void ConfigureFileDropTarget()
    {
        AllowDrop = true;
        _root.AllowDrop = true;
        _scroll.AllowDrop = true;
        _iconPanel.AllowDrop = true;
        _listPanel.AllowDrop = true;

        DragEnter += OnFilesDragOver;
        DragOver += OnFilesDragOver;
        Drop += OnFilesDrop;
        _root.DragEnter += OnFilesDragOver;
        _root.DragOver += OnFilesDragOver;
        _root.Drop += OnFilesDrop;
        _scroll.DragEnter += OnFilesDragOver;
        _scroll.DragOver += OnFilesDragOver;
        _scroll.Drop += OnFilesDrop;
        _iconPanel.DragEnter += OnFilesDragOver;
        _iconPanel.DragOver += OnFilesDragOver;
        _iconPanel.Drop += OnFilesDrop;
        _listPanel.DragEnter += OnFilesDragOver;
        _listPanel.DragOver += OnFilesDragOver;
        _listPanel.Drop += OnFilesDrop;
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        if (!CanAcceptFileDrop() || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = ResolveDropEffect(e);
        ExpandFromHover();
        e.Handled = true;
    }

    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (!CanAcceptFileDrop() || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var effect = ResolveDropEffect(e);
        if (_controller.ImportDroppedFiles(_box, paths, effect == DragDropEffects.Move))
        {
            _controller.RefreshAfterShellChange(forceRefresh: true);
            e.Effects = effect;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private bool CanAcceptFileDrop()
    {
        return _box.Kind is BoxKind.Normal or BoxKind.Mapped;
    }

    private void CanPasteCommand(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = CanPasteIntoBox();
        e.Handled = e.CanExecute;
    }

    private void OnPasteCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (!CanPasteIntoBox())
        {
            return;
        }

        if (PasteIntoBox())
        {
            e.Handled = true;
        }
    }

    private bool CanPasteIntoBox()
    {
        if (_searchBox.IsKeyboardFocusWithin)
        {
            return false;
        }

        try
        {
            if (CanAcceptFileDrop() && Clipboard.ContainsFileDropList())
            {
                return true;
            }

            return _controller.IsTemporaryWorkspaceBox(_box) &&
                   (Clipboard.ContainsImage() || Clipboard.ContainsText() || Forms.Clipboard.ContainsImage());
        }
        catch (ExternalException ex)
        {
            Logger.Error(ex, "Failed to inspect clipboard.");
            return false;
        }
    }

    private bool PasteIntoBox()
    {
        if (CanAcceptFileDrop() && TryPasteClipboardFiles())
        {
            return true;
        }

        return _controller.IsTemporaryWorkspaceBox(_box) && SaveClipboardIntoTemporaryWorkspace();
    }

    private bool TryPasteClipboardFiles()
    {
        string[] paths;
        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                return false;
            }

            paths = Clipboard.GetFileDropList()
                .Cast<string>()
                .Where(ShellOperations.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (ExternalException ex)
        {
            Logger.Error(ex, "Failed to read file drop clipboard data.");
            return false;
        }

        if (paths.Length == 0)
        {
            return false;
        }

        var move = ClipboardPreferredDropEffectIsMove();
        if (!_controller.ImportDroppedFiles(_box, paths, move))
        {
            return false;
        }

        if (move)
        {
            try
            {
                Clipboard.Clear();
            }
            catch (ExternalException ex)
            {
                Logger.Error(ex, "Failed to clear cut clipboard data after paste.");
            }
        }

        RefreshAfterFileOperation(force: true);
        return true;
    }

    private static bool ClipboardPreferredDropEffectIsMove()
    {
        try
        {
            var data = Clipboard.GetData("Preferred DropEffect");
            if (data is MemoryStream stream)
            {
                stream.Position = 0;
                return stream.ReadByte() == (byte)DragDropEffects.Move;
            }

            if (data is byte[] bytes && bytes.Length > 0)
            {
                return bytes[0] == (byte)DragDropEffects.Move;
            }

            if (data is int effect)
            {
                return (effect & (int)DragDropEffects.Move) == (int)DragDropEffects.Move;
            }
        }
        catch
        {
            // Treat unreadable clipboard metadata as copy.
        }

        return false;
    }

    private bool SaveClipboardIntoTemporaryWorkspace()
    {
        var directory = _controller.GetImportDirectory(_box);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        var savedPaths = new List<string>();
        try
        {
            if (Clipboard.ContainsImage() || Forms.Clipboard.ContainsImage() || ClipboardContainsPngData())
            {
                var path = GetUniqueClipboardPath(directory, "剪贴板图片", ".png");
                if (TrySaveClipboardImage(path))
                {
                    savedPaths.Add(path);
                }
            }

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!string.IsNullOrEmpty(text))
                {
                    var path = GetUniqueClipboardPath(directory, "剪贴板文本", ".txt");
                    File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    savedPaths.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException)
        {
            Logger.Error(ex, "Failed to save clipboard content into temporary workspace.");
            return false;
        }

        if (savedPaths.Count == 0)
        {
            return false;
        }

        _controller.ImportDroppedFiles(_box, savedPaths, move: false);
        _controller.RefreshAfterShellChange(forceRefresh: true);
        RefreshItems();
        ExpandPersistentlyFromSelection();
        return true;
    }

    private static bool ClipboardContainsPngData()
    {
        try
        {
            var dataObject = Forms.Clipboard.GetDataObject();
            return dataObject is not null && ClipboardPngFormats.Any(dataObject.GetDataPresent);
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySaveClipboardImage(string path)
    {
        if (TrySaveClipboardPngData(path))
        {
            return true;
        }

        try
        {
            if (Forms.Clipboard.ContainsImage())
            {
                using var sourceImage = Forms.Clipboard.GetImage();
                if (sourceImage is not null)
                {
                    using var bitmap = new System.Drawing.Bitmap(sourceImage.Width, sourceImage.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(System.Drawing.Color.Transparent);
                        graphics.DrawImage(sourceImage, 0, 0, sourceImage.Width, sourceImage.Height);
                    }

                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException)
        {
            Logger.Error(ex, "Failed to save image from WinForms clipboard.");
        }

        try
        {
            var source = Clipboard.GetImage();
            if (source is null)
            {
                return false;
            }

            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            for (var index = 3; index < pixels.Length; index += 4)
            {
                pixels[index] = 255;
            }

            var opaque = BitmapSource.Create(
                converted.PixelWidth,
                converted.PixelHeight,
                converted.DpiX,
                converted.DpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            using var stream = File.Create(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(opaque));
            encoder.Save(stream);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.Error(ex, "Failed to save image from WPF clipboard.");
            return false;
        }
    }

    private static bool TrySaveClipboardPngData(string path)
    {
        try
        {
            var dataObject = Forms.Clipboard.GetDataObject();
            if (dataObject is null)
            {
                return false;
            }

            foreach (var format in ClipboardPngFormats)
            {
                if (!dataObject.GetDataPresent(format))
                {
                    continue;
                }

                var data = dataObject.GetData(format);
                if (data is Stream stream)
                {
                    stream.Position = 0;
                    using var output = File.Create(path);
                    stream.CopyTo(output);
                    return true;
                }

                if (data is byte[] bytes && bytes.Length > 0)
                {
                    File.WriteAllBytes(path, bytes);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException)
        {
            Logger.Error(ex, "Failed to save raw PNG clipboard data.");
        }

        return false;
    }

    private static readonly string[] ClipboardPngFormats = ["PNG", "PNG image", "image/png"];

    private static string GetUniqueClipboardPath(string directory, string stem, string extension)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var candidate = Path.Combine(directory, $"{stem}_{timestamp}{extension}");
        if (!ShellOperations.Exists(candidate))
        {
            return candidate;
        }

        for (var index = 2; index < 1000; index++)
        {
            candidate = Path.Combine(directory, $"{stem}_{timestamp}_{index}{extension}");
            if (!ShellOperations.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem}_{timestamp}_{Guid.NewGuid():N}{extension}");
    }

    private static DragDropEffects ResolveDropEffect(DragEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
            (e.AllowedEffects & DragDropEffects.Move) == DragDropEffects.Move)
        {
            return DragDropEffects.Move;
        }

        if ((e.AllowedEffects & DragDropEffects.Copy) == DragDropEffects.Copy)
        {
            return DragDropEffects.Copy;
        }

        return (e.AllowedEffects & DragDropEffects.Move) == DragDropEffects.Move
            ? DragDropEffects.Move
            : DragDropEffects.None;
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
        menu.Items.Add(MenuItem("复制", () => CopySelectedItems(cut: false)));
        menu.Items.Add(MenuItem("剪切", () => CopySelectedItems(cut: true)));
        menu.Items.Add(MenuItem("重命名", RenameSelectedItem));
        menu.Items.Add(MenuItem("删除到回收站", DeleteSelectedItems));
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

    private List<DeskItem> GetFilteredItems()
    {
        var items = _controller.GetItemsForBox(_box);
        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            return items.ToList();
        }

        var terms = _searchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return items.ToList();
        }

        return items.Where(item => terms.All(term => ItemMatchesSearch(item, term))).ToList();
    }

    private string BuildRenderSignature(string title, bool usesListMode, IReadOnlyList<DeskItem> items)
    {
        var itemSignature = items.Select(item => string.Join('\u001f',
            item.Path,
            item.DisplayName,
            item.BoxId,
            item.Category,
            item.ClickCount,
            item.OpenCount,
            item.LastClickedUtc?.Ticks ?? 0,
            item.LastOpenedUtc?.Ticks ?? 0,
            item.IsDirectory ? "D" : "F",
            item.IsShortcut ? "L" : "-"));

        return string.Join('\u001e',
            title,
            _searchQuery,
            usesListMode ? "list" : "icon",
            _box.Kind,
            _box.DisplayMode,
            _box.CurrentPath,
            _controller.Settings.ShowFileNames,
            _controller.Settings.MatchDesktopIconSize,
            GetEffectiveIconSize(),
            _controller.Settings.BoxTextColor,
            string.Join('\u001d', itemSignature));
    }

    private static bool ItemMatchesSearch(DeskItem item, string term)
    {
        return Contains(item.DisplayName, term) ||
               Contains(item.Name, term) ||
               Contains(item.Extension, term) ||
               Contains(item.Path, term);
    }

    private static bool Contains(string? value, string term)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    private void AddEmptyState()
    {
        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_searchQuery) ? "暂无文件" : "没有匹配的文件",
            Foreground = new SolidColorBrush(TryParseColor(_controller.Settings.BoxTextColor, Colors.White)),
            Opacity = 0.72,
            FontSize = 12.5,
            Margin = new Thickness(10, 8, 10, 8)
        };

        if (UsesListMode())
        {
            _listPanel.Children.Add(text);
        }
        else
        {
            _iconPanel.Children.Add(text);
        }
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
        menu.Items.Add(MenuItem(_box.IsLocked ? "解除锁定" : "锁定盒子", ToggleLock));
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
        menu.Items.Add(BuildOpacitySliderMenuItem());
        foreach (var opacity in Array.Empty<double>())
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

    private MenuItem BuildOpacitySliderMenuItem()
    {
        var currentOpacity = Math.Clamp(_box.Opacity <= 0 ? _controller.Settings.GlobalOpacity : _box.Opacity, 0.05, 1.0);
        var label = new TextBlock
        {
            Text = $"透明度：{currentOpacity:P0}",
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
        };
        var slider = new Slider
        {
            Minimum = 0.05,
            Maximum = 1.0,
            Value = currentOpacity,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = false,
            Width = 220
        };
        slider.ValueChanged += (_, _) =>
        {
            _box.Opacity = slider.Value;
            label.Text = $"透明度：{slider.Value:P0}";
            RefreshVisualStyle();
            _controller.SaveSettings();
        };

        var panel = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };
        panel.Children.Add(label);
        panel.Children.Add(slider);

        return new MenuItem
        {
            Header = panel,
            StaysOpenOnClick = true,
            IsHitTestVisible = true
        };
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
        menu.Items.Add(MenuItem("在此处打开 PowerShell", OpenTerminalForBox));
        if (_controller.IsTemporaryWorkspaceBox(_box))
        {
            menu.Items.Add(MenuItem("粘贴剪贴板内容", () => SaveClipboardIntoTemporaryWorkspace()));
        }

        menu.Items.Add(MenuItem("解散盒子", () => _controller.RemoveBox(_box)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("创建盒子", _controller.CreateBox));
        menu.Items.Add(MenuItem("创建映射盒子", _controller.CreateMappedBox));
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private void OpenTerminalForBox()
    {
        var directory = _controller.GetImportDirectory(_box);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to open terminal for box: {_box.Name}");
        }
    }

    private void ToggleLock()
    {
        _box.IsLocked = !_box.IsLocked;
        ApplyLockState();
        _controller.SaveSettings();
    }

    private void ApplyLockState()
    {
        _lockButton.Content = _box.IsLocked ? "🔒" : "🔓";
        _lockButton.ToolTip = _box.IsLocked ? "已锁定：点击解除锁定" : "未锁定：点击锁定盒子位置和大小";
        _titleBar.Cursor = _box.IsLocked ? Cursors.Arrow : Cursors.SizeAll;
        _resizeGrip.IsEnabled = !_box.IsLocked && IsContentVisible();
        _resizeGrip.Visibility = _box.IsLocked || !IsContentVisible() ? Visibility.Collapsed : Visibility.Visible;
        _resizeGrip.Opacity = _box.IsLocked ? 0.12 : 0.38;
    }

    private void ToggleSearchPopup()
    {
        _searchPopup.IsOpen = !_searchPopup.IsOpen;
        if (_searchPopup.IsOpen)
        {
            ExpandFromHover();
            Dispatcher.BeginInvoke(() =>
            {
                _searchBox.Focus();
                _searchBox.SelectAll();
            });
        }
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_box.IsLocked || e.ChangedButton != MouseButton.Left || IsInteractiveTitleElement(e.OriginalSource))
        {
            return;
        }

        _dragging = true;
        GetCursorPos(out _dragOrigin);
        var currentWindow = GetWindowRect(IsContentVisible());
        _originLeft = currentWindow.X;
        _originTop = currentWindow.Y;
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
        ApplyBounds(ResolveDockedBounds(_controller.Snap(_box, candidate, false)), true);
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
        _controller.ResolveBoxOverlaps(_box);
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (_box.IsLocked || !IsContentVisible())
        {
            return;
        }

        GetCursorPos(out var current);
        var candidate = _box.Bounds.Clone();
        candidate.Width = Math.Max(180, _originWidth + current.X - _resizeOrigin.X);
        candidate.Height = Math.Max(96, _originHeight + current.Y - _resizeOrigin.Y);
        ApplyBounds(ResolveDockedBounds(_controller.Snap(_box, candidate, true)), true);
    }

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (_box.IsLocked || !IsContentVisible())
        {
            return;
        }

        GetCursorPos(out _resizeOrigin);
        _originWidth = GetExpandedWidth();
        _originHeight = GetExpandedHeight();
    }

    private void ApplyBounds(DesktopRect bounds, bool snapFeedback)
    {
        _box.Bounds = NormalizeDockedBounds(bounds, GetWorkArea());
        PersistExpandedRect(_box.Bounds);
        ApplyTitleDock();
        ApplyCollapsedState();

        _snapHint.BorderBrush = snapFeedback ? new SolidColorBrush(Color.FromArgb(145, 125, 211, 252)) : Brushes.Transparent;
    }

    private void PersistBounds()
    {
        _box.HasUserLayout = true;
        if (IsContentVisible())
        {
            PersistExpandedRect(GetWindowRect(true));
        }

        _controller.SaveSettings();
    }

    private void ApplyCollapsedState(bool animate = false)
    {
        var showContent = IsContentVisible();
        ApplyTitleDock();
        var target = GetWindowRect(showContent);
        var targetScrollVisibility = showContent ? Visibility.Visible : Visibility.Collapsed;
        var toggleGlyph = GetToggleGlyph();
        if (!animate &&
            IsWindowRectCurrent(target) &&
            _scroll.Visibility == targetScrollVisibility &&
            Equals(_collapseButton.Content, toggleGlyph))
        {
            ApplyLockState();
            return;
        }

        if (showContent)
        {
            _scroll.Visibility = Visibility.Visible;
        }

        _collapseButton.Content = toggleGlyph;
        if (!CloseEnough(Left, target.X))
        {
            Left = target.X;
        }

        if (!CloseEnough(Top, target.Y))
        {
            Top = target.Y;
        }

        if (animate)
        {
            AnimateSize(target.Width, target.Height, () =>
            {
                if (!IsContentVisible())
                {
                    _scroll.Visibility = Visibility.Collapsed;
                }

                ApplyLockState();
            });
        }
        else
        {
            if (!CloseEnough(Width, target.Width) || !CloseEnough(Height, target.Height))
            {
                BeginAnimation(HeightProperty, null);
                BeginAnimation(WidthProperty, null);
                Width = target.Width;
                Height = target.Height;
            }

            if (!showContent)
            {
                _scroll.Visibility = Visibility.Collapsed;
            }
        }

        if (!_box.IsCollapsed)
        {
            PersistExpandedRect(target);
        }

        ApplyLockState();
    }

    private bool IsWindowRectCurrent(DesktopRect target)
    {
        return CloseEnough(Left, target.X) &&
               CloseEnough(Top, target.Y) &&
               CloseEnough(Width, target.Width) &&
               CloseEnough(Height, target.Height);
    }

    private static bool CloseEnough(double left, double right)
    {
        return !double.IsNaN(left) && !double.IsInfinity(left) && Math.Abs(left - right) < 0.5;
    }

    private void AnimateSize(double targetWidth, double targetHeight, Action? completed = null)
    {
        BeginAnimation(HeightProperty, null);
        BeginAnimation(WidthProperty, null);

        var heightAnimation = new DoubleAnimation
        {
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var widthAnimation = heightAnimation.Clone();
        widthAnimation.To = targetWidth;

        if (completed is not null)
        {
            heightAnimation.Completed += (_, _) => completed();
        }

        BeginAnimation(WidthProperty, widthAnimation, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(HeightProperty, heightAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private bool IsContentVisible()
    {
        return !_box.IsCollapsed || _hoverExpanded;
    }

    private void ApplyTitleDock()
    {
        var titleLength = GetCollapsedTitleLength();
        MinWidth = CurrentTitleHeight();
        MinHeight = CurrentTitleHeight();
        _titleBar.LayoutTransform = null;

        switch (_box.DockEdge)
        {
            case BoxDockEdge.Left:
                DockPanel.SetDock(_titleBar, Dock.Left);
                ApplyVerticalTitleBar(titleLength);
                break;
            case BoxDockEdge.Right:
                DockPanel.SetDock(_titleBar, Dock.Right);
                ApplyVerticalTitleBar(titleLength);
                break;
            case BoxDockEdge.Top:
            default:
                DockPanel.SetDock(_titleBar, Dock.Top);
                ApplyHorizontalTitleBar(titleLength);
                break;
        }
    }

    private void ApplyHorizontalTitleBar(double titleLength)
    {
        _titleBar.Width = titleLength;
        _titleBar.Height = CurrentTitleHeight();
        _title.Text = BuildTitle();
        _title.TextAlignment = TextAlignment.Left;
        _title.HorizontalAlignment = HorizontalAlignment.Left;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(12, 0, 8, 0);
        _titleButtons.Orientation = Orientation.Horizontal;
        _titleButtons.Margin = new Thickness(0, 0, 8, 0);
        _titleButtons.HorizontalAlignment = HorizontalAlignment.Left;
        _titleButtons.VerticalAlignment = VerticalAlignment.Center;
        SetTitleButtonMargins(new Thickness(2, 0, 0, 0));
        Grid.SetRow(_title, 0);
        Grid.SetColumn(_title, 0);
        Grid.SetRow(_titleButtons, 0);
        Grid.SetColumn(_titleButtons, 1);
    }

    private void ApplyVerticalTitleBar(double titleLength)
    {
        _titleBar.Width = CurrentTitleHeight();
        _titleBar.Height = titleLength;
        _title.Text = ToVerticalTitle(BuildTitle());
        _title.TextAlignment = TextAlignment.Center;
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(0, 8, 0, 6);
        _titleButtons.Orientation = Orientation.Vertical;
        _titleButtons.Margin = new Thickness(0, 0, 0, 8);
        _titleButtons.HorizontalAlignment = HorizontalAlignment.Center;
        _titleButtons.VerticalAlignment = VerticalAlignment.Bottom;
        SetTitleButtonMargins(new Thickness(0, 2, 0, 0));
        Grid.SetRow(_title, 0);
        Grid.SetColumn(_title, 0);
        Grid.SetRow(_titleButtons, 1);
        Grid.SetColumn(_titleButtons, 0);
    }

    private void SetTitleButtonMargins(Thickness margin)
    {
        foreach (var button in _titleButtons.Children.OfType<WpfButton>())
        {
            button.Margin = margin;
        }
    }

    private DesktopRect ResolveDockedBounds(DesktopRect candidate)
    {
        var work = GetWorkArea();
        var snap = Math.Max(8, _controller.Settings.SnapDistance);
        var resolved = candidate.Clone();
        resolved.Width = Math.Clamp(resolved.Width, 180, work.Width);
        resolved.Height = Math.Clamp(resolved.Height, 96, work.Height);

        if (candidate.Y <= work.Top + snap)
        {
            _box.DockEdge = BoxDockEdge.Top;
            resolved.Y = work.Top;
        }
        else if (candidate.X <= work.Left + snap)
        {
            _box.DockEdge = BoxDockEdge.Left;
            resolved.X = work.Left;
        }
        else if (candidate.X + resolved.Width >= work.Right - snap)
        {
            _box.DockEdge = BoxDockEdge.Right;
        }
        else
        {
            resolved.X = Math.Clamp(candidate.X, work.Left, Math.Max(work.Left, work.Right - resolved.Width));
            resolved.Y = Math.Clamp(candidate.Y, work.Top, Math.Max(work.Top, work.Bottom - resolved.Height));
        }

        return NormalizeDockedBounds(resolved, work);
    }

    private DesktopRect GetWindowRect(bool showContent)
    {
        var work = GetWorkArea();
        var titleThickness = CurrentTitleHeight();
        var titleLength = GetCollapsedTitleLength();
        var panelWidth = Math.Clamp(GetExpandedWidth(), Math.Max(180, titleThickness + 120), work.Width);
        var panelHeight = Math.Clamp(GetExpandedHeight(), Math.Max(96, titleThickness), work.Height);

        return _box.DockEdge switch
        {
            BoxDockEdge.Left => new DesktopRect
            {
                X = work.Left,
                Y = Math.Clamp(_box.Bounds.Y, work.Top, Math.Max(work.Top, work.Bottom - titleLength)),
                Width = showContent ? panelWidth : titleThickness,
                Height = titleLength
            },
            BoxDockEdge.Right => CreateRightDockWindowRect(work, _box.Bounds.Y, titleThickness, titleLength, panelWidth, showContent),
            _ => new DesktopRect
            {
                X = Math.Clamp(_box.Bounds.X, work.Left, Math.Max(work.Left, GetTopDockRight(work) - titleLength)),
                Y = work.Top,
                Width = titleLength,
                Height = showContent ? panelHeight : titleThickness
            }
        };
    }

    private static DesktopRect CreateRightDockWindowRect(WorkArea work, double top, double titleThickness, double titleLength, double panelWidth, bool showContent)
    {
        var width = showContent ? panelWidth : titleThickness;
        return new DesktopRect
        {
            X = Math.Max(work.Left, work.Right - width),
            Y = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - titleLength)),
            Width = width,
            Height = titleLength
        };
    }

    private static WorkArea GetWorkArea()
    {
        try
        {
            var work = SystemParameters.WorkArea;
            if (work.Width > 1 && work.Height > 1)
            {
                return new WorkArea(work.Left, work.Top, work.Width, work.Height);
            }
        }
        catch
        {
            // Fall through to WinForms when WPF metrics are unavailable.
        }

        var fallback = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1400, 900);
        return new WorkArea(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    private readonly record struct WorkArea(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    private double GetExpandedHeight()
    {
        var height = _box.Bounds.Height > 0 ? _box.Bounds.Height : _box.LastExpandedHeight;
        if (_box.DockEdge == BoxDockEdge.Top && !_box.HasUserLayout)
        {
            height = Math.Max(height, Math.Max(220, _controller.Settings.DefaultBoxHeight));
        }

        return Math.Max(96, height);
    }

    private double GetExpandedWidth()
    {
        return Math.Max(180, _box.Bounds.Width > 0 ? _box.Bounds.Width : _box.LastExpandedWidth);
    }

    private double GetCollapsedTitleLength()
    {
        var work = GetWorkArea();
        var maxLength = _box.DockEdge == BoxDockEdge.Top ? GetTopDockWidth(work) : work.Height;
        var required = EstimateRequiredTitleLength(BuildTitle(), _box.DockEdge);
        var axisLength = _box.DockEdge == BoxDockEdge.Top
            ? _box.Bounds.Width
            : _box.Bounds.Height;
        _box.TitleLength = Math.Clamp(Math.Max(required, axisLength), 120, Math.Max(120, maxLength));
        SyncTitleAxisLength(_box.TitleLength);
        return _box.TitleLength;
    }

    private DesktopRect NormalizeDockedBounds(DesktopRect bounds, WorkArea work)
    {
        var resolved = bounds.Clone();
        var titleThickness = CurrentTitleHeight();
        var requiredTitleLength = EstimateRequiredTitleLength(BuildTitle(), _box.DockEdge);

        if (_box.DockEdge == BoxDockEdge.Top)
        {
            var availableWidth = GetTopDockWidth(work);
            var right = GetTopDockRight(work);
            var titleLength = Math.Clamp(Math.Max(requiredTitleLength, resolved.Width), 120, Math.Max(120, availableWidth));
            resolved.Width = titleLength;
            resolved.Height = Math.Clamp(resolved.Height, Math.Max(96, titleThickness), work.Height);
            resolved.X = Math.Clamp(resolved.X, work.Left, Math.Max(work.Left, right - titleLength));
            resolved.Y = work.Top;
            return resolved;
        }

        var sideTitleLength = Math.Clamp(Math.Max(requiredTitleLength, resolved.Height), 120, Math.Max(120, work.Height));
        resolved.Width = Math.Clamp(resolved.Width, Math.Max(180, titleThickness + 120), work.Width);
        resolved.Height = sideTitleLength;
        resolved.X = _box.DockEdge == BoxDockEdge.Left
            ? work.Left
            : Math.Max(work.Left, work.Right - resolved.Width);
        resolved.Y = Math.Clamp(resolved.Y, work.Top, Math.Max(work.Top, work.Bottom - sideTitleLength));
        return resolved;
    }

    private void PersistExpandedRect(DesktopRect rect)
    {
        _box.Bounds.X = rect.X;
        _box.Bounds.Y = rect.Y;
        if (_box.DockEdge == BoxDockEdge.Top)
        {
            _box.TitleLength = Math.Max(120, rect.Width);
            _box.Bounds.Width = _box.TitleLength;
            _box.Bounds.Height = Math.Max(96, rect.Height);
            _box.LastExpandedWidth = _box.TitleLength;
            _box.LastExpandedHeight = _box.Bounds.Height;
            return;
        }

        _box.TitleLength = Math.Max(120, rect.Height);
        _box.Bounds.Width = Math.Max(180, rect.Width);
        _box.Bounds.Height = _box.TitleLength;
        _box.LastExpandedWidth = _box.Bounds.Width;
        _box.LastExpandedHeight = _box.TitleLength;
    }

    private void SyncTitleAxisLength(double titleLength)
    {
        if (_box.DockEdge == BoxDockEdge.Top)
        {
            _box.Bounds.Width = titleLength;
            _box.LastExpandedWidth = titleLength;
            return;
        }

        _box.Bounds.Height = titleLength;
        _box.LastExpandedHeight = titleLength;
    }

    private double GetTopDockWidth(WorkArea work)
    {
        return Math.Max(120, work.Width - GetRightDockReserve(work));
    }

    private double GetTopDockRight(WorkArea work)
    {
        return work.Right - GetRightDockReserve(work);
    }

    private double GetRightDockReserve(WorkArea work)
    {
        if (_box.DockEdge != BoxDockEdge.Top)
        {
            return 0;
        }

        var hasRightDockBoxes = _controller.Settings.Boxes.Any(box =>
            box.IsVisible &&
            box.DockEdge == BoxDockEdge.Right &&
            !box.Id.Equals(_box.Id, StringComparison.OrdinalIgnoreCase));
        if (!hasRightDockBoxes)
        {
            return 0;
        }

        var gap = Math.Clamp(_controller.Settings.BoxGap <= 0 ? 18 : _controller.Settings.BoxGap, 12, 28);
        return Math.Max(0, Math.Min(work.Width - 120, CurrentTitleHeight() + gap));
    }

    private static double EstimateRequiredTitleLength(string title, BoxDockEdge edge)
    {
        var text = string.IsNullOrWhiteSpace(title) ? "盒子" : title.Trim();
        if (edge != BoxDockEdge.Top)
        {
            var verticalLength = TitleButtonCount * VerticalTitleButtonStride +
                                 VerticalTitleChromePadding +
                                 text.Length * VerticalTitleCharacterHeight;
            return Math.Clamp(verticalLength, 244, 9000);
        }

        var titleWidth = 0.0;
        foreach (var ch in text)
        {
            titleWidth += ch > 255 ? 14 : 7;
        }

        var horizontalLength = TitleButtonCount * HorizontalTitleButtonStride +
                               HorizontalTitleChromePadding +
                               titleWidth;
        return Math.Clamp(horizontalLength, 220, 9000);
    }

    private string GetToggleGlyph()
    {
        var expanded = IsContentVisible();
        return _box.DockEdge switch
        {
            BoxDockEdge.Left => expanded ? "<" : ">",
            BoxDockEdge.Right => expanded ? ">" : "<",
            _ => expanded ? "^" : "v"
        };
    }

    private static string ToVerticalTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "盒\n子";
        }

        return string.Join('\n', title.Trim().Select(ch => ch.ToString()));
    }

    private void OnWindowMouseEnter(object sender, WpfMouseEventArgs e)
    {
        _autoHideTimer.Stop();
        ExpandFromHover();
    }

    private void OnWindowMouseLeave(object sender, WpfMouseEventArgs e)
    {
        ScheduleAutoCollapse();
    }

    private void ExpandFromHover()
    {
        if (!_box.IsCollapsed || _hoverExpanded || IsClosed)
        {
            return;
        }

        _hoverExpanded = true;
        ApplyCollapsedState(animate: true);
    }

    private void ScheduleAutoCollapse()
    {
        if (!_box.IsCollapsed || !_hoverExpanded)
        {
            return;
        }

        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    private void CollapseHoverExpansionIfReady()
    {
        if (!_box.IsCollapsed || !_hoverExpanded || IsMouseOver || _searchPopup.IsOpen || _searchBox.IsKeyboardFocusWithin || _dragging)
        {
            return;
        }

        _hoverExpanded = false;
        ApplyCollapsedState(animate: true);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = _searchBox.Text.Trim();
        RefreshItems();

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            ExpandFromHover();
        }
    }

    private static bool IsInteractiveTitleElement(object source)
    {
        if (source is not DependencyObject dependencyObject)
        {
            return false;
        }

        var current = dependencyObject;
        while (current is not null)
        {
            if (current is WpfButton or TextBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
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
        var opacity = Math.Clamp(_box.Opacity <= 0 ? _controller.Settings.GlobalOpacity : _box.Opacity, 0.05, 1.0);
        var theme = _controller.Settings.ThemeMode?.ToLowerInvariant() ?? "glass";
        var accent = TryParseColor(_controller.Settings.BoxAccentColor, Color.FromRgb(125, 211, 252));
        var background = TryParseColor(_controller.Settings.BoxBackgroundColor, Color.FromRgb(34, 48, 58));

        if (theme == "transparent")
        {
            return new SolidColorBrush(Color.FromArgb(AlphaFromOpacity(opacity, 4, 190), background.R, background.G, background.B));
        }

        if (theme == "solid")
        {
            return new SolidColorBrush(Color.FromArgb(AlphaFromOpacity(opacity, 35, 245), background.R, background.G, background.B));
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(AlphaFromOpacity(opacity, 18, 225), background.R, background.G, background.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(AlphaFromOpacity(opacity, 8, 172), accent.R, accent.G, accent.B), 0.58));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(AlphaFromOpacity(opacity, 16, 210), 15, 23, 32), 1));
        return brush;
    }

    private static byte AlphaFromOpacity(double opacity, byte minAlpha, byte maxAlpha)
    {
        var normalized = Math.Clamp(opacity, 0.0, 1.0);
        return (byte)Math.Clamp((int)Math.Round(minAlpha + ((maxAlpha - minAlpha) * normalized)), 0, 255);
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

        _titleBar.Background = new LinearGradientBrush(
            Color.FromArgb(AlphaFromOpacity(_controller.Settings.TitleBarOpacity, 8, 190), 255, 255, 255),
            Color.FromArgb(AlphaFromOpacity(_controller.Settings.TitleBarOpacity * 0.85, 4, 150), accentColor.R, accentColor.G, accentColor.B),
            new Point(0, 0),
            new Point(1, 0));

        _title.Foreground = new SolidColorBrush(titleColor);
        _title.FontSize = _controller.Settings.CompactTitleBar ? 12 : 13;
        _searchButton.Height = _controller.Settings.CompactTitleBar ? 20 : 24;
        _terminalButton.Height = _controller.Settings.CompactTitleBar ? 20 : 24;
        _collapseButton.Height = _controller.Settings.CompactTitleBar ? 20 : 24;
        _lockButton.Height = _controller.Settings.CompactTitleBar ? 20 : 24;
        _searchBox.Foreground = new SolidColorBrush(TryParseColor(_controller.Settings.BoxTextColor, Colors.White));
        ApplyLockState();
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
        EnsureDesktopPlacement();
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
