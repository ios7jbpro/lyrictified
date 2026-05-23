namespace Lyrictified.Models;

public sealed record SongInfo(
    string Title,
    string Artist,
    string? Album,
    TimeSpan Duration,
    bool IsPlaying)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Artist)
        ? Title
        : $"{Title} - {Artist}";
}
