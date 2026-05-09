using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;

namespace CleanDesk.App.Services;

public static class AppIconService
{
    public const string RelativeIconPath = "Assets\\CleanDesk.ico";
    public const string RelativeLogoPath = "Assets\\CleanDesk_logo.png";

    public static string IconPath => Path.Combine(AppContext.BaseDirectory, RelativeIconPath);
    public static string LogoPath => Path.Combine(AppContext.BaseDirectory, RelativeLogoPath);

    public static DrawingIcon CreateNotifyIcon()
    {
        try
        {
            if (File.Exists(IconPath))
            {
                return new DrawingIcon(IconPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load tray icon.");
        }

        return (DrawingIcon)DrawingSystemIcons.Application.Clone();
    }

    public static ImageSource? CreateWindowIcon()
    {
        try
        {
            if (!File.Exists(IconPath))
            {
                return null;
            }

            using var icon = new DrawingIcon(IconPath);
            var image = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load window icon.");
            return null;
        }
    }
}
