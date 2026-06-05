namespace Lyrictified.Models;

public sealed record LyricsResult(
    IReadOnlyList<LyricLine> Lines,
    bool IsTtml,
    IReadOnlyList<LyricLine>? CleanedLrcLines = null);
