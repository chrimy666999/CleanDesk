using CleanDesk.App.Models;
using Forms = System.Windows.Forms;

namespace CleanDesk.App.Services;

public sealed class AdaptiveBoxLayoutService
{
    private const int TitleHeight = 38;
    private const int ItemWidth = 92;
    private const int ItemHeight = 90;
    private const int PaddingX = 24;
    private const int PaddingY = 16;
    private const int MinimumVisibleWidth = 180;
    private const int MinimumExpandedHeight = 96;
    private const int DefaultCompactGap = 8;

    public void Apply(AppSettings settings, Func<BoxModel, int> itemCountProvider, bool force)
    {
        EnsurePresets(settings);

        var preset = settings.LayoutPresets.FirstOrDefault(preset => preset.Id == settings.ActiveLayoutPresetId)
            ?? settings.LayoutPresets.First();
        var boxes = settings.Boxes.Where(box => box.IsVisible).ToList();
        if (boxes.Count == 0)
        {
            return;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in boxes)
        {
            var count = itemCountProvider(box);
            counts[box.Id] = count;

            if (!force && box.HasUserLayout)
            {
                continue;
            }

            var size = CalculateSize(settings, count);
            box.IsCollapsed = preset.CollapseEmptyBoxes && count == 0;
            box.Bounds.Width = size.Width;
            box.Bounds.Height = box.IsCollapsed ? settings.MinBoxHeight : size.Height;
            box.LastExpandedWidth = size.Width;
            box.LastExpandedHeight = size.Height;
        }

        Arrange(settings, preset, boxes.Where(box => force || !box.HasUserLayout).ToList(), counts);
    }

    public static void EnsurePresets(AppSettings settings)
    {
        if (settings.LayoutPresets.Count == 0)
        {
            settings.LayoutPresets = BoxLayoutPreset.CreateDefaults();
        }

        foreach (var preset in BoxLayoutPreset.CreateDefaults())
        {
            if (!settings.LayoutPresets.Any(existing => existing.Id == preset.Id))
            {
                settings.LayoutPresets.Add(preset);
            }
        }

        foreach (var preset in settings.LayoutPresets.Where(preset => preset.Id is "left" or "right" or "top" or "bottom"))
        {
            preset.Gap = Math.Clamp(preset.Gap <= 0 ? DefaultCompactGap : preset.Gap, 6, 10);
        }

        settings.BoxGap = Math.Clamp(settings.BoxGap <= 0 ? DefaultCompactGap : settings.BoxGap, 6, 10);

        if (settings.LayoutPresets.All(preset => preset.Id != settings.ActiveLayoutPresetId))
        {
            settings.ActiveLayoutPresetId = settings.LayoutPresets.First().Id;
        }
    }

    private static (double Width, double Height) CalculateSize(AppSettings settings, int itemCount)
    {
        if (itemCount <= 0)
        {
            return (Math.Max(settings.MinBoxWidth, 220), settings.MinBoxHeight);
        }

        var columns = itemCount switch
        {
            <= 2 => itemCount,
            <= 6 => 3,
            <= 12 => 4,
            <= 24 => 5,
            _ => 6
        };

        var rows = Math.Min(Math.Max(1, (int)Math.Ceiling(itemCount / (double)Math.Max(1, columns))), itemCount <= 12 ? 3 : 5);
        var width = PaddingX + columns * ItemWidth;
        var height = TitleHeight + PaddingY + rows * ItemHeight;

        width = Math.Clamp(width, settings.MinBoxWidth, settings.MaxBoxWidth);
        height = Math.Clamp(height, Math.Max(MinimumExpandedHeight, settings.MinBoxHeight), settings.MaxBoxHeight);
        return (width, height);
    }

    private static void Arrange(AppSettings settings, BoxLayoutPreset preset, List<BoxModel> boxes, Dictionary<string, int> itemCounts)
    {
        if (boxes.Count == 0)
        {
            return;
        }

        var work = GetPrimaryWorkArea();
        var gap = ResolveGap(settings, preset);
        var ordered = boxes
            .OrderByDescending(box => itemCounts.GetValueOrDefault(box.Id))
            .ThenBy(box => box.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ConstrainToWorkArea(settings, ordered, work, gap);

        switch (preset.Alignment)
        {
            case BoxLayoutAlignment.Right:
                ArrangeVerticalPacked(ordered, work, gap, alignRight: true);
                break;
            case BoxLayoutAlignment.Top:
                ArrangeHorizontalPacked(ordered, work, gap, alignBottom: false);
                break;
            case BoxLayoutAlignment.Bottom:
                ArrangeHorizontalPacked(ordered, work, gap, alignBottom: true);
                break;
            case BoxLayoutAlignment.Left:
            default:
                ArrangeVerticalPacked(ordered, work, gap, alignRight: false);
                break;
        }
    }

    private static int ResolveGap(AppSettings settings, BoxLayoutPreset preset)
    {
        var presetGap = preset.Gap <= 0 ? settings.BoxGap : preset.Gap;
        return Math.Clamp(Math.Min(settings.BoxGap <= 0 ? DefaultCompactGap : settings.BoxGap, presetGap), 6, 10);
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
            // Fall through to WinForms when WPF system metrics are unavailable.
        }

        var fallback = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1400, 900);
        return new WorkArea(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    private static void ConstrainToWorkArea(AppSettings settings, List<BoxModel> boxes, WorkArea work, int gap)
    {
        var usableWidth = Math.Max(MinimumVisibleWidth, work.Width - gap * 2);
        var usableHeight = Math.Max(TitleHeight, work.Height - gap * 2);
        foreach (var box in boxes)
        {
            var minWidth = Math.Min(Math.Max(MinimumVisibleWidth, settings.MinBoxWidth), usableWidth);
            var minHeight = box.IsCollapsed
                ? Math.Min(Math.Max(TitleHeight, settings.MinBoxHeight), usableHeight)
                : Math.Min(MinimumExpandedHeight, usableHeight);
            box.Bounds.Width = Math.Clamp(box.Bounds.Width, minWidth, usableWidth);
            box.Bounds.Height = Math.Clamp(box.Bounds.Height, minHeight, usableHeight);
            if (!box.IsCollapsed)
            {
                box.LastExpandedWidth = box.Bounds.Width;
                box.LastExpandedHeight = box.Bounds.Height;
            }
        }
    }

    private static void ArrangeVerticalPacked(List<BoxModel> boxes, WorkArea work, int gap, bool alignRight)
    {
        var usableWidth = Math.Max(MinimumVisibleWidth, work.Width - gap * 2);
        var usableHeight = Math.Max(TitleHeight, work.Height - gap * 2);
        var maxColumns = Math.Max(1, Math.Min(boxes.Count, FitCount(usableWidth, MinimumVisibleWidth, gap)));
        var columns = BuildVerticalColumns(boxes, maxColumns, usableWidth, usableHeight, gap);
        var totalWidth = columns.Sum(column => column.Width) + gap * Math.Max(0, columns.Count - 1);
        var x = alignRight ? work.Right - gap - totalWidth : work.Left + gap;
        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - gap - totalWidth));

        foreach (var column in columns)
        {
            var y = work.Top + gap;
            foreach (var box in column.Boxes)
            {
                box.Bounds.X = alignRight ? x + column.Width - box.Bounds.Width : x;
                box.Bounds.Y = y;
                ClampIntoWorkArea(box, work, gap);
                y += box.Bounds.Height + gap;
            }

            x += column.Width + gap;
        }
    }

    private static List<PackedColumn> BuildVerticalColumns(List<BoxModel> boxes, int maxColumns, double usableWidth, double usableHeight, int gap)
    {
        for (var columnCount = 1; columnCount <= maxColumns; columnCount++)
        {
            var candidate = SplitIntoColumns(boxes, columnCount, gap);
            var canShrinkToFitWidth = MinimumVisibleWidth * candidate.Count + gap * Math.Max(0, candidate.Count - 1) <= usableWidth;
            if (canShrinkToFitWidth && candidate.All(column => column.Height <= usableHeight))
            {
                ShrinkColumnWidths(candidate, usableWidth, gap);
                return candidate;
            }
        }

        var packed = SplitIntoColumns(boxes, maxColumns, gap);
        ShrinkColumnWidths(packed, usableWidth, gap);
        ShrinkColumnHeights(packed, usableHeight, gap);
        return packed;
    }

    private static List<PackedColumn> SplitIntoColumns(List<BoxModel> boxes, int columnCount, int gap)
    {
        var columns = Enumerable.Range(0, columnCount).Select(_ => new PackedColumn()).ToList();
        var rowsPerColumn = Math.Max(1, (int)Math.Ceiling(boxes.Count / (double)columnCount));
        for (var i = 0; i < boxes.Count; i++)
        {
            var column = columns[Math.Min(columnCount - 1, i / rowsPerColumn)];
            column.Boxes.Add(boxes[i]);
        }

        Recalculate(columns, gap);
        return columns.Where(column => column.Boxes.Count > 0).ToList();
    }

    private static void ShrinkColumnWidths(List<PackedColumn> columns, double usableWidth, int gap)
    {
        var maxColumnWidth = Math.Max(MinimumVisibleWidth, (usableWidth - gap * Math.Max(0, columns.Count - 1)) / Math.Max(1, columns.Count));
        foreach (var column in columns)
        {
            foreach (var box in column.Boxes)
            {
                box.Bounds.Width = Math.Min(box.Bounds.Width, maxColumnWidth);
            }
        }

        Recalculate(columns, gap);
    }

    private static void ShrinkColumnHeights(List<PackedColumn> columns, double usableHeight, int gap)
    {
        foreach (var column in columns)
        {
            if (column.Height <= usableHeight || column.Boxes.Count == 0)
            {
                continue;
            }

            var maxItemHeight = (usableHeight - gap * Math.Max(0, column.Boxes.Count - 1)) / column.Boxes.Count;
            foreach (var box in column.Boxes)
            {
                var minimum = box.IsCollapsed ? TitleHeight : MinimumExpandedHeight;
                box.Bounds.Height = Math.Min(box.Bounds.Height, Math.Max(minimum, maxItemHeight));
            }
        }

        Recalculate(columns, gap);
    }

    private static void ArrangeHorizontalPacked(List<BoxModel> boxes, WorkArea work, int gap, bool alignBottom)
    {
        var usableWidth = Math.Max(MinimumVisibleWidth, work.Width - gap * 2);
        var usableHeight = Math.Max(TitleHeight, work.Height - gap * 2);
        var maxRows = Math.Max(1, Math.Min(boxes.Count, FitCount(usableHeight, TitleHeight, gap)));
        var rows = BuildHorizontalRows(boxes, maxRows, usableWidth, usableHeight, gap);
        var totalHeight = rows.Sum(row => row.Height) + gap * Math.Max(0, rows.Count - 1);
        var y = alignBottom ? work.Bottom - gap - totalHeight : work.Top + gap;
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - gap - totalHeight));

        foreach (var row in rows)
        {
            var x = work.Left + gap;
            foreach (var box in row.Boxes)
            {
                box.Bounds.X = x;
                box.Bounds.Y = alignBottom ? y + row.Height - box.Bounds.Height : y;
                ClampIntoWorkArea(box, work, gap);
                x += box.Bounds.Width + gap;
            }

            y += row.Height + gap;
        }
    }

    private static List<PackedRow> BuildHorizontalRows(List<BoxModel> boxes, int maxRows, double usableWidth, double usableHeight, int gap)
    {
        for (var rowCount = 1; rowCount <= maxRows; rowCount++)
        {
            var candidate = SplitIntoRows(boxes, rowCount, gap);
            var canShrinkToFitHeight = TitleHeight * candidate.Count + gap * Math.Max(0, candidate.Count - 1) <= usableHeight;
            if (canShrinkToFitHeight && candidate.All(row => row.Width <= usableWidth))
            {
                ShrinkRowHeights(candidate, usableHeight, gap);
                return candidate;
            }
        }

        var packed = SplitIntoRows(boxes, maxRows, gap);
        ShrinkRowWidths(packed, usableWidth, gap);
        ShrinkRowHeights(packed, usableHeight, gap);
        return packed;
    }

    private static List<PackedRow> SplitIntoRows(List<BoxModel> boxes, int rowCount, int gap)
    {
        var rows = Enumerable.Range(0, rowCount).Select(_ => new PackedRow()).ToList();
        var columnsPerRow = Math.Max(1, (int)Math.Ceiling(boxes.Count / (double)rowCount));
        for (var i = 0; i < boxes.Count; i++)
        {
            var row = rows[Math.Min(rowCount - 1, i / columnsPerRow)];
            row.Boxes.Add(boxes[i]);
        }

        Recalculate(rows, gap);
        return rows.Where(row => row.Boxes.Count > 0).ToList();
    }

    private static void ShrinkRowWidths(List<PackedRow> rows, double usableWidth, int gap)
    {
        foreach (var row in rows)
        {
            if (row.Width <= usableWidth || row.Boxes.Count == 0)
            {
                continue;
            }

            var maxBoxWidth = Math.Max(MinimumVisibleWidth, (usableWidth - gap * Math.Max(0, row.Boxes.Count - 1)) / row.Boxes.Count);
            foreach (var box in row.Boxes)
            {
                box.Bounds.Width = Math.Min(box.Bounds.Width, maxBoxWidth);
            }
        }

        Recalculate(rows, gap);
    }

    private static void ShrinkRowHeights(List<PackedRow> rows, double usableHeight, int gap)
    {
        var maxRowHeight = Math.Max(TitleHeight, (usableHeight - gap * Math.Max(0, rows.Count - 1)) / Math.Max(1, rows.Count));
        foreach (var row in rows)
        {
            foreach (var box in row.Boxes)
            {
                box.Bounds.Height = Math.Min(box.Bounds.Height, maxRowHeight);
            }
        }

        Recalculate(rows, gap);
    }

    private static int FitCount(double length, double itemLength, int gap)
    {
        return Math.Max(1, (int)Math.Floor((length + gap) / (itemLength + gap)));
    }

    private static void Recalculate(List<PackedColumn> columns, int gap)
    {
        foreach (var column in columns)
        {
            column.Width = column.Boxes.Count == 0 ? 0 : column.Boxes.Max(box => box.Bounds.Width);
            column.Height = column.Boxes.Sum(box => box.Bounds.Height) + Math.Max(0, column.Boxes.Count - 1) * gap;
        }
    }

    private static void Recalculate(List<PackedRow> rows, int gap)
    {
        foreach (var row in rows)
        {
            row.Width = row.Boxes.Sum(box => box.Bounds.Width) + Math.Max(0, row.Boxes.Count - 1) * gap;
            row.Height = row.Boxes.Count == 0 ? 0 : row.Boxes.Max(box => box.Bounds.Height);
        }
    }

    private static void ClampIntoWorkArea(BoxModel box, WorkArea work, int gap)
    {
        var maxWidth = Math.Max(MinimumVisibleWidth, work.Width - gap * 2);
        var maxHeight = Math.Max(TitleHeight, work.Height - gap * 2);
        box.Bounds.Width = Math.Min(box.Bounds.Width, maxWidth);
        box.Bounds.Height = Math.Min(box.Bounds.Height, maxHeight);

        var maxX = Math.Max(work.Left + gap, work.Right - gap - box.Bounds.Width);
        var maxY = Math.Max(work.Top + gap, work.Bottom - gap - box.Bounds.Height);
        box.Bounds.X = Math.Clamp(box.Bounds.X, work.Left + gap, maxX);
        box.Bounds.Y = Math.Clamp(box.Bounds.Y, work.Top + gap, maxY);
    }

    private sealed class PackedColumn
    {
        public List<BoxModel> Boxes { get; } = [];
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private sealed class PackedRow
    {
        public List<BoxModel> Boxes { get; } = [];
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private readonly record struct WorkArea(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }
}
