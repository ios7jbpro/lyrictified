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

    public LyricAlignment LyricAlignment { get; set; } = LyricAlignment.Center;

    public bool ShowAlbumArt { get; set; } = true;

    public bool WordByWordMode { get; set; }

    public bool TestMode { get; set; }

    public int MaxCacheSize { get; set; } = 25;

    public List<DetectedMediaApp> DetectedMediaApps { get; set; } = new();

    public List<string> IgnoredMediaAppIds { get; set; } = new();
}
