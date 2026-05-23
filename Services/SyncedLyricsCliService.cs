using System.Diagnostics;
using System.Text;
using Lyrictified.Models;

namespace Lyrictified.Services;

public sealed class SyncedLyricsCliService
{
    private static readonly string[] PreferredProviders = ["Lrclib", "NetEase", "Musixmatch", "Megalobiz"];
    private readonly string? _configuredCommand = Environment.GetEnvironmentVariable("SYNCEDLYRICS_COMMAND");

    public SyncedLyricsCliService()
    {
        IsAvailable = DetectAvailability();
        StatusHint = IsAvailable ? string.Empty : "syncedlyrics helper not installed";
    }

    public bool IsAvailable { get; }

    public string StatusHint { get; }

    public async Task<IReadOnlyList<LyricLine>> GetTimedLyricsAsync(SongInfo song, bool enhanced, CancellationToken cancellationToken)
    {
        Logger.Log($"SyncedLyricsCli: enhanced={enhanced} song={song.Title}");

        if (!IsAvailable || string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist))
        {
            Logger.Log($"SyncedLyricsCli: not available (IsAvailable={IsAvailable})");
            return Array.Empty<LyricLine>();
        }

        foreach (var command in GetCandidateCommands(enhanced))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var args = command.Arguments(song);
                Logger.Log($"SyncedLyricsCli: running '{command.FileName} {args}'");
                var sw = Stopwatch.StartNew();
                var lyrics = await TryRunCommandAsync(command.FileName, args, cancellationToken);
                sw.Stop();
                var wordCount = lyrics.Count(l => l.Words?.Count > 0);
                Logger.Log($"SyncedLyricsCli: '{command.FileName}' {lyrics.Count} lines, {wordCount} with word data in {sw.ElapsedMilliseconds}ms");
                if (lyrics.Count > 0)
                {
                    return lyrics;
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"SyncedLyricsCli: '{command.FileName}' cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"SyncedLyricsCli '{command.FileName}' failed: {ex.Message}");
            }
        }

        Logger.Log("SyncedLyricsCli: all commands returned nothing");
        return Array.Empty<LyricLine>();
    }

    private bool DetectAvailability()
    {
        foreach (var command in GetCandidateCommands(false))
        {
            if (TryProbeCommand(command.FileName, command.ProbeArguments))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryProbeCommand(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return false;
            }

            if (!process.WaitForExit(1500))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<CommandSpec> GetCandidateCommands(bool enhanced)
    {
        if (!string.IsNullOrWhiteSpace(_configuredCommand))
        {
            yield return new CommandSpec(_configuredCommand, s => BuildArgs(s, enhanced), "--help");
        }

        yield return new CommandSpec("syncedlyrics", s => BuildArgs(s, enhanced), "--help");
        yield return new CommandSpec("py", s => BuildPyArgs(s, enhanced), "-m syncedlyrics --help");
        yield return new CommandSpec("python", s => BuildPyArgs(s, enhanced), "-m syncedlyrics --help");
    }

    private static string BuildArgs(SongInfo song, bool enhanced)
    {
        var searchTerm = BuildSearchTerm(song);
        var enhancedFlag = enhanced ? " --enhanced" : "";
        return $"\"{searchTerm}\" --synced-only -p {string.Join(' ', PreferredProviders)}{enhancedFlag}";
    }

    private static string BuildPyArgs(SongInfo song, bool enhanced)
    {
        var searchTerm = BuildSearchTerm(song);
        var enhancedFlag = enhanced ? " --enhanced" : "";
        return $"-m syncedlyrics \"{searchTerm}\" --synced-only -p {string.Join(' ', PreferredProviders)}{enhancedFlag}";
    }

    private static string BuildSearchTerm(SongInfo song)
    {
        return string.IsNullOrWhiteSpace(song.Artist)
            ? song.Title
            : $"{song.Title} {song.Artist}";
    }

    private static async Task<IReadOnlyList<LyricLine>> TryRunCommandAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        if (!process.Start())
        {
            return Array.Empty<LyricLine>();
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    Logger.Log($"TryRunCommand: stderr: {error.Trim()[..Math.Min(error.Trim().Length, 300)]}");
                return Array.Empty<LyricLine>();
            }

            Logger.Log($"TryRunCommand: stdout ({output.Length} chars): {output[..Math.Min(output.Length, 500)].Replace("\n", "\\n").Replace("\r", "")}");
            return LrcLibLyricsService.ParseSyncedLyrics(output);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
        catch
        {
            TryKillProcess(process);
            return Array.Empty<LyricLine>();
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }

    private sealed record CommandSpec(string FileName, Func<SongInfo, string> Arguments, string ProbeArguments);
}
