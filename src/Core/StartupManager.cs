using Microsoft.Win32;

namespace TextCascadeSharp.Core;

public static class StartupManager
{
    private const string AppName = "TextCascade";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open Windows startup registry key.");

        if (enabled)
        {
            key.SetValue(AppName, Quote(Environment.ProcessPath ?? Application.ExecutablePath), RegistryValueKind.String);
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

    private static string Quote(string value)
    {
        return "\"" + value + "\"";
    }
}
