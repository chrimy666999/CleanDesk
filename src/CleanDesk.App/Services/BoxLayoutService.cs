using CleanDesk.App.Models;

namespace CleanDesk.App.Services;

public sealed class BoxLayoutService
{
    private const int TitleHeight = 38;
    private const int TitleButtonCount = 6;
    private const double HorizontalTitleButtonStride = 26;
    private const double HorizontalTitleChromePadding = 48;
    private const double VerticalTitleButtonStride = 26;
    private const double VerticalTitleChromePadding = 40;
    private const double VerticalTitleCharacterHeight = 16;
    private readonly AppSettings _settings;

    public BoxLayoutService(AppSettings settings)
    {
        _settings = settings;
    }

    public DesktopRect Snap(string movingBoxId, DesktopRect candidate, bool resize)
    {
        var snapped = candidate.Clone();
        var snap = Math.Max(4, _settings.SnapDistance);
        var grid = Math.Max(4, _settings.GridSize);
        var workArea = GetWorkArea();

        if (resize)
        {
            return SnapResize(movingBoxId, snapped, snap, grid, workArea);
        }

        var x = snapped.X;
        var y = snapped.Y;
        SnapValue(ref x, workArea.Left, snap);
        SnapValue(ref y, workArea.Top, snap);
        snapped.X = x;
        snapped.Y = y;
        {
            var right = snapped.X + snapped.Width;
            var bottom = snapped.Y + snapped.Height;
            if (Math.Abs(right - workArea.Right) <= snap)
            {
                snapped.X = workArea.Right - snapped.Width;
            }

            if (Math.Abs(bottom - workArea.Bottom) <= snap)
            {
                snapped.Y = workArea.Bottom - snapped.Height;
            }
        }

        foreach (var other in _settings.Boxes.Where(box => box.Id != movingBoxId && box.IsVisible))
        {
            var bounds = other.Bounds;
            x = snapped.X;
            y = snapped.Y;
            SnapValue(ref x, bounds.X, snap);
            SnapValue(ref x, bounds.X + bounds.Width, snap);
            SnapValue(ref y, bounds.Y, snap);
            SnapValue(ref y, bounds.Y + bounds.Height, snap);
            snapped.X = x;
            snapped.Y = y;

            var candidateRight = snapped.X + snapped.Width;
            var candidateBottom = snapped.Y + snapped.Height;
            if (Math.Abs(candidateRight - bounds.X) <= snap)
            {
                snapped.X = bounds.X - snapped.Width;
            }

            if (Math.Abs(candidateRight - (bounds.X + bounds.Width)) <= snap)
            {
                snapped.X = bounds.X + bounds.Width - snapped.Width;
            }

            if (Math.Abs(candidateBottom - bounds.Y) <= snap)
            {
                snapped.Y = bounds.Y - snapped.Height;
            }

            if (Math.Abs(candidateBottom - (bounds.Y + bounds.Height)) <= snap)
            {
                snapped.Y = bounds.Y + bounds.Height - snapped.Height;
            }
        }

        x = snapped.X;
        y = snapped.Y;
        SnapToGrid(ref x, grid, snap);
        SnapToGrid(ref y, grid, snap);
        snapped.X = x;
        snapped.Y = y;

        snapped.X = Math.Max(workArea.Left, Math.Min(snapped.X, workArea.Right - Math.Min(120, snapped.Width)));
        snapped.Y = Math.Max(workArea.Top, Math.Min(snapped.Y, workArea.Bottom - 32));

        return snapped;
    }

    public void ResolveOverlaps(string movingBoxId)
    {
        var workArea = GetWorkArea();
        var gap = Math.Clamp(_settings.BoxGap <= 0 ? 18 : _settings.BoxGap, 12, 28);

        if (!string.IsNullOrWhiteSpace(movingBoxId))
        {
            var movingBox = _settings.Boxes.FirstOrDefault(box => box.Id.Equals(movingBoxId, StringComparison.OrdinalIgnoreCase) && box.IsVisible);
            if (movingBox is not null)
            {
                ResolveMovedBoxOverlap(movingBox, workArea, gap);
            }

            return;
        }

        ResolveFullLayout(workArea, gap);
    }

    private void ResolveFullLayout(WorkArea workArea, int gap)
    {
        foreach (var edge in Enum.GetValues<BoxDockEdge>())
        {
            var boxes = _settings.Boxes
                .Where(box => box.IsVisible && box.DockEdge == edge)
                .OrderBy(box => GetDefaultDockOrder(box, edge))
                .ThenBy(box => edge == BoxDockEdge.Top ? box.Bounds.X : box.Bounds.Y)
                .ToList();

            if (boxes.Count == 0)
            {
                continue;
            }

            if (edge == BoxDockEdge.Top)
            {
                var rightReserve = GetTopRightReserve(workArea, gap);
                MoveTopOverflowToSideColumns(boxes, workArea, gap, rightReserve);
                boxes = _settings.Boxes
                    .Where(box => box.IsVisible && box.DockEdge == BoxDockEdge.Top)
                    .OrderBy(box => GetDefaultDockOrder(box, BoxDockEdge.Top))
                    .ThenBy(box => box.Bounds.X)
                    .ToList();
                PackTopRow(boxes, workArea, gap, rightReserve);
            }
            else
            {
                PackSideColumn(boxes, workArea, gap, edge);
            }
        }
    }

    private void ResolveMovedBoxOverlap(BoxModel movingBox, WorkArea workArea, int gap)
    {
        var edge = movingBox.DockEdge;
        var spans = _settings.Boxes
            .Where(box => box.IsVisible && box.DockEdge == edge)
            .Select(box => new LineSpan(box, GetAxisStart(box, edge), GetTitleLength(box)))
            .OrderBy(span => span.Start)
            .ThenBy(span => span.Box.Id.Equals(movingBox.Id, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(span => GetDefaultDockOrder(span.Box, edge))
            .ToList();

        if (spans.Count == 0)
        {
            return;
        }

        var axisStart = edge == BoxDockEdge.Top ? workArea.Left : workArea.Top;
        var axisEnd = edge == BoxDockEdge.Top ? workArea.Right - GetTopRightReserve(workArea, gap) : workArea.Bottom;
        var axisLength = Math.Max(0, axisEnd - axisStart);
        if (axisLength <= 0)
        {
            return;
        }

        foreach (var span in spans)
        {
            span.Length = Math.Min(span.Length, axisLength);
            span.Start = Math.Clamp(span.Start, axisStart, Math.Max(axisStart, axisEnd - span.Length));
        }

        var movingIndex = spans.FindIndex(span => span.Box.Id.Equals(movingBox.Id, StringComparison.OrdinalIgnoreCase));
        if (movingIndex < 0)
        {
            return;
        }

        var firstAffected = movingIndex;
        var lastAffected = movingIndex;
        var moving = spans[movingIndex];
        var nextStart = moving.End + gap;
        for (var i = movingIndex + 1; i < spans.Count; i++)
        {
            var span = spans[i];
            if (span.Start >= nextStart)
            {
                break;
            }

            span.Start = nextStart;
            lastAffected = i;
            nextStart = span.End + gap;
        }

        var previousEnd = moving.Start - gap;
        for (var i = movingIndex - 1; i >= 0; i--)
        {
            var span = spans[i];
            if (span.End <= previousEnd)
            {
                break;
            }

            span.Start = previousEnd - span.Length;
            firstAffected = i;
            previousEnd = span.Start - gap;
        }

        ConstrainAffectedCluster(spans, ref firstAffected, ref lastAffected, axisStart, axisEnd, gap);

        for (var i = firstAffected; i <= lastAffected; i++)
        {
            ApplySpan(spans[i], edge, workArea);
        }
    }

    private static void ConstrainAffectedCluster(List<LineSpan> spans, ref int firstAffected, ref int lastAffected, double axisStart, double axisEnd, int gap)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            if (spans[firstAffected].Start < axisStart)
            {
                var delta = axisStart - spans[firstAffected].Start;
                ShiftSpans(spans, firstAffected, lastAffected, delta);
                ExtendForward(spans, ref lastAffected, gap);
            }

            if (spans[lastAffected].End > axisEnd)
            {
                var delta = axisEnd - spans[lastAffected].End;
                ShiftSpans(spans, firstAffected, lastAffected, delta);
                ExtendBackward(spans, ref firstAffected, gap);
            }
        }

        if (spans[firstAffected].Start < axisStart && spans[lastAffected].End > axisEnd)
        {
            var usable = Math.Max(0, axisEnd - axisStart - Math.Max(0, spans.Count - 1) * gap);
            var equalLength = usable / Math.Max(1, spans.Count);
            var start = axisStart;
            firstAffected = 0;
            lastAffected = spans.Count - 1;
            foreach (var span in spans)
            {
                span.Length = Math.Min(span.Length, equalLength);
                span.Start = start;
                start = span.End + gap;
            }
        }
    }

    private static void ShiftSpans(List<LineSpan> spans, int first, int last, double delta)
    {
        for (var i = first; i <= last; i++)
        {
            spans[i].Start += delta;
        }
    }

    private static void ExtendForward(List<LineSpan> spans, ref int lastAffected, int gap)
    {
        var nextStart = spans[lastAffected].End + gap;
        for (var i = lastAffected + 1; i < spans.Count; i++)
        {
            var span = spans[i];
            if (span.Start >= nextStart)
            {
                break;
            }

            span.Start = nextStart;
            lastAffected = i;
            nextStart = span.End + gap;
        }
    }

    private static void ExtendBackward(List<LineSpan> spans, ref int firstAffected, int gap)
    {
        var previousEnd = spans[firstAffected].Start - gap;
        for (var i = firstAffected - 1; i >= 0; i--)
        {
            var span = spans[i];
            if (span.End <= previousEnd)
            {
                break;
            }

            span.Start = previousEnd - span.Length;
            firstAffected = i;
            previousEnd = span.Start - gap;
        }
    }

    public static BoxDockEdge GetDefaultDockEdge(string boxName)
    {
        return NormalizeBoxName(boxName) switch
        {
            "图片" or "音乐视频" or "压缩包" => BoxDockEdge.Left,
            "今日文件" or "临时收纳区" or "临时工作区" or "其他" => BoxDockEdge.Right,
            _ => BoxDockEdge.Top
        };
    }

    private static int GetDefaultDockOrder(BoxModel box, BoxDockEdge edge)
    {
        var name = NormalizeBoxName(box.Name);
        return edge switch
        {
            BoxDockEdge.Top => name switch
            {
                "最近常用" => 0,
                "快捷方式" => 1,
                "目录" => 2,
                "文档" => 3,
                _ => 100
            },
            BoxDockEdge.Left => name switch
            {
                "图片" => 0,
                "音乐视频" => 1,
                "压缩包" => 2,
                _ => 100
            },
            BoxDockEdge.Right => name switch
            {
                "今日文件" => 0,
                "临时收纳区" or "临时工作区" => 1,
                "其他" => 2,
                _ => 100
            },
            _ => 100
        };
    }

    private void MoveTopOverflowToSideColumns(IReadOnlyList<BoxModel> topBoxes, WorkArea workArea, int gap, double rightReserve)
    {
        var availableWidth = Math.Max(120, workArea.Width - rightReserve);
        var used = 0.0;
        var overflow = new List<BoxModel>();
        foreach (var box in topBoxes)
        {
            var length = Math.Min(GetTitleLength(box), availableWidth);
            var next = used <= 0 ? length : used + gap + length;
            if (next <= availableWidth || used <= 0)
            {
                used = next;
                continue;
            }

            overflow.Add(box);
        }

        for (var i = 0; i < overflow.Count; i++)
        {
            var box = overflow[i];
            box.DockEdge = i % 2 == 0 ? BoxDockEdge.Left : BoxDockEdge.Right;
            var length = Math.Min(GetTitleLength(box), workArea.Height);
            box.TitleLength = length;
            box.Bounds.Height = length;
            box.LastExpandedHeight = length;
            box.Bounds.X = box.DockEdge == BoxDockEdge.Left
                ? workArea.Left
                : Math.Max(workArea.Left, workArea.Right - Math.Max(box.Bounds.Width, box.LastExpandedWidth));
            box.Bounds.Y = workArea.Top + (i / 2) * (length + gap);
        }
    }

    private DesktopRect SnapResize(string movingBoxId, DesktopRect snapped, int snap, int grid, WorkArea workArea)
    {
        var right = snapped.X + snapped.Width;
        var bottom = snapped.Y + snapped.Height;

        SnapValue(ref right, workArea.Right, snap);
        SnapValue(ref bottom, workArea.Bottom, snap);

        foreach (var other in _settings.Boxes.Where(box => box.Id != movingBoxId && box.IsVisible))
        {
            var bounds = other.Bounds;
            SnapValue(ref right, bounds.X, snap);
            SnapValue(ref right, bounds.X + bounds.Width, snap);
            SnapValue(ref bottom, bounds.Y, snap);
            SnapValue(ref bottom, bounds.Y + bounds.Height, snap);
        }

        SnapToGrid(ref right, grid, snap);
        SnapToGrid(ref bottom, grid, snap);

        snapped.X = Math.Max(workArea.Left, Math.Min(snapped.X, workArea.Right - Math.Min(120, snapped.Width)));
        snapped.Y = Math.Max(workArea.Top, Math.Min(snapped.Y, workArea.Bottom - 32));
        snapped.Width = Math.Min(Math.Max(180, right - snapped.X), workArea.Right - snapped.X);
        snapped.Height = Math.Min(Math.Max(48, bottom - snapped.Y), workArea.Bottom - snapped.Y);

        return snapped;
    }

    private static void PackTopRow(IReadOnlyList<BoxModel> boxes, WorkArea workArea, int gap, double rightReserve)
    {
        var availableRight = workArea.Right - rightReserve;
        var availableWidth = Math.Max(120, availableRight - workArea.Left);
        var lengths = FitLengthsToLine(boxes.Select(GetTitleLength).ToList(), availableWidth, gap, 120);
        var total = lengths.Sum() + Math.Max(0, boxes.Count - 1) * gap;
        var start = total > availableWidth
            ? workArea.Left
            : Math.Clamp(boxes[0].Bounds.X, workArea.Left, Math.Max(workArea.Left, availableRight - total));
        var x = start;

        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            var length = lengths[i];
            box.TitleLength = length;
            box.Bounds.X = x;
            box.Bounds.Y = workArea.Top;
            box.Bounds.Width = length;
            box.LastExpandedWidth = length;
            x = box.Bounds.X + length + gap;
        }
    }

    private static void PackSideColumn(IReadOnlyList<BoxModel> boxes, WorkArea workArea, int gap, BoxDockEdge edge)
    {
        var lengths = FitLengthsToLine(boxes.Select(GetTitleLength).ToList(), workArea.Height, gap, 84);
        var total = lengths.Sum() + Math.Max(0, boxes.Count - 1) * gap;
        var start = total > workArea.Height
            ? workArea.Top
            : Math.Clamp(boxes[0].Bounds.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - total));
        var y = start;

        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            var length = lengths[i];
            box.TitleLength = length;
            box.Bounds.Y = y;
            box.Bounds.Height = length;
            box.LastExpandedHeight = length;
            box.Bounds.X = edge == BoxDockEdge.Left
                ? workArea.Left
                : Math.Max(workArea.Left, workArea.Right - Math.Max(box.Bounds.Width, box.LastExpandedWidth));
            y = box.Bounds.Y + length + gap;
        }
    }

    private static double GetTitleLength(BoxModel box)
    {
        var storedAxisLength = box.DockEdge == BoxDockEdge.Top
            ? Math.Max(box.Bounds.Width, box.LastExpandedWidth)
            : Math.Max(box.Bounds.Height, box.LastExpandedHeight);
        return Math.Clamp(Math.Max(EstimateTitleLength(box), Math.Max(box.TitleLength, storedAxisLength)), 120, 9000);
    }

    private double GetTopRightReserve(WorkArea workArea, int gap)
    {
        var hasRightDockBoxes = _settings.Boxes.Any(box => box.IsVisible && box.DockEdge == BoxDockEdge.Right);
        if (!hasRightDockBoxes)
        {
            return 0;
        }

        return Math.Max(0, Math.Min(workArea.Width - 120, TitleHeight + gap));
    }

    private static List<double> FitLengthsToLine(IReadOnlyList<double> preferredLengths, double axisLength, int gap, double minimumLength)
    {
        if (preferredLengths.Count == 0)
        {
            return [];
        }

        var usable = Math.Max(0, axisLength - Math.Max(0, preferredLengths.Count - 1) * gap);
        if (usable <= 0)
        {
            return preferredLengths.Select(_ => 0.0).ToList();
        }

        var lengths = preferredLengths.Select(length => Math.Min(length, usable)).ToList();
        var total = lengths.Sum();
        if (total <= usable)
        {
            return lengths;
        }

        var minimum = Math.Min(minimumLength, usable / preferredLengths.Count);
        var scale = usable / total;
        lengths = lengths.Select(length => Math.Max(minimum, length * scale)).ToList();
        total = lengths.Sum();
        if (total <= usable)
        {
            return lengths;
        }

        var equal = usable / preferredLengths.Count;
        return preferredLengths.Select(_ => equal).ToList();
    }

    private static double EstimateTitleLength(BoxModel box)
    {
        var title = BuildTitle(box);
        var text = string.IsNullOrWhiteSpace(title) ? "盒子" : title.Trim();
        if (box.DockEdge != BoxDockEdge.Top)
        {
            return TitleButtonCount * VerticalTitleButtonStride +
                   VerticalTitleChromePadding +
                   text.Length * VerticalTitleCharacterHeight;
        }

        var titleWidth = 0.0;
        foreach (var ch in text)
        {
            titleWidth += ch > 255 ? 14 : 7;
        }

        return TitleButtonCount * HorizontalTitleButtonStride +
               HorizontalTitleChromePadding +
               titleWidth;
    }

    private static string BuildTitle(BoxModel box)
    {
        if (box.Kind != BoxKind.Mapped)
        {
            return box.Name;
        }

        var current = string.IsNullOrWhiteSpace(box.CurrentPath) ? box.MappedPath : box.CurrentPath;
        return string.IsNullOrWhiteSpace(current) ? box.Name : $"{box.Name}  {current}";
    }

    private static double GetAxisStart(BoxModel box, BoxDockEdge edge)
    {
        return edge == BoxDockEdge.Top ? box.Bounds.X : box.Bounds.Y;
    }

    private static void ApplySpan(LineSpan span, BoxDockEdge edge, WorkArea workArea)
    {
        span.Box.TitleLength = span.Length;
        switch (edge)
        {
            case BoxDockEdge.Top:
                span.Box.Bounds.X = span.Start;
                span.Box.Bounds.Y = workArea.Top;
                span.Box.Bounds.Width = span.Length;
                span.Box.LastExpandedWidth = span.Length;
                break;
            case BoxDockEdge.Left:
                span.Box.Bounds.X = workArea.Left;
                span.Box.Bounds.Y = span.Start;
                span.Box.Bounds.Height = span.Length;
                span.Box.LastExpandedHeight = span.Length;
                break;
            case BoxDockEdge.Right:
                span.Box.Bounds.X = Math.Max(workArea.Left, workArea.Right - Math.Max(span.Box.Bounds.Width, span.Box.LastExpandedWidth));
                span.Box.Bounds.Y = span.Start;
                span.Box.Bounds.Height = span.Length;
                span.Box.LastExpandedHeight = span.Length;
                break;
        }
    }

    private static string NormalizeBoxName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
    }

    private static void SnapValue(ref double value, double target, int snap)
    {
        if (Math.Abs(value - target) <= snap)
        {
            value = target;
        }
    }

    private static void SnapToGrid(ref double value, int grid, int snap)
    {
        var target = Math.Round(value / grid) * grid;
        if (Math.Abs(value - target) <= Math.Max(3, snap / 3))
        {
            value = target;
        }
    }

    private static WorkArea GetWorkArea()
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
            // Fall through to a conservative default.
        }

        return new WorkArea(0, 0, 1400, 900);
    }

    private readonly record struct WorkArea(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    private sealed class LineSpan
    {
        public LineSpan(BoxModel box, double start, double length)
        {
            Box = box;
            Start = start;
            Length = length;
        }

        public BoxModel Box { get; }
        public double Start { get; set; }
        public double Length { get; set; }
        public double End => Start + Length;
    }
}
