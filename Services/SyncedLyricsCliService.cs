using System.Diagnostics;
using System.Text;
using Lyrictified.Models;

namespace Lyrictified.Services;

public sealed class SyncedLyricsCliService
{
    private static readonly string[] PreferredProviders = ["Lrclib", "NetEase", "Megalobiz"];
    private readonly string? _configuredCommand = Environment.GetEnvironmentVariable("SYNCEDLYRICS_COMMAND");

    public SyncedLyricsCliService()
    {
        IsAvailable = DetectAvailability();
        StatusHint = IsAvailable ? string.Empty : "syncedlyrics helper not installed";
    }

    public bool IsAvailable { get; }

    public string StatusHint { get; }

    public async Task<IReadOnlyList<LyricLine>> GetTimedLyricsAsync(SongInfo song, CancellationToken cancellationToken)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist))
        {
            return Array.Empty<LyricLine>();
        }

        foreach (var command in GetCandidateCommands())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var lyrics = await TryRunCommandAsync(command.FileName, command.Arguments(song), cancellationToken);
                if (lyrics.Count > 0)
                {
                    return lyrics;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SyncedLyricsCliService command '{command.FileName}' failed: {ex}");
            }
        }

        return Array.Empty<LyricLine>();
    }

    private bool DetectAvailability()
    {
        foreach (var command in GetCandidateCommands())
        {
            if (TryProbeCommand(command.FileName, command.ProbeArguments))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<CommandSpec> GetCandidateCommands()
    {
        if (!string.IsNullOrWhiteSpace(_configuredCommand))
        {
            yield return new CommandSpec(_configuredCommand, BuildDirectArgs, "--help");
        }

        yield return new CommandSpec("syncedlyrics", BuildDirectArgs, "--help");
        yield return new CommandSpec("py", BuildPyModuleArgs, "-m syncedlyrics --help");
        yield return new CommandSpec("python", BuildPythonModuleArgs, "-m syncedlyrics --help");
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

    private static string BuildDirectArgs(SongInfo song)
    {
        var searchTerm = BuildSearchTerm(song);
        return $"\"{searchTerm}\" --synced-only -p {string.Join(' ', PreferredProviders)}";
    }

    private static string BuildPyModuleArgs(SongInfo song)
    {
        var searchTerm = BuildSearchTerm(song);
        return $"-m syncedlyrics \"{searchTerm}\" --synced-only -p {string.Join(' ', PreferredProviders)}";
    }

    private static string BuildPythonModuleArgs(SongInfo song)
    {
        var searchTerm = BuildSearchTerm(song);
        return $"-m syncedlyrics \"{searchTerm}\" --synced-only -p {string.Join(' ', PreferredProviders)}";
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
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            _ = await errorTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return Array.Empty<LyricLine>();
            }

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
