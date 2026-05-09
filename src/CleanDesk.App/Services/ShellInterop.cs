using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CleanDesk.App.Services;

public static class ShellInterop
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiDisplayName = 0x000000200;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint ShgfiAddOverlays = 0x000000020;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    public static string GetDisplayName(string path)
    {
        try
        {
            var info = new ShFileInfo();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiDisplayName);
            return result == IntPtr.Zero || string.IsNullOrWhiteSpace(info.szDisplayName)
                ? Path.GetFileName(path)
                : info.szDisplayName;
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    public static ImageSource GetIcon(string path, int size)
    {
        var attributes = Directory.Exists(path) ? FileAttributeDirectory : FileAttributeNormal;
        var flags = ShgfiIcon | ShgfiAddOverlays | (size <= 24 ? ShgfiSmallIcon : ShgfiLargeIcon);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            flags |= ShgfiUseFileAttributes;
        }

        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return CreateFallbackIcon(size);
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static ImageSource CreateFallbackIcon(int size)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(232, 238, 246)), null, new Rect(0, 0, size, size));
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(115, 128, 150)), 1), new Rect(0.5, 0.5, size - 1, size - 1));
        }

        drawing.Freeze();
        return new DrawingImage(drawing);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
