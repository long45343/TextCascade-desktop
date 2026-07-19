using Microsoft.Win32;

namespace TextCascadeSharp.Core;

// 管理开机自启动。Windows 平台通过 HKCU\...\Run 注册表项实现。
// HKCU（HKEY_CURRENT_USER）只对当前用户生效，无需管理员权限。
public static class StartupManager
{
    private const string AppName = "TextCascade";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // 启动参数：用于让程序知道自己是被 Run 项启动的（避免重复显示主窗口）
    private const string StartupArgument = "--startup";

    // 启用/禁用开机自启。enabled=true 时写入注册表项，值为
    //   "C:\path\to\TextCascade.exe" --startup
    // false 时删除该值。
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

    // 检查当前是否已注册开机自启
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    // 旧版本可能写入过没有 --startup 参数的 Run 项。这里把已有的注册表值
    // 标准化为带 --startup 的形式，确保升级后行为一致。
    public static void NormalizeEnabledEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value))
        {
            key.SetValue(AppName, EnsureStartupArgument(value), RegistryValueKind.String);
        }
    }

    // 把路径用双引号包起来。Windows Run 项的值若包含空格必须用引号包裹，
    // 否则系统会把第一个空格之前的部分当作可执行文件路径。
    private static string Quote(string value)
    {
        // 防御性：Windows 路径不会包含双引号，但若万一包含则先剥离，
        // 避免引号嵌套破坏 Run 项的解析（review issue #17）。
        var sanitized = value.Replace("\"", string.Empty);
        return "\"" + sanitized + "\"";
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
