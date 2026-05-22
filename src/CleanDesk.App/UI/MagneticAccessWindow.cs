using CleanDesk.App.Models;
using CleanDesk.App.Services;
using System;
using System.Collections.Generic;
using System.Linq;
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
using WpfButton = System.Windows.Controls.Button;

namespace CleanDesk.App.UI;

public sealed class MagneticAccessWindow : Window
{
    private const string DisplayModeList = "List";
    private const string DisplayModeCompact = "Compact";
    private const string DisplayModeIcon = "Icon";
    private const double ResizeGripSize = 18;

    private readonly CleanDeskController _controller;
    private readonly DialogAccessService _service;
    private readonly ScrollViewer _scroll;
    private readonly Border _root;
    private readonly LinearGradientBrush _backgroundBrush;
    private readonly Thumb _resizeGrip;
    private readonly DispatcherTimer _opacitySaveTimer;
    private WpfButton _searchButton = null!;
    private TextBox _searchBox = null!;
    private Popup _searchPopup = null!;
    private Slider _opacitySlider = null!;
    private readonly List<WpfButton> _displayModeButtons = [];
    private readonly Dictionary<string, bool> _expandedByBoxId = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedItemPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<FrameworkElement>> _itemElements = new(StringComparer.OrdinalIgnoreCase);
    private StackPanel _sections;
    private bool _placing;
    private bool _userAdjusted;
    private bool _closingForShutdown;
    private bool _sectionDragActive;
    private Expander? _draggedSection;
    private string _draggedBoxId = "";
    private string _searchQuery = "";

    public MagneticAccessWindow(CleanDeskController controller, DialogAccessService service)
    {
        _controller = controller;
        _service = service;

        Title = "";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        MinWidth = 240;
        MinHeight = 280;
        Width = Math.Clamp(_controller.Settings.MagneticAccessWidth <= 0 ? 300 : _controller.Settings.MagneticAccessWidth, MinWidth, 720);
        Height = Math.Clamp(_controller.Settings.MagneticAccessHeight <= 0 ? 460 : _controller.Settings.MagneticAccessHeight, MinHeight, 820);
        Opacity = 1.0;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _sections = new StackPanel { Orientation = Orientation.Vertical };
        _backgroundBrush = CreateBackgroundBrush();
        _opacitySaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(260)
        };
        _opacitySaveTimer.Tick += (_, _) =>
        {
            _opacitySaveTimer.Stop();
            _controller.SaveSettings();
        };

        _root = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(155, 125, 211, 252)),
            Background = _backgroundBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            SnapsToDevicePixels = true
        };

        var layout = new DockPanel { LastChildFill = true };
        _root.Child = layout;

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _sections,
            Background = Brushes.Transparent
        };
        _scroll.PreviewMouseLeftButtonDown += OnItemsBackgroundMouseDown;
        layout.Children.Add(_scroll);

        _resizeGrip = BuildResizeGrip();

        Content = new Grid
        {
            Children =
            {
                _root,
                _resizeGrip
            }
        };

        SourceInitialized += (_, _) => DesktopInterop.MakeToolWindow(new WindowInteropHelper(this).Handle);
        Closing += OnClosing;
        LocationChanged += (_, _) =>
        {
            if (!_placing && IsVisible)
            {
                _userAdjusted = true;
                RememberUserSize();
            }
        };
        SizeChanged += (_, _) =>
        {
            if (!_placing && IsVisible)
            {
                _userAdjusted = true;
                RememberUserSize();
            }
        };

        UpdateDisplayModeButtons();
    }

    public bool IsUserAdjusted => _userAdjusted;

    public bool IsUserInteracting => IsMouseOver || _sectionDragActive || _resizeGrip.IsDragging;

    public bool IsOwnWindow(IntPtr hwnd)
    {
        var handle = new WindowInteropHelper(this).Handle;
        return handle != IntPtr.Zero && hwnd == handle;
    }

    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        Close();
    }

    public void RefreshContent()
    {
        var expandedSnapshot = _sections.Children
            .OfType<Expander>()
            .ToDictionary(expander => expander.Tag?.ToString() ?? "", expander => expander.IsExpanded, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in expandedSnapshot.Where(pair => !string.IsNullOrWhiteSpace(pair.Key)))
        {
            _expandedByBoxId[pair.Key] = pair.Value;
        }

        ApplyBackgroundOpacity();
        UpdateDisplayModeButtons();
        var replacement = new StackPanel { Orientation = Orientation.Vertical };
        var boxes = GetOrderedVisibleBoxes();
        _itemElements.Clear();
        foreach (var box in boxes)
        {
            AddBoxSection(replacement, box);
        }

        _sections = replacement;
        _scroll.Content = _sections;
        PruneSelectionToRenderedItems();
        RefreshSelectionVisuals();
    }

    public void PositionNear(Rect dialogRectInDevicePixels, bool force)
    {
        if (_userAdjusted && !force)
        {
            return;
        }

        var dialogRect = FromDeviceRect(dialogRectInDevicePixels);
        var work = GetWorkAreaFor(dialogRectInDevicePixels);
        var gap = 10.0;

        _placing = true;
        try
        {
            var maxHeight = Math.Min(820, Math.Max(MinHeight, work.Height - gap * 2));
            var preferredHeight = force || !_userAdjusted
                ? Math.Clamp(dialogRect.Height, MinHeight, maxHeight)
                : Math.Clamp(Height <= 0 ? 460 : Height, MinHeight, maxHeight);
            Height = preferredHeight;

            var preferredWidth = Math.Clamp(Width <= 0 ? 300 : Width, MinWidth, Math.Min(720, Math.Max(MinWidth, work.Width - gap * 2)));
            var availableRight = work.Right - dialogRect.Right - gap;
            if (availableRight >= MinWidth)
            {
                Width = Math.Min(preferredWidth, availableRight);
                Left = dialogRect.Right + gap;
            }
            else
            {
                Width = preferredWidth;
                Left = Math.Max(work.Left + gap, dialogRect.Left - Width - gap);
            }

            Top = Math.Clamp(dialogRect.Top, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - Height - gap));
        }
        finally
        {
            _placing = false;
        }
    }

    private UIElement BuildToolbar()
    {
        var toolbar = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 36,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll
        };
        toolbar.MouseLeftButtonDown += OnToolbarMouseLeftButtonDown;

        var close = BuildToolbarButton("x", "关闭");
        close.FontSize = 13;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        toolbar.Children.Add(close);

        _searchButton = BuildToolbarButton("⌕", "搜索磁力访问窗中的文件和目录");
        _searchButton.Click += (_, _) => ToggleSearchPopup();
        DockPanel.SetDock(_searchButton, Dock.Right);
        toolbar.Children.Add(_searchButton);

        var modePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(modePanel, Dock.Right);
        modePanel.Children.Add(BuildModeButton("列表", DisplayModeList));
        modePanel.Children.Add(BuildModeButton("紧凑", DisplayModeCompact));
        modePanel.Children.Add(BuildModeButton("图标", DisplayModeIcon));
        toolbar.Children.Add(modePanel);

        var opacityPanel = new DockPanel
        {
            LastChildFill = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        opacityPanel.Children.Add(new TextBlock
        {
            Text = "透明度",
            Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0)
        });

        var slider = new Slider
        {
            Minimum = 0.05,
            Maximum = 0.95,
            Value = Math.Clamp(_controller.Settings.MagneticAccessOpacity, 0.05, 0.95),
            TickFrequency = 0.05,
            IsSnapToTickEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "只调节访问窗背景透明度，不影响文字和按钮"
        };
        _opacitySlider = slider;
        slider.ValueChanged += (_, e) =>
        {
            var value = Math.Clamp(e.NewValue, 0.05, 0.95);
            _controller.Settings.MagneticAccessOpacity = value;
            ApplyBackgroundOpacity();
            ScheduleSettingsSave();
        };
        opacityPanel.Children.Add(slider);
        toolbar.Children.Add(opacityPanel);
        BuildSearchPopup();
        return toolbar;
    }

    private void BuildSearchPopup()
    {
        _searchBox = new TextBox
        {
            Width = 240,
            Height = 30,
            Padding = new Thickness(9, 3, 9, 3),
            FontSize = 12.5,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(230, 20, 35, 42)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            ToolTip = "搜索当前访问窗中的文件和目录",
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _searchBox.TextChanged += (_, _) =>
        {
            _searchQuery = _searchBox.Text.Trim();
            RefreshSectionBodies();
        };
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
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(235, 16, 28, 34)),
                Padding = new Thickness(6),
                Child = _searchBox
            }
        };
    }

    private void ToggleSearchPopup()
    {
        _searchPopup.IsOpen = !_searchPopup.IsOpen;
        if (_searchPopup.IsOpen)
        {
            _searchBox.Focus();
            _searchBox.SelectAll();
        }
    }

    private WpfButton BuildModeButton(string text, string mode)
    {
        var button = BuildToolbarButton(text, $"{text}显示");
        button.MinWidth = 42;
        button.Tag = mode;
        button.Click += (_, _) =>
        {
            if (string.Equals(_controller.Settings.MagneticAccessDisplayMode, mode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var previous = CaptureWindowState();
            _controller.Settings.MagneticAccessDisplayMode = mode;
            ScheduleSettingsSave();
            UpdateDisplayModeButtons();
            RefreshSectionBodies();
            RestoreWindowState(previous);
        };
        _displayModeButtons.Add(button);
        return button;
    }

    private static WpfButton BuildToolbarButton(string text, string toolTip)
    {
        return new WpfButton
        {
            Content = text,
            Height = 26,
            MinWidth = 28,
            Margin = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(7, 0, 7, 0),
            FontSize = 11.5,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(68, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = toolTip
        };
    }

    private Thumb BuildResizeGrip()
    {
        var grip = new Thumb
        {
            Width = ResizeGripSize,
            Height = ResizeGripSize,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Opacity = 0.58,
            Background = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255))
        };
        grip.DragStarted += (_, _) => _userAdjusted = true;
        grip.DragDelta += (_, e) =>
        {
            _userAdjusted = true;
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, 720);
            Height = Math.Clamp(Height + e.VerticalChange, MinHeight, 820);
            RememberUserSize();
        };
        return grip;
    }

    private void AddBoxSection(Panel target, BoxModel box)
    {
        var items = GetSectionItems(box);
        var hasItems = items.Count > 0;
        var hasSearch = !string.IsNullOrWhiteSpace(_searchQuery);
        var expander = new Expander
        {
            Tag = box.Id,
            IsExpanded = hasItems && (hasSearch || !_expandedByBoxId.TryGetValue(box.Id, out var expanded) || expanded),
            Header = BuildSectionHeader(box),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8)
        };
        if (!hasItems)
        {
            _expandedByBoxId[box.Id] = false;
        }

        expander.Expanded += (_, _) => _expandedByBoxId[box.Id] = true;
        expander.Collapsed += (_, _) => _expandedByBoxId[box.Id] = false;

        var body = BuildSectionBody(box, items);
        expander.Content = body;
        target.Children.Add(expander);
    }

    private UIElement BuildSectionBody(BoxModel box, IReadOnlyList<DeskItem>? cachedItems = null)
    {
        var mode = CurrentDisplayMode();
        Panel bodyPanel = mode == DisplayModeIcon
            ? new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(2, 3, 2, 4),
                Background = Brushes.Transparent
            }
            : new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(4, 4, 4, 5),
                Background = Brushes.Transparent
            };
        bodyPanel.MouseLeftButtonDown += OnItemsBackgroundMouseDown;

        var items = cachedItems ?? GetSectionItems(box);
        if (items.Count == 0)
        {
            bodyPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_searchQuery) ? "暂无文件" : "无匹配结果",
                Foreground = new SolidColorBrush(Color.FromArgb(205, 255, 255, 255)),
                FontSize = 12,
                Margin = new Thickness(10, 6, 0, 5)
            });
        }
        else
        {
            foreach (var item in items)
            {
                var element = mode switch
                {
                    DisplayModeIcon => BuildIconItem(item),
                    DisplayModeCompact => BuildListItem(item, compact: true),
                    _ => BuildListItem(item, compact: false)
                };
                bodyPanel.Children.Add(element);
            }
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(38, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)),
            BorderThickness = new Thickness(1, 0, 1, 1),
            CornerRadius = new CornerRadius(0, 0, 5, 5),
            Margin = new Thickness(7, -1, 0, 0),
            Padding = new Thickness(2),
            Child = bodyPanel
        };
    }

    private IReadOnlyList<DeskItem> GetSectionItems(BoxModel box)
    {
        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            return _controller.GetItemsForBox(box).Take(120).ToList();
        }

        return SearchIndexService.SearchBox(_controller, box, _searchQuery, 120)
            .Select(result => result.Item)
            .ToList();
    }

    private UIElement BuildSectionHeader(BoxModel box)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(145, 90, 144, 164)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 5, 6, 5)
        };

        var dock = new DockPanel { LastChildFill = true };
        border.Child = dock;

        var dragHandle = BuildSectionDragHandle(box);
        DockPanel.SetDock(dragHandle, Dock.Right);
        dock.Children.Add(dragHandle);

        dock.Children.Add(new TextBlock
        {
            Text = box.Name,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        return border;
    }

    private UIElement BuildSectionDragHandle(BoxModel box)
    {
        var handle = new Border
        {
            Width = 28,
            Height = 24,
            Margin = new Thickness(8, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeAll,
            ToolTip = "拖动调整分类顺序",
            Child = new TextBlock
            {
                Text = "≡",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };

        handle.MouseLeftButtonDown += (_, e) =>
        {
            BeginSectionDrag(box.Id, handle);
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (!_sectionDragActive || !_draggedBoxId.Equals(box.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ReorderDraggedSection(e.GetPosition(_sections).Y);
            e.Handled = true;
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (_sectionDragActive && _draggedBoxId.Equals(box.Id, StringComparison.OrdinalIgnoreCase))
            {
                EndSectionDrag(handle);
                e.Handled = true;
            }
        };
        handle.LostMouseCapture += (_, _) =>
        {
            if (_sectionDragActive && _draggedBoxId.Equals(box.Id, StringComparison.OrdinalIgnoreCase))
            {
                EndSectionDrag(handle);
            }
        };
        return handle;
    }

    private IReadOnlyList<BoxModel> GetOrderedVisibleBoxes()
    {
        var defaultOrder = _controller.VisibleBoxes
            .OrderBy(GetPreferredDefaultOrderRank)
            .ThenBy(GetDockGroup)
            .ThenBy(GetAxisPosition)
            .ThenBy(box => box.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var byId = defaultOrder.ToDictionary(box => box.Id, StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var useSavedOrder = _controller.Settings.MagneticAccessBoxOrderCustomized;
        if (useSavedOrder)
        {
            foreach (var boxId in _controller.Settings.MagneticAccessBoxOrder)
            {
                if (byId.ContainsKey(boxId) && seen.Add(boxId))
                {
                    normalized.Add(boxId);
                }
            }
        }

        foreach (var box in defaultOrder)
        {
            if (seen.Add(box.Id))
            {
                normalized.Add(box.Id);
            }
        }

        _controller.Settings.MagneticAccessBoxOrder = normalized;
        return normalized.Select(id => byId[id]).ToList();
    }

    private void BeginSectionDrag(string boxId, UIElement handle)
    {
        _draggedSection = _sections.Children
            .OfType<Expander>()
            .FirstOrDefault(section => string.Equals(section.Tag?.ToString(), boxId, StringComparison.OrdinalIgnoreCase));
        if (_draggedSection is null)
        {
            return;
        }

        _draggedBoxId = boxId;
        _sectionDragActive = true;
        _draggedSection.Opacity = 0.82;
        Panel.SetZIndex(_draggedSection, 10);
        handle.CaptureMouse();
    }

    private void ReorderDraggedSection(double pointerY)
    {
        if (!_sectionDragActive || _draggedSection is null)
        {
            return;
        }

        var currentIndex = _sections.Children.IndexOf(_draggedSection);
        var targetIndex = GetTargetSectionIndex(pointerY);
        if (currentIndex < 0 || targetIndex < 0 || currentIndex == targetIndex)
        {
            return;
        }

        var before = CaptureSectionTops();
        _sections.Children.RemoveAt(currentIndex);
        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        targetIndex = Math.Clamp(targetIndex, 0, _sections.Children.Count);
        _sections.Children.Insert(targetIndex, _draggedSection);
        PersistCurrentSectionOrder(save: false);
        AnimateSectionReorder(before);
    }

    private void EndSectionDrag(UIElement handle)
    {
        if (!_sectionDragActive)
        {
            return;
        }

        _sectionDragActive = false;
        if (_draggedSection is not null)
        {
            _draggedSection.Opacity = 1.0;
            Panel.SetZIndex(_draggedSection, 0);
        }

        _draggedSection = null;
        _draggedBoxId = "";
        if (handle.IsMouseCaptured)
        {
            handle.ReleaseMouseCapture();
        }

        PersistCurrentSectionOrder(save: true);
    }

    private int GetTargetSectionIndex(double pointerY)
    {
        var count = _sections.Children.Count;
        if (count == 0)
        {
            return -1;
        }

        for (var index = 0; index < count; index++)
        {
            if (_sections.Children[index] is not FrameworkElement child)
            {
                continue;
            }

            var top = child.TranslatePoint(new Point(0, 0), _sections).Y;
            var midpoint = top + (child.ActualHeight / 2);
            if (pointerY < midpoint)
            {
                return index;
            }
        }

        return count;
    }

    private Dictionary<UIElement, double> CaptureSectionTops()
    {
        return _sections.Children
            .OfType<UIElement>()
            .ToDictionary(child => child, child => child.TranslatePoint(new Point(0, 0), _sections).Y);
    }

    private void AnimateSectionReorder(IReadOnlyDictionary<UIElement, double> before)
    {
        _sections.UpdateLayout();
        foreach (var child in _sections.Children.OfType<FrameworkElement>())
        {
            if (!before.TryGetValue(child, out var previousTop))
            {
                continue;
            }

            var currentTop = child.TranslatePoint(new Point(0, 0), _sections).Y;
            var delta = previousTop - currentTop;
            if (Math.Abs(delta) < 0.5)
            {
                continue;
            }

            if (child.RenderTransform is not TranslateTransform transform)
            {
                transform = new TranslateTransform();
                child.RenderTransform = transform;
            }

            transform.Y = delta;
            var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(155))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void PersistCurrentSectionOrder(bool save)
    {
        _controller.Settings.MagneticAccessBoxOrder = _sections.Children
            .OfType<Expander>()
            .Select(section => section.Tag?.ToString() ?? "")
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _controller.Settings.MagneticAccessBoxOrderCustomized = true;
        if (save)
        {
            ScheduleSettingsSave();
        }
    }

    private UIElement BuildListItem(DeskItem item, bool compact)
    {
        var label = item.IsDirectory ? $"[目录] {item.DisplayName}" : item.DisplayName;
        var button = new WpfButton
        {
            Content = new TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brushes.White,
                FontSize = compact ? 11.5 : 12
            },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = compact ? new Thickness(0, 1, 0, 0) : new Thickness(0, 2, 0, 0),
            Padding = compact ? new Thickness(8, 3, 8, 3) : new Thickness(9, 5, 9, 5),
            Background = new SolidColorBrush(Color.FromArgb(compact ? (byte)22 : (byte)30, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = item.Path
        };
        button.Click += (_, _) => _service.ActivatePath(item);
        return button;
    }

    private UIElement BuildIconItem(DeskItem item)
    {
        var width = CalculateIconItemWidth();
        var icon = new Image
        {
            Source = _controller.Icons.GetIcon(item.Path, 28),
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 2),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.NearestNeighbor);

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = width - 4
        };
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = item.DisplayName,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            TextAlignment = TextAlignment.Center,
            Foreground = Brushes.White,
            FontSize = 11.5
        });

        var frame = new Border
        {
            Width = width,
            MinHeight = 70,
            Margin = new Thickness(1, 1, 2, 3),
            Padding = new Thickness(2, 2, 2, 2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = true,
            Tag = item,
            ToolTip = item.Path,
            Child = stack
        };
        RegisterItemElement(item.Path, frame);
        ApplyItemSelectionVisual(frame, _selectedItemPaths.Contains(item.Path));
        frame.MouseEnter += (_, _) => frame.Opacity = 0.86;
        frame.MouseLeave += (_, _) => frame.Opacity = 1;
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            var isSelected = SelectItem(item, Keyboard.Modifiers);
            frame.Focus();
            Keyboard.Focus(frame);
            if (isSelected)
            {
                _service.ActivatePath(item);
            }

            e.Handled = true;
        };
        frame.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space))
            {
                return;
            }

            _service.ActivatePath(item);
            e.Handled = true;
        };
        return frame;
    }

    private void OnToolbarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInsideInteractiveElement(e.OriginalSource))
        {
            return;
        }

        _userAdjusted = true;
        e.Handled = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse state changes during activation.
        }
    }

    private void OnItemsBackgroundMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsItemElement(e.OriginalSource))
        {
            return;
        }

        _selectedItemPaths.Clear();
        RefreshSelectionVisuals();
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
            if (_itemElements.Values.Any(elements => elements.Any(element => ReferenceEquals(element, current))))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool SelectItem(DeskItem item, ModifierKeys modifiers)
    {
        _controller.RecordItemClick(item);
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
        return _selectedItemPaths.Contains(item.Path);
    }

    private void PruneSelectionToRenderedItems()
    {
        var renderedPaths = _itemElements.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedItemPaths.RemoveWhere(path => !renderedPaths.Contains(path));
    }

    private void RegisterItemElement(string path, FrameworkElement element)
    {
        if (!_itemElements.TryGetValue(path, out var elements))
        {
            elements = [];
            _itemElements[path] = elements;
        }

        elements.Add(element);
    }

    private void RefreshSelectionVisuals()
    {
        foreach (var pair in _itemElements)
        {
            var selected = _selectedItemPaths.Contains(pair.Key);
            foreach (var element in pair.Value)
            {
                ApplyItemSelectionVisual(element, selected);
            }
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

    private void UpdateDisplayModeButtons()
    {
        var current = CurrentDisplayMode();
        foreach (var button in _displayModeButtons)
        {
            var active = string.Equals(button.Tag?.ToString(), current, StringComparison.OrdinalIgnoreCase);
            button.Background = new SolidColorBrush(active ? Color.FromArgb(95, 125, 211, 252) : Color.FromArgb(34, 255, 255, 255));
            button.BorderBrush = new SolidColorBrush(active ? Color.FromArgb(160, 255, 255, 255) : Color.FromArgb(68, 255, 255, 255));
        }
    }

    private void RefreshSectionBodies()
    {
        var byId = _controller.VisibleBoxes.ToDictionary(box => box.Id, StringComparer.OrdinalIgnoreCase);
        _itemElements.Clear();
        foreach (var section in _sections.Children.OfType<Expander>())
        {
            var boxId = section.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(boxId) || !byId.TryGetValue(boxId, out var box))
            {
                continue;
            }

            var items = GetSectionItems(box);
            section.Content = BuildSectionBody(box, items);
            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                section.IsExpanded = items.Count > 0;
                _expandedByBoxId[box.Id] = section.IsExpanded;
            }
        }

        PruneSelectionToRenderedItems();
        RefreshSelectionVisuals();
    }

    private string CurrentDisplayMode()
    {
        var mode = _controller.Settings.MagneticAccessDisplayMode;
        if (string.Equals(mode, DisplayModeCompact, StringComparison.OrdinalIgnoreCase))
        {
            return DisplayModeCompact;
        }

        if (string.Equals(mode, DisplayModeIcon, StringComparison.OrdinalIgnoreCase))
        {
            return DisplayModeIcon;
        }

        return DisplayModeList;
    }

    private double CalculateIconItemWidth()
    {
        return 92;
    }

    private WindowStateSnapshot CaptureWindowState()
    {
        return new WindowStateSnapshot(Left, Top, Width, Height, _userAdjusted);
    }

    private void RememberUserSize()
    {
        _controller.Settings.MagneticAccessWidth = Math.Clamp(Width <= 0 ? 300 : Width, MinWidth, 720);
        _controller.Settings.MagneticAccessHeight = Math.Clamp(Height <= 0 ? 460 : Height, MinHeight, 820);
    }

    private void ScheduleSettingsSave()
    {
        _opacitySaveTimer.Stop();
        _opacitySaveTimer.Start();
    }

    private void RestoreWindowState(WindowStateSnapshot snapshot)
    {
        _placing = true;
        try
        {
            if (Math.Abs(Left - snapshot.Left) > 0.5)
            {
                Left = snapshot.Left;
            }

            if (Math.Abs(Top - snapshot.Top) > 0.5)
            {
                Top = snapshot.Top;
            }

            if (Math.Abs(Width - snapshot.Width) > 0.5)
            {
                Width = snapshot.Width;
            }

            if (Math.Abs(Height - snapshot.Height) > 0.5)
            {
                Height = snapshot.Height;
            }
        }
        finally
        {
            _placing = false;
            _userAdjusted = snapshot.UserAdjusted;
        }
    }

    private void ApplyBackgroundOpacity()
    {
        UpdateBackgroundBrush(_backgroundBrush);
        if (_opacitySlider is not null)
        {
            var value = Math.Clamp(_controller.Settings.MagneticAccessOpacity, 0.05, 0.95);
            if (Math.Abs(_opacitySlider.Value - value) > 0.001)
            {
                _opacitySlider.Value = value;
            }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingForShutdown)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        _service.NotifyWindowClosedByUser();
    }

    private Rect GetWorkAreaFor(Rect dialogRectInDevicePixels)
    {
        var screen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
            (int)Math.Round(dialogRectInDevicePixels.Left),
            (int)Math.Round(dialogRectInDevicePixels.Top),
            Math.Max(1, (int)Math.Round(dialogRectInDevicePixels.Width)),
            Math.Max(1, (int)Math.Round(dialogRectInDevicePixels.Height))));
        var work = screen.WorkingArea;
        return FromDeviceRect(new Rect(work.Left, work.Top, work.Width, work.Height));
    }

    private Rect FromDeviceRect(Rect deviceRect)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(deviceRect.Left, deviceRect.Top));
        var bottomRight = transform.Transform(new Point(deviceRect.Right, deviceRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private LinearGradientBrush CreateBackgroundBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        UpdateBackgroundBrush(brush);
        return brush;
    }

    private void UpdateBackgroundBrush(LinearGradientBrush brush)
    {
        var opacity = Math.Clamp(_controller.Settings.MagneticAccessOpacity <= 0 ? 0.9 : _controller.Settings.MagneticAccessOpacity, 0.05, 0.95);
        brush.GradientStops[0].Color = Color.FromArgb(AlphaFromOpacity(opacity, 10, 218), 20, 35, 42);
        brush.GradientStops[1].Color = Color.FromArgb(AlphaFromOpacity(opacity, 18, 235), 42, 72, 82);
    }

    private static byte AlphaFromOpacity(double opacity, byte minAlpha, byte maxAlpha)
    {
        var normalized = Math.Clamp(opacity, 0.0, 1.0);
        return (byte)Math.Clamp((int)Math.Round(minAlpha + ((maxAlpha - minAlpha) * normalized)), 0, 255);
    }

    private static bool IsInsideInteractiveElement(object source)
    {
        if (source is not DependencyObject current)
        {
            return false;
        }

        while (current is not null)
        {
            if (current is ButtonBase or Slider or Thumb)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static int GetDockGroup(BoxModel box)
    {
        return box.DockEdge switch
        {
            BoxDockEdge.Top => 0,
            BoxDockEdge.Left => 1,
            BoxDockEdge.Right => 2,
            _ => 3
        };
    }

    private static double GetAxisPosition(BoxModel box)
    {
        return box.DockEdge == BoxDockEdge.Top ? box.Bounds.X : box.Bounds.Y;
    }

    private static int GetPreferredDefaultOrderRank(BoxModel box)
    {
        var preferred = Array.IndexOf(PreferredDefaultBoxNames, box.Name);
        return preferred >= 0 ? preferred : int.MaxValue;
    }

    private static readonly string[] PreferredDefaultBoxNames =
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

    private readonly record struct WindowStateSnapshot(double Left, double Top, double Width, double Height, bool UserAdjusted);
}
