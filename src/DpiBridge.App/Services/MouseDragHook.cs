using System.Runtime.InteropServices;
using DpiBridge.App.Native;

namespace DpiBridge.App.Services;

/// <summary>
/// タイトルバーのドラッグを横取りする低レベルマウスフック（WH_MOUSE_LL）。
/// 他プロセスへの DLL 注入はせず、専用スレッドで動かす（UI が忙しくてもマウスが重くならないように）。
///
/// 流れ:
///  1. タイトルバー上で左ボタンが押されたら、そのイベントを止めて「保留」にする（Windows 標準のドラッグを始めさせない）。
///  2. ドラッグとみなす距離を動いたら DragStarted、その後は DragMoved を通知（カーソル自体は普通に動く）。
///  3. 離したら DragEnded。動かずに離したら、止めていたクリックをそのまま再送する（クリック・ダブルクリックは今まで通り）。
/// 通知はすべてこのフックのスレッドから呼ばれるので、受け側で UI スレッドへ移すこと。
/// </summary>
internal sealed class MouseDragHook : IDisposable
{
    /// <summary>自分で再送した入力の印。これが付いた入力は素通しする。</summary>
    public static readonly IntPtr Marker = new(0x44504942); // "DPIB"

    private enum State { Idle, Pending, Dragging }

    private readonly Win32.LowLevelMouseProc _proc; // GC されないよう保持
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;
    private volatile State _state = State.Idle;
    private IntPtr _target;
    private POINT _down;
    private readonly int _dragX = Math.Max(4, Win32.GetSystemMetrics(Win32.SM_CXDRAG));
    private readonly int _dragY = Math.Max(4, Win32.GetSystemMetrics(Win32.SM_CYDRAG));

    /// <summary>横取りしてよいか（設定が有効で、見た目の違うモニターが2枚以上あるとき）。UI スレッドから更新する。</summary>
    public volatile bool Active;

    public event Action<IntPtr, int, int>? DragStarted;
    public event Action<int, int>? DragMoved;
    public event Action<int, int>? DragEnded;
    /// <summary>動かずに離された → クリックを再送してほしい。</summary>
    public event Action? ClickReleased;

    public MouseDragHook()
    {
        _proc = HookProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "DpiBridge.MouseHook" };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        _hook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Info($"マウスフックの設定に失敗 err={Marshal.GetLastWin32Error()}");
            LastDecision = "マウスフックを設定できませんでした";
            return;
        }
        Log.Info("マウスフックを設定しました");
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
        Win32.UnhookWindowsHookEx(_hook);
    }

    /// <summary>ドラッグを強制的に終わったことにする（取りこぼし対策）。</summary>
    public void Reset() => _state = State.Idle;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo == Marker) return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
            int msg = wParam.ToInt32();

            switch (_state)
            {
                case State.Idle:
                    // 注意: Sunshine/Moonlight 経由の操作は「プログラムが送った入力（INJECTED）」として届くので、
                    // INJECTED かどうかでは除外しない（自分が再送した入力は Marker で見分ける）。
                    if (msg == Win32.WM_LBUTTONDOWN)
                    {
                        if (!Active) { Trace("無効（設定オフ、または見た目の違うモニターが無い）"); break; }
                        if ((Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0) { Trace("Shift 押下中 → Windows 標準"); break; } // Shift なら標準のドラッグ
                        var hwnd = CaptionWindowAt(info.pt, out string why);
                        if (hwnd == IntPtr.Zero) { Trace(why); break; }
                        _target = hwnd;
                        _down = info.pt;
                        _state = State.Pending;
                        Trace("タイトルバーを検出 → DpiBridge がドラッグを担当 " + why);
                        return (IntPtr)1; // 押下を止める
                    }
                    break;

                case State.Pending:
                    if (msg == Win32.WM_MOUSEMOVE)
                    {
                        if (Math.Abs(info.pt.X - _down.X) >= _dragX || Math.Abs(info.pt.Y - _down.Y) >= _dragY)
                        {
                            _state = State.Dragging;
                            DragStarted?.Invoke(_target, _down.X, _down.Y);
                            DragMoved?.Invoke(info.pt.X, info.pt.Y);
                        }
                    }
                    else if (msg == Win32.WM_LBUTTONUP)
                    {
                        _state = State.Idle;
                        ClickReleased?.Invoke();
                        return (IntPtr)1;
                    }
                    else if (msg is Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN or Win32.WM_LBUTTONDOWN)
                    {
                        _state = State.Idle; // 想定外の操作。止めていた押下はクリックとして返す。
                        ClickReleased?.Invoke();
                    }
                    break;

                case State.Dragging:
                    if (msg == Win32.WM_MOUSEMOVE) DragMoved?.Invoke(info.pt.X, info.pt.Y);
                    else if (msg == Win32.WM_LBUTTONUP)
                    {
                        _state = State.Idle;
                        DragEnded?.Invoke(info.pt.X, info.pt.Y);
                        return (IntPtr)1;
                    }
                    else if (msg == Win32.WM_LBUTTONDOWN)
                    {
                        // 離したイベントを取りこぼしていた。前のドラッグを終わらせて、今回は素通し。
                        _state = State.Idle;
                        DragEnded?.Invoke(info.pt.X, info.pt.Y);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _state = State.Idle;
            Log.Error("マウスフック内のエラー", ex);
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>pt がトップレベルウィンドウのタイトルバー上ならそのウィンドウ。why には判定の理由（ログ用）。</summary>
    private static IntPtr CaptionWindowAt(POINT pt, out string why)
    {
        var child = Win32.WindowFromPoint(pt);
        if (child == IntPtr.Zero) { why = "ウィンドウなし"; return IntPtr.Zero; }
        var root = Win32.GetAncestor(child, Win32.GA_ROOT);
        string name = Describe(root);
        if (root == IntPtr.Zero || !Win32.IsWindowVisible(root)) { why = "トップレベルでない " + name; return IntPtr.Zero; }
        // 最大化中のウィンドウも引き受ける（ドラッグし始めたら DpiBridge が元の大きさに戻して運ぶ）
        Win32.GetWindowThreadProcessId(root, out uint pid);
        if (pid == Environment.ProcessId) { why = "DpiBridge 自身"; return IntPtr.Zero; }

        // まずトップレベル、次にカーソル下の子ウィンドウでヒットテストする
        // （WinUI など、タイトルバーの判定を子ウィンドウが受け持つアプリがあるため）。
        long rootHit = HitTest(root, pt);
        if (rootHit == Win32.HTCAPTION) { why = name; return root; }
        long childHit = child != root ? HitTest(child, pt) : rootHit;
        if (childHit == Win32.HTCAPTION) { why = name + "（子ウィンドウで判定）"; return root; }
        why = $"タイトルバーではない（判定 {rootHit}/{childHit}、-999=応答なし・権限不足） {name}";
        return IntPtr.Zero;
    }

    private static long HitTest(IntPtr hwnd, POINT pt)
    {
        // 相手の DPI 認識に合わせた座標でヒットテストする（DPI 非対応アプリは論理座標で判定するため）。
        var p = pt;
        try { Win32.PhysicalToLogicalPointForPerMonitorDPI(hwnd, ref p); } catch (EntryPointNotFoundException) { }
        var lParam = (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
        // 応答しないアプリでマウスが重くならないよう、30ms で諦める。管理者権限のアプリには届かない（＝横取りしない）。
        return Win32.SendMessageTimeout(hwnd, Win32.WM_NCHITTEST, IntPtr.Zero, lParam, Win32.SMTO_ABORTIFHUNG, 30, out var hit) == IntPtr.Zero
            ? -999 : (short)hit.ToInt64();
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var cls = new System.Text.StringBuilder(128);
        Win32.GetClassName(hwnd, cls, cls.Capacity);
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        return $"[pid {pid} / {cls}]"; // マウス操作を遅くしないよう、重い処理（プロセス名の取得）はしない
    }

    private long _lastTrace;

    /// <summary>判定の理由をログに残す（マウス操作を遅くしないよう、間引いて別スレッドで書く）。</summary>
    private void Trace(string message)
    {
        long now = Environment.TickCount64;
        if (now - _lastTrace < 300) return;
        _lastTrace = now;
        ThreadPool.QueueUserWorkItem(_ => Log.Info("ドラッグ判定: " + message));
        LastDecision = message;
    }

    /// <summary>最後にクリックしたときの判定（診断画面に表示する）。</summary>
    public static volatile string LastDecision = "まだクリックがありません";

    /// <summary>止めていたクリックを再送する（UI スレッドから呼ぶ）。</summary>
    public static void ReplayClick()
    {
        var inputs = new[]
        {
            new Win32.INPUT { type = Win32.INPUT_MOUSE, mi = new Win32.MOUSEINPUT { dwFlags = Win32.MOUSEEVENTF_LEFTDOWN, dwExtraInfo = Marker } },
            new Win32.INPUT { type = Win32.INPUT_MOUSE, mi = new Win32.MOUSEINPUT { dwFlags = Win32.MOUSEEVENTF_LEFTUP, dwExtraInfo = Marker } },
        };
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
