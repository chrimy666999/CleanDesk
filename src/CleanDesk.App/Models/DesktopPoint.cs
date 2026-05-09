namespace CleanDesk.App.Models;

public sealed class DesktopPoint
{
    public int X { get; set; }
    public int Y { get; set; }

    public DesktopPoint()
    {
    }

    public DesktopPoint(int x, int y)
    {
        X = x;
        Y = y;
    }
}
