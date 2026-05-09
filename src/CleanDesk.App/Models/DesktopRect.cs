namespace CleanDesk.App.Models;

public sealed class DesktopRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 260;

    public DesktopRect Clone()
    {
        return new DesktopRect
        {
            X = X,
            Y = Y,
            Width = Width,
            Height = Height
        };
    }
}
