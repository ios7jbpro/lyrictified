using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.UI.ViewManagement;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace Lyrictified.Styling;

public sealed class WindowAppearanceManager
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private readonly Window _window;

    public WindowAppearanceManager(Window window)
    {
        _window = window;
    }

    public AppearancePalette Apply()
    {
        var accent = GetAccentBaseColor();
        var backdropApplied = TryApplySystemBackdrop();

        return backdropApplied
            ? CreateBackdropPalette(accent)
            : CreateFallbackPalette(accent);
    }

    private bool TryApplySystemBackdrop()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return false;
        }

        var helper = new WindowInteropHelper(_window);
        if (helper.Handle == IntPtr.Zero)
        {
            return false;
        }

        var darkMode = 1;
        _ = DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, Marshal.SizeOf<int>());

        var backdrop = (int)DwmSystemBackdropType.MainWindow;
        var result = DwmSetWindowAttribute(helper.Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, Marshal.SizeOf<int>());
        return result == 0;
    }

    private static AppearancePalette CreateBackdropPalette(MediaColor accent)
    {
        return new AppearancePalette(
            WindowBackground: WpfBrushes.Transparent,
            SurfaceBackground: new SolidColorBrush(MediaColor.FromArgb(0x58, 0x08, 0x0C, 0x11)),
            SurfaceBorder: new SolidColorBrush(MediaColor.FromArgb(0x90, accent.R, accent.G, accent.B)),
            ButtonBackground: new SolidColorBrush(MediaColor.FromArgb(0x55, accent.R, accent.G, accent.B)),
            ButtonBorder: new SolidColorBrush(MediaColor.FromArgb(0xA0, accent.R, accent.G, accent.B)));
    }

    private static AppearancePalette CreateFallbackPalette(MediaColor accent)
    {
        var surface = Darken(accent, 0.78);
        var button = Darken(accent, 0.68);
        var border = Lighten(surface, 0.18);

        return new AppearancePalette(
            WindowBackground: new SolidColorBrush(Darken(surface, 0.12)),
            SurfaceBackground: new SolidColorBrush(surface),
            SurfaceBorder: new SolidColorBrush(border),
            ButtonBackground: new SolidColorBrush(button),
            ButtonBorder: new SolidColorBrush(Lighten(button, 0.16)));
    }

    private static MediaColor GetAccentBaseColor()
    {
        try
        {
            var settings = new UISettings();
            var accent = settings.GetColorValue(UIColorType.AccentDark2);
            return MediaColor.FromArgb(accent.A, accent.R, accent.G, accent.B);
        }
        catch
        {
            return MediaColor.FromRgb(0x20, 0x46, 0x68);
        }
    }

    private static MediaColor Darken(MediaColor color, double amount)
    {
        var factor = Math.Clamp(1.0 - amount, 0.0, 1.0);
        return MediaColor.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R * factor, 0, 255),
            (byte)Math.Clamp(color.G * factor, 0, 255),
            (byte)Math.Clamp(color.B * factor, 0, 255));
    }

    private static MediaColor Lighten(MediaColor color, double amount)
    {
        return MediaColor.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R + ((255 - color.R) * amount), 0, 255),
            (byte)Math.Clamp(color.G + ((255 - color.G) * amount), 0, 255),
            (byte)Math.Clamp(color.B + ((255 - color.B) * amount), 0, 255));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private enum DwmSystemBackdropType
    {
        Auto = 0,
        None = 1,
        MainWindow = 2,
        TransientWindow = 3,
        TabbedWindow = 4
    }
}

public sealed record AppearancePalette(
    MediaBrush WindowBackground,
    MediaBrush SurfaceBackground,
    MediaBrush SurfaceBorder,
    MediaBrush ButtonBackground,
    MediaBrush ButtonBorder);
