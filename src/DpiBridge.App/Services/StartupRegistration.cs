using Microsoft.Win32;

namespace DpiBridge.App.Services;

/// <summary>Windows へのサインイン時に自動起動する設定（HKCU\...\Run）。</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DpiBridge";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
