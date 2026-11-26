using DpiBridge.App.Native;
using DpiBridge.Core;

namespace DpiBridge.App.Services;

internal sealed record CorrectionRecord(DateTime Time, string App, string From, string To, RectI Before, RectI After, bool Success, string? Note);

/// <summary>
/// ユーザーによるウィンドウのドラッグ移動を監視し、モニターをまたいだら移動先に合わせてサイズを補正する。
/// SetWinEventHook (out-of-context) を使うので他プロセスへの DLL 注入はしない。
/// UI スレッド（メッセージループのあるスレッド）で生成すること。
/// </summary>
internal sealed class WindowTracker : IDisposable
{
    private readonly MonitorService _monitors;
    private readonly Func<AppSettings> _settings;
    private readonly Win32.WinEventDelegate _callback; // GC されないよう保持
    private readonly IntPtr _hook;
    private readonly Dictionary<IntPtr, (RectI Visual, MonitorGeometry Monitor)> _starts = new();

    public event Action<CorrectionRecord>? Corrected;

    /// <summary>移動・サイズ変更の開始時に呼ばれる。true を返したら、そのドラッグは呼び出し先が引き受けたので補正しない。</summary>
    public Func<IntPtr, bool>? TakeOver;

    /// <summary>移動・サイズ変更の終了時に呼ばれる（引き受けたドラッグで Windows 標準の移動が止まったかの確認用）。</summary>
    public Action<IntPtr>? MoveSizeEnded;

    public WindowTracker(MonitorService monitors, Func<AppSettings> settings)
    {
        _monitors = monitors;
        _settings = settings;
        _callback = OnWinEvent;
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _callback, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        Log.Info(_hook == IntPtr.Zero ? "SetWinEventHook に失敗しました" : "Windows の移動通知の受け取りを開始しました");
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            // 原因調査のため、Windows からの「移動開始・終了」の通知はすべて記録する（1回のドラッグで2行だけ）。
            Log.Info($"Windows通知: {(ev == Win32.EVENT_SYSTEM_MOVESIZESTART ? "移動開始" : "移動終了")} obj={idObject} " +
                     $"{WindowInspector.Describe(hwnd)} 最大化={Win32.IsZoomed(hwnd)}");
            if (idObject != Win32.OBJID_WINDOW || hwnd == IntPtr.Zero) return;
            if (ev == Win32.EVENT_SYSTEM_MOVESIZESTART)
            {
                if (TakeOver?.Invoke(hwnd) == true) { _starts.Remove(hwnd); return; }
                OnStart(hwnd);
            }
            else if (ev == Win32.EVENT_SYSTEM_MOVESIZEEND)
            {
                MoveSizeEnded?.Invoke(hwnd);
                OnEnd(hwnd);
            }
        }
        catch (Exception ex) { Log.Error("WinEvent 処理中のエラー", ex); }
    }

    private void OnStart(IntPtr hwnd)
    {
        if (!_settings().Enabled) return;
        var visual = WindowInspector.GetVisualRect(hwnd);
        var mon = SizingEngine.MonitorFromPoint(_monitors.Monitors, visual.CenterX, visual.CenterY);
        if (mon is null) return;
        _starts[hwnd] = (visual, mon);
    }

    private void OnEnd(IntPtr hwnd)
    {
        if (!_starts.Remove(hwnd, out var start)) return;
        var settings = _settings();
        if (!settings.Enabled) return;

        // アプリ自身の WM_DPICHANGED 処理（再レイアウト）が落ち着くのを少し待ってから補正する。
        var cursor = Win32.GetCursorPos(out var p) ? (p.X, p.Y) : ((int, int)?)null;
        if (settings.SettleDelayMs > 0)
        {
            var timer = new System.Windows.Forms.Timer { Interval = settings.SettleDelayMs };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                Correct(hwnd, start.Visual, start.Monitor, cursor);
            };
            timer.Start();
        }
        else Correct(hwnd, start.Visual, start.Monitor, cursor);
    }

    private void Correct(IntPtr hwnd, RectI startVisual, MonitorGeometry source, (int, int)? cursor)
    {
        var settings = _settings();
        var end = WindowInspector.GetVisualRect(hwnd);
        var monitors = _monitors.Monitors;
        var dest = SizingEngine.MonitorFromPoint(monitors, end.CenterX, end.CenterY);
        // 移動中にモニター構成が変わった場合に備え、移動元も最新の情報に引き直す。
        source = monitors.FirstOrDefault(m => m.Id == source.Id) ?? source;
        if (dest is null || dest.Id == source.Id) return; // 同じモニター内の移動は対象外（履歴にも残さない）

        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        string exe = Path.GetFileName(WindowInspector.GetExePath(pid) ?? "（管理者権限のアプリ？）").ToLowerInvariant();
        var mode = settings.ModeFor(exe);

        void Skip(string why)
        {
            Log.Info($"補正なし {exe}: {source.FriendlyName}→{dest.FriendlyName} {why}");
            Corrected?.Invoke(new CorrectionRecord(DateTime.Now, exe, source.FriendlyName, dest.FriendlyName, end, end, true, "補正なし: " + why));
        }

        if (WindowInspector.WhyNotResizable(hwnd) is { } reason) { Skip(reason); return; }
        if (mode == SizingMode.None) { Skip("このアプリは「補正しない」設定"); return; }

        var target = SizingEngine.ComputeTarget(new SizingRequest
        {
            StartVisual = startVisual,
            EndVisual = end,
            Source = source,
            Destination = dest,
            Cursor = cursor,
            Mode = mode,
        });
        if (target is null)
        {
            double k = dest.PxPerMm / source.PxPerMm;
            Skip($"既に適切な大きさ（倍率 {k:0.00}）" +
                 (!source.HasRealScale || !dest.HasRealScale ? "。見た目の大きさが不明なモニターがあります → キャリブレーションしてください" : ""));
            return;
        }

        bool ok = WindowMover.MoveTo(hwnd, target.Value, settings.Animate, settings.AnimationMs);
        string? note = ok ? null :
            (WindowInspector.SelfElevated ? "サイズ変更に失敗" : "サイズ変更に失敗（対象が管理者権限の可能性）");
        var rec = new CorrectionRecord(DateTime.Now, exe, source.FriendlyName, dest.FriendlyName, end, target.Value, ok, note);
        Log.Info($"補正 {exe}: {source.FriendlyName}→{dest.FriendlyName} {end} → {target.Value} mode={mode} ok={ok}");
        Corrected?.Invoke(rec);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
    }
}
