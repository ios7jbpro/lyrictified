namespace Lyrictified.Settings;

public sealed class DetectedMediaApp
{
    public DetectedMediaApp()
    {
    }

    public DetectedMediaApp(string appId, string displayName)
    {
        AppId = appId;
        DisplayName = displayName;
    }

    public string AppId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}
