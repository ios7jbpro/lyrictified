namespace Lyrictified.Settings;

public sealed class AppSettings
{
    public DisplayMode DisplayMode { get; set; } = DisplayMode.AppBar;

    public HideMode HideMode { get; set; } = HideMode.Nothing;

    public string? PreferredMonitorDeviceName { get; set; }

    public string? AppBarPreferredMonitorDeviceName { get; set; }

    public string? TaskbarPreferredMonitorDeviceName { get; set; }

    public bool ShowNextLine { get; set; }

    public int? CustomBarHeight { get; set; }

    public int? TaskbarMaximumWidth { get; set; }
}
