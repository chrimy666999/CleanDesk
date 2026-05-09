using CleanDesk.App.Models;

namespace CleanDesk.App.Services;

public sealed class BoxLayoutService
{
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
}
