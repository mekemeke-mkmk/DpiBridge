namespace DpiBridge.App.Services;

/// <summary>%AppData%\DpiBridge\dpibridge.log への簡易ログ（1MB を超えたら作り直す）。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    public static string Path { get; } = System.IO.Path.Combine(AppPaths.Folder, "dpibridge.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception ex) => Write("ERROR", $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Folder);
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* ログ失敗で本体を止めない */ }
    }
}

internal static class AppPaths
{
    public static string Folder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DpiBridge");
    public static string Settings { get; } = System.IO.Path.Combine(Folder, "settings.json");
}
