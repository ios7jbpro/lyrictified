using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace Lyrictified.Services;

public static class WindowsAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Lyrictified";

    public static void Apply(bool enabled)
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (runKey is null)
            {
                Logger.Log("WindowsAutostartService: could not open Run key");
                return;
            }

            if (!enabled)
            {
                runKey.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var executablePath = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                Logger.Log("WindowsAutostartService: executable path is empty");
                return;
            }

            runKey.SetValue(ValueName, QuoteArgument(executablePath), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Logger.Log($"WindowsAutostartService failed: {ex}");
        }
    }

    private static string? GetExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        try
        {
            var processPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                return processPath;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"WindowsAutostartService process path lookup failed: {ex.Message}");
        }

        return Assembly.GetEntryAssembly()?.Location;
    }

    private static string QuoteArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }
}
