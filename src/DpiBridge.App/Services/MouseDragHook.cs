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
            return;
        }
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
                    if (msg == Win32.WM_LBUTTONDOWN && Active && (info.flags & Win32.LLMHF_INJECTED) == 0
                        && (Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) == 0 // Shift を押しながらなら Windows 標準のドラッグ
                        && CaptionWindowAt(info.pt) is var hwnd && hwnd != IntPtr.Zero)
                    {
                        _target = hwnd;
                        _down = info.pt;
                        _state = State.Pending;
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

    /// <summary>pt がトップレベルウィンドウのタイトルバー上ならそのウィンドウ。</summary>
    private static IntPtr CaptionWindowAt(POINT pt)
    {
        var child = Win32.WindowFromPoint(pt);
        if (child == IntPtr.Zero) return IntPtr.Zero;
        var root = Win32.GetAncestor(child, Win32.GA_ROOT);
        if (root == IntPtr.Zero || Win32.IsZoomed(root) || !Win32.IsWindowVisible(root)) return IntPtr.Zero; // 最大化中は Windows に任せる
        Win32.GetWindowThreadProcessId(root, out uint pid);
        if (pid == Environment.ProcessId) return IntPtr.Zero;

        // 相手の DPI 認識に合わせた座標でヒットテストする（DPI 非対応アプリは論理座標で判定するため）。
        var p = pt;
        try { Win32.PhysicalToLogicalPointForPerMonitorDPI(root, ref p); } catch (EntryPointNotFoundException) { }
        var lParam = (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
        // 応答しないアプリでマウスが重くならないよう、30ms で諦める。管理者権限のアプリには届かない（＝横取りしない）。
        if (Win32.SendMessageTimeout(root, Win32.WM_NCHITTEST, IntPtr.Zero, lParam, Win32.SMTO_ABORTIFHUNG, 30, out var hit) == IntPtr.Zero)
            return IntPtr.Zero;
        return hit.ToInt64() == Win32.HTCAPTION ? root : IntPtr.Zero;
    }

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
