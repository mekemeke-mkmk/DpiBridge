using System.Diagnostics;
using DpiBridge.App.Services;
using DpiBridge.App.UI;

namespace DpiBridge.App;

/// <summary>どうやって起動されたか。</summary>
internal enum LaunchMode
{
    /// <summary>ユーザーが exe を実行した。必ず画面を出して起動したことが分かるようにする。</summary>
    Manual,
    /// <summary>Windows のサインイン時の自動起動。必要なときだけ画面を出す。</summary>
    Startup,
    /// <summary>自動更新後の再起動。</summary>
    Updated,
}

internal static class Program
{
    public const string StartupArg = "--startup";

    [STAThread]
    private static void Main(string[] args)
    {
        Application.ThreadException += (_, e) => Log.Error("UI スレッド例外", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("未処理例外", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        var mode = LaunchMode.Manual;
        if (args.Length >= 1 && args[0] == StartupArg) mode = LaunchMode.Startup;
        if (args.Length >= 2 && args[0] == UpdateService.UpdatedArg)
        {
            mode = LaunchMode.Updated;
            // 自動更新後の起動: 古いプロセスが終わるのを待ってから始める。
            if (int.TryParse(args[1], out int oldPid))
            {
                try { using var old = Process.GetProcessById(oldPid); old.WaitForExit(15000); }
                catch (ArgumentException) { /* もう終わっている */ }
            }
        }
        Log.Info($"起動要求 mode={mode} version={UpdateService.CurrentVersion} path={Environment.ProcessPath}");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); // app.manifest でも宣言済み
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = new Mutex(false, SingleInstance.MutexName);
        if (!AcquireSingleInstance(mutex, mode)) return;

        try
        {
            UpdateService.CleanupOldFiles();
            Application.Run(new TrayApp(mode));
        }
        catch (Exception ex)
        {
            Log.Error("起動に失敗", ex);
            MessageBox.Show(
                $"DpiBridge を起動できませんでした。\n\n{ex.Message}\n\n詳しい内容はログに記録しました:\n{Log.Path}",
                "DpiBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    /// <summary>
    /// 二重起動の防止。既に動いている DpiBridge があれば、その画面を前に出させて自分は終了する。
    /// 応答しない（古いバージョンが動いている等）場合は、終了させて起動し直すかを尋ねる。
    /// </summary>
    private static bool AcquireSingleInstance(Mutex mutex, LaunchMode mode)
    {
        if (TryWait(mutex, mode == LaunchMode.Updated ? 5000 : 0)) return true;

        if (mode == LaunchMode.Startup) return false; // サインイン時の重複起動は黙って終了
        if (SingleInstance.AskExistingToShow(TimeSpan.FromSeconds(3)))
        {
            Log.Info("既に起動中のため、そちらの画面を表示して終了");
            return false;
        }

        var others = Process.GetProcessesByName("DpiBridge").Where(p => p.Id != Environment.ProcessId).ToList();
        var answer = MessageBox.Show(
            "DpiBridge は既に起動していますが応答しません（古いバージョンが動いている可能性があります）。\n\n" +
            "動いている DpiBridge を終了して、このバージョンを起動しますか？",
            "DpiBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return false;

        foreach (var p in others)
        {
            try { p.Kill(); p.WaitForExit(5000); }
            catch (Exception ex) { Log.Error($"PID {p.Id} を終了できません", ex); }
            finally { p.Dispose(); }
        }
        if (TryWait(mutex, 5000)) return true;

        MessageBox.Show("動いている DpiBridge を終了できませんでした。タスクマネージャーで DpiBridge を終了してから、もう一度起動してください。",
            "DpiBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private static bool TryWait(Mutex mutex, int ms)
    {
        try { return mutex.WaitOne(ms); }
        catch (AbandonedMutexException) { return true; } // 前のプロセスが異常終了していた
    }
}
