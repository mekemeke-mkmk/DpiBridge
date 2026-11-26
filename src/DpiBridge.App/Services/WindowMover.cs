using System.ComponentModel;
using System.Runtime.InteropServices;
using DpiBridge.App.Native;
using DpiBridge.Core;

namespace DpiBridge.App.Services;

/// <summary>ウィンドウを目標の「見た目の矩形」へ移動・拡縮する（必要ならアニメーション付き）。</summary>
internal static class WindowMover
{
    private const uint Flags = Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_ASYNCWINDOWPOS;

    // 同じウィンドウへの多重アニメーションを防ぐ。
    private static readonly Dictionary<IntPtr, CancellationTokenSource> Running = new();
    private static readonly object Gate = new();

    /// <summary>見た目の矩形 target に合わせる。成功したら true。</summary>
    public static bool MoveTo(IntPtr hwnd, RectI targetVisual, bool animate, int durationMs)
    {
        var fromVisual = WindowInspector.GetVisualRect(hwnd);
        var margins = FrameMargins.Between(WindowInspector.GetWindowRect(hwnd), fromVisual);

        CancellationTokenSource cts;
        lock (Gate)
        {
            if (Running.Remove(hwnd, out var old)) old.Cancel();
            cts = new CancellationTokenSource();
            Running[hwnd] = cts;
        }

        if (!animate || durationMs <= 0)
        {
            bool ok = Apply(hwnd, margins.Expand(targetVisual));
            Finish(hwnd, cts);
            return ok;
        }

        // 最初の1フレームを同期で適用して権限エラー等を確認する。
        var first = SizingEngine.Interpolate(fromVisual, targetVisual, 1.0 / Math.Max(1, durationMs / 16));
        if (!Apply(hwnd, margins.Expand(first)))
        {
            Finish(hwnd, cts);
            return false;
        }

        var token = cts.Token;
        var thread = new Thread(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!token.IsCancellationRequested)
                {
                    // DWM の合成タイミングに合わせて1フレームずつ進める（カクつき・ティアリング防止）。
                    Win32.DwmFlush();
                    double t = sw.Elapsed.TotalMilliseconds / durationMs;
                    var r = SizingEngine.Interpolate(fromVisual, targetVisual, t);
                    if (!Apply(hwnd, margins.Expand(r)) || t >= 1) break;
                }
            }
            catch (Exception ex) { Log.Error("アニメーション中のエラー", ex); }
            finally { Finish(hwnd, cts); }
        }) { IsBackground = true, Name = "DpiBridge.Animate" };
        thread.Start();
        return true;
    }

    private static bool Apply(IntPtr hwnd, RectI r)
    {
        if (Win32.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, Flags)) return true;
        int err = Marshal.GetLastWin32Error();
        Log.Info($"SetWindowPos 失敗 hwnd=0x{hwnd:X} err={err} ({new Win32Exception(err).Message})" +
                 (err == 5 ? " — 対象が管理者権限で動作している可能性。DpiBridge を管理者として実行してください。" : ""));
        return false;
    }

    private static void Finish(IntPtr hwnd, CancellationTokenSource cts)
    {
        lock (Gate)
        {
            if (Running.TryGetValue(hwnd, out var cur) && cur == cts) Running.Remove(hwnd);
        }
        cts.Dispose();
    }
}
