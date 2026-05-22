using CleanDesk.App.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CleanDesk.App.UI;

public sealed class GlobalSearchWindow : Window
{
    private readonly CleanDeskController _controller;
    private readonly DispatcherTimer _searchTimer;
    private readonly TextBox _queryBox;
    private readonly ListView _results;
    private bool _closingForShutdown;

    public GlobalSearchWindow(CleanDeskController controller)
    {
        _controller = controller;
        Title = "CleanDesk 搜索";
        Width = 780;
        Height = 520;
        MinWidth = 560;
        MinHeight = 360;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        AllowsTransparency = false;

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshResults();
        };

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(18, 33, 39)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(125, 211, 252)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12)
        };
        Content = root;

        var layout = new DockPanel { LastChildFill = true };
        root.Child = layout;

        var top = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);

        var hint = new TextBlock
        {
            Text = "Ctrl+空格",
            Foreground = new SolidColorBrush(Color.FromArgb(175, 255, 255, 255)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 2, 0)
        };
        DockPanel.SetDock(hint, Dock.Right);
        top.Children.Add(hint);

        _queryBox = new TextBox
        {
            Height = 34,
            FontSize = 15,
            Padding = new Thickness(10, 5, 10, 5),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(8, 22, 28)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(130, 213, 255)),
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White,
            SelectionBrush = new SolidColorBrush(Color.FromRgb(125, 211, 252)),
            SelectionTextBrush = new SolidColorBrush(Color.FromRgb(2, 20, 30)),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "搜索全部盒子、映射文件夹、最近常用、今日文件和临时工作区"
        };
        _queryBox.TextChanged += (_, _) => QueueSearch();
        _queryBox.KeyDown += OnQueryKeyDown;
        top.Children.Add(_queryBox);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        actions.Children.Add(BuildActionButton("打开", OpenSelected));
        actions.Children.Add(BuildActionButton("定位盒子", RevealSelected));
        actions.Children.Add(BuildActionButton("打开目录", OpenFolderSelected));
        actions.Children.Add(BuildActionButton("关闭", Hide));

        _results = new ListView
        {
            Background = new SolidColorBrush(Color.FromRgb(4, 16, 22)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(113, 190, 217)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(235, 252, 255)),
            ItemContainerStyle = BuildResultItemStyle()
        };
        _results.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(125, 211, 252));
        _results.Resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(Color.FromRgb(2, 20, 30));
        _results.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(Color.FromRgb(67, 149, 190));
        _results.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brushes.White;
        _results.MouseDoubleClick += (_, _) => OpenSelected();
        _results.KeyDown += OnResultsKeyDown;
        _results.View = new GridView
        {
            ColumnHeaderContainerStyle = BuildColumnHeaderStyle(),
            Columns =
            {
                BuildColumn("名称", nameof(SearchResult.DisplayName), 210),
                BuildColumn("类型", nameof(SearchResult.TypeName), 78),
                BuildColumn("所在盒子", nameof(SearchResult.BoxName), 150),
                BuildColumn("最近使用", nameof(SearchResult.LastUsedDisplay), 130),
                BuildColumn("路径", nameof(SearchResult.Path), 420)
            }
        };
        layout.Children.Add(_results);

        Closing += OnClosing;
    }

    public void ShowSearch()
    {
        RefreshResults();
        Show();
        Activate();
        _queryBox.Focus();
        _queryBox.SelectAll();
    }

    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        Close();
    }

    private static GridViewColumn BuildColumn(string title, string path, double width)
    {
        return new GridViewColumn
        {
            Header = title,
            Width = width,
            CellTemplate = BuildCellTemplate(path)
        };
    }

    private static DataTemplate BuildCellTemplate(string path)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        text.SetBinding(TextBlock.ForegroundProperty, new Binding
        {
            Path = new PropertyPath(Control.ForegroundProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListViewItem), 1)
        });
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.MarginProperty, new Thickness(8, 0, 8, 0));
        return new DataTemplate { VisualTree = text };
    }

    private static Style BuildColumnHeaderStyle()
    {
        var style = new Style(typeof(GridViewColumnHeader));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(2, 24, 35))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(216, 244, 255))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(132, 188, 210))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 3, 8, 3)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        return style;
    }

    private static Style BuildResultItemStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(238, 252, 255))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(5, 18, 25))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(75, 113, 190, 217))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0, 4, 0, 4)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(22, 58, 72))));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Triggers.Add(hover);

        var selected = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(125, 211, 252))));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(2, 20, 30))));
        selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Triggers.Add(selected);

        return style;
    }

    private static Button BuildActionButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 78,
            Height = 30,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(72, 121, 140)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(105, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void QueueSearch()
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void RefreshResults()
    {
        var selectedPath = (_results.SelectedItem as SearchResult)?.Path ?? "";
        var results = SearchIndexService.SearchAll(_controller, _queryBox.Text, 140);
        _results.ItemsSource = results;
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            _results.SelectedItem = results.FirstOrDefault(result => result.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        }

        if (_results.SelectedItem is null && results.Count > 0)
        {
            _results.SelectedIndex = 0;
        }
    }

    private SearchResult? SelectedResult()
    {
        return _results.SelectedItem as SearchResult;
    }

    private void OpenSelected()
    {
        if (SelectedResult() is not { } result)
        {
            return;
        }

        _controller.OpenSearchResult(result);
        Hide();
    }

    private void RevealSelected()
    {
        if (SelectedResult() is not { } result)
        {
            return;
        }

        _controller.RevealSearchResult(result);
        Hide();
    }

    private void OpenFolderSelected()
    {
        if (SelectedResult() is not { } result)
        {
            return;
        }

        _controller.OpenSearchResultFolder(result);
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down && _results.Items.Count > 0)
        {
            _results.Focus();
            _results.SelectedIndex = Math.Max(0, _results.SelectedIndex);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                RevealSelected();
            }
            else
            {
                OpenSelected();
            }

            e.Handled = true;
        }
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            OpenSelected();
            e.Handled = true;
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
    }
}
