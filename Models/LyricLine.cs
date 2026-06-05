namespace Lyrictified.Models;

public sealed record WordInfo(TimeSpan Timestamp, string Word, TimeSpan? EndTime = null);

public sealed record LyricLine(
    TimeSpan Timestamp,
    string Text,
    IReadOnlyList<WordInfo>? Words = null,
    TimeSpan? EndTime = null,
    bool IsTtml = false,
    bool IsBackground = false);
