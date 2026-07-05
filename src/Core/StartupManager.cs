using Microsoft.Win32;

namespace TextCascadeSharp.Core;

public static class StartupManager
{
    private const string AppName = "TextCascade";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupArgument = "--startup";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open Windows startup registry key.");

        if (enabled)
        {
            key.SetValue(AppName, BuildStartupValue(Environment.ProcessPath ?? Application.ExecutablePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void NormalizeEnabledEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value))
        {
            key.SetValue(AppName, EnsureStartupArgument(value), RegistryValueKind.String);
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value + "\"";
    }

    private static string BuildStartupValue(string executablePath)
    {
        return Quote(executablePath) + " " + StartupArgument;
    }

    private static string EnsureStartupArgument(string value)
    {
        return value.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase)
            ? value
            : value.Trim() + " " + StartupArgument;
    }
}
