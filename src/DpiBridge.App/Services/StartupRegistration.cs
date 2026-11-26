using DpiBridge.App;
using Microsoft.Win32;

namespace DpiBridge.App.Services;

/// <summary>Windows へのサインイン時に自動起動する設定（HKCU\...\Run）。</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DpiBridge";

    private static string Command => $"\"{Environment.ProcessPath}\" {Program.StartupArg}";

    private static string? Current()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) as string;
    }

    public static bool IsEnabled =>
        Current() is string s && s.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, Command);
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>0.3.0 で登録した値（--startup なし）を新しい形式に直す。</summary>
    public static void Repair()
    {
        try
        {
            if (Current() is string s && s != Command && s.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase))
                Set(true);
        }
        catch (Exception ex) { Log.Error("自動起動設定の更新に失敗", ex); }
    }
}
