namespace DpiBridge.App.Services;

/// <summary>2つ目の起動から、動いている DpiBridge に「画面を出して」と伝えるための名前付きイベント。</summary>
internal static class SingleInstance
{
    public const string MutexName = @"Local\DpiBridge.SingleInstance";
    private const string ShowEventName = @"Local\DpiBridge.Show";
    private const string AckEventName = @"Local\DpiBridge.ShowAck";

    /// <summary>動いている DpiBridge に画面表示を頼む。応答があれば true。</summary>
    public static bool AskExistingToShow(TimeSpan timeout)
    {
        try
        {
            using var ack = new EventWaitHandle(false, EventResetMode.AutoReset, AckEventName);
            ack.Reset();
            if (!EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) return false; // 古いバージョンは待ち受けていない
            using (show) show.Set();
            return ack.WaitOne(timeout);
        }
        catch (Exception ex)
        {
            Log.Error("既存プロセスへの通知に失敗", ex);
            return false;
        }
    }

    /// <summary>他の起動からの要求を待ち受ける。onShow はスレッドプールから呼ばれる。</summary>
    public static IDisposable Listen(Action onShow)
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var ack = new EventWaitHandle(false, EventResetMode.AutoReset, AckEventName);
        var reg = ThreadPool.RegisterWaitForSingleObject(show, (_, _) =>
        {
            try { onShow(); ack.Set(); }
            catch (Exception ex) { Log.Error("画面表示要求の処理に失敗", ex); }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        return new Listener(reg, show, ack);
    }

    private sealed class Listener(RegisteredWaitHandle reg, EventWaitHandle show, EventWaitHandle ack) : IDisposable
    {
        public void Dispose()
        {
            reg.Unregister(null);
            show.Dispose();
            ack.Dispose();
        }
    }
}
