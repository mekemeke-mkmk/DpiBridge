using System.Diagnostics;
using DpiBridge.App.Services;
using DpiBridge.App.UI;

namespace DpiBridge.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 自動更新後の起動: 古いプロセスが終わるのを待ってから始める。
        bool justUpdated = args.Length >= 2 && args[0] == UpdateService.UpdatedArg;
        if (justUpdated && int.TryParse(args[1], out int oldPid))
        {
            try { using var old = Process.GetProcessById(oldPid); old.WaitForExit(15000); }
            catch (ArgumentException) { /* もう終わっている */ }
        }

        using var mutex = new Mutex(false, @"Local\DpiBridge.SingleInstance");
        bool owned;
        try { owned = mutex.WaitOne(justUpdated ? 5000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return; // 二重起動しない

        Application.ThreadException += (_, e) => Log.Error("UI スレッド例外", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("未処理例外", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        UpdateService.CleanupOldFiles();

        // 高 DPI モードは app.manifest でも PerMonitorV2 を宣言している。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp(justUpdated));
        mutex.ReleaseMutex();
    }
}
