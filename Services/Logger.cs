using System.IO;

namespace Lyrictified.Services;

public static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lyrictified", "debug.log");

    private static readonly object _lock = new();

    static Logger()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, $"--- Lyrictified Debug Log {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n");
        }
        catch { }
    }

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
            }
        }
        catch { }
    }
}
