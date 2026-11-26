using DpiBridge.App.Native;
using DpiBridge.App.UI;
using DpiBridge.Core;

namespace DpiBridge.App.Services;

/// <summary>
/// ウィンドウのドラッグ移動を DpiBridge が代わりに行い、モニター間の移動を自然に見せる。
///
/// ・Windows がウィンドウの移動を始めた瞬間（EVENT_SYSTEM_MOVESIZESTART）に、その移動を止めて（WM_CANCELMODE）引き受ける。
///   タイトルバーの作りはアプリごとに違うが、この方法ならどのアプリでも同じように働く。
/// ・引き受けた後は、画面の更新に合わせてカーソル位置とボタンの状態を読み取る（マウスフック不要。Sunshine 経由の操作でも動く）。
/// ・同じモニター内では本物のウィンドウをそのまま動かす（見た目は Windows 標準と同じ）。
/// ・見た目の違うモニターにかかりそうになったら、本物のウィンドウは元のモニター側の画面外に退避させ
///   （DPI が切り替わらないので、途中で大きさが変わったり一部だけ拡大されたりしない）、
///   代わりに各モニターの密度で描いたプレビューを境目でつなげて表示する。
/// ・離したら、移動先で最終的な大きさになるよう 1 回で配置し、プレビューで覆っている間に整えてから消す。
/// UI スレッドで生成・操作すること。
/// </summary>
internal sealed class SmoothDragController : IDisposable
{
    private readonly MonitorService _monitors;
    private readonly Func<AppSettings> _settings;
    private readonly Control _invoker;
    private volatile bool _active;
    private volatile bool _polling;
    private IntPtr _pendingHwnd;           // Windows の移動を止めるのを待っているウィンドウ
    private System.Windows.Forms.Timer? _pendingTimer;

    /// <summary>直近のドラッグの判定（診断画面とログ用）。</summary>
    public static volatile string LastDecision = "まだドラッグがありません";
    private readonly Dictionary<string, GhostWindow> _ghosts = new();

    // ドラッグ中の状態
    private IntPtr _hwnd;
    private MonitorGeometry? _home;        // 本物のウィンドウがいるモニター（退避中も DPI はこのモニターのまま）
    private RectI _liveVisual;              // 本物のウィンドウの見た目の矩形（退避前の最後の位置）
    private FrameMargins _margins;
    private double _widthMm, _heightMm, _grabFx, _grabFy;
    private bool _resizable;
    private bool _ghostMode;
    private RectI _startWindowRect;
    private int _movePending;
    private int _latestX, _latestY;

    public event Action<CorrectionRecord>? Moved;
    private bool _activeLogged;

    public SmoothDragController(MonitorService monitors, Func<AppSettings> settings, Control invoker)
    {
        _monitors = monitors;
        _settings = settings;
        _invoker = invoker;
        _monitors.Changed += UpdateActive;
        UpdateActive();
    }

    private void Post(Action a)
    {
        try { if (!_invoker.IsDisposed) _invoker.BeginInvoke(a); }
        catch (Exception ex) { Log.Error("UI スレッドへの受け渡しに失敗", ex); }
    }

    /// <summary>設定やモニター構成が変わったら、横取りするかどうかを更新する。</summary>
    public void UpdateActive()
    {
        var mons = _monitors.Monitors;
        bool differs = mons.Any(a => mons.Any(b => a.Id != b.Id && DragGeometry.LooksDifferent(a, b)));
        var s = _settings();
        bool active = s.Enabled && s.SmoothDrag && differs;
        if (active != _active || !_activeLogged)
        {
            _activeLogged = true;
            Log.Info($"なめらかドラッグ: {(active ? "有効" : "無効")} (自動補正={s.Enabled}, なめらかドラッグ設定={s.SmoothDrag}, 見た目の違うモニター={differs}, モニター数={mons.Count})");
        }
        _active = active;
    }

    private static void Decide(string message)
    {
        LastDecision = $"{DateTime.Now:HH:mm:ss} {message}";
        Log.Info("ドラッグ判定: " + message);
    }

    private static bool PrimaryButtonDown()
    {
        int vk = Win32.GetSystemMetrics(Win32.SM_SWAPBUTTON) != 0 ? Win32.VK_RBUTTON : Win32.VK_LBUTTON;
        return (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    // ---------------- Windows の移動を引き受ける ----------------

    /// <summary>
    /// Windows がウィンドウの移動・サイズ変更を始めたときに呼ばれる（UI スレッド）。引き受けたら true。
    /// </summary>
    public bool TryTakeOver(IntPtr hwnd)
    {
        string name = WindowInspector.Describe(hwnd);
        if (!_active) { Decide($"引き受けない: なめらかドラッグが無効 {name}"); return false; }
        if (_hwnd != IntPtr.Zero || _pendingHwnd != IntPtr.Zero) return false;
        if ((Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0) { Decide($"引き受けない: Shift 押下中 → Windows 標準 {name}"); return false; }
        if (!PrimaryButtonDown()) { Decide($"引き受けない: マウスのボタンが押されていない（キーボードでの移動など） {name}"); return false; }
        if (Win32.IsZoomed(hwnd)) { Decide($"引き受けない: 最大化中 → Windows 標準 {name}"); return false; }
        if (WindowInspector.WhyNotResizable(hwnd) is { } why && why != "サイズ変更できないウィンドウ")
        { Decide($"引き受けない: {why} → Windows 標準 {name}"); return false; }
        if (!Win32.GetCursorPos(out var cur)) return false;
        if (IsResizeOperation(hwnd, cur)) { Decide($"引き受けない: サイズ変更の操作 {name}"); return false; }

        // Windows の移動ループを止める（マウスのキャプチャを外させる）。止まると EVENT_SYSTEM_MOVESIZEEND が来る。
        if (!Win32.PostMessage(hwnd, Win32.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero))
        { Decide($"引き受けない: 移動を止められない（管理者権限のアプリ？） {name}"); return false; }
        _pendingHwnd = hwnd;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _pendingTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _pendingTimer.Tick += (_, _) =>
        {
            if (_pendingHwnd != hwnd) { StopPendingTimer(); return; }
            // 止まらなければ諦めて Windows 標準の移動のままにする（理由はログへ）
            if (sw.ElapsedMilliseconds >= 300)
            {
                _pendingHwnd = IntPtr.Zero;
                StopPendingTimer();
                Decide($"引き受けられませんでした: Windows の移動が止まらない {name}");
            }
        };
        _pendingTimer.Start();
        Decide($"引き受け: Windows の移動を止めて DpiBridge が移動します {name}");
        return true;
    }

    private void StopPendingTimer()
    {
        _pendingTimer?.Stop();
        _pendingTimer?.Dispose();
        _pendingTimer = null;
    }

    /// <summary>Windows の移動が終わった（＝止まった）ときに呼ばれる（UI スレッド）。</summary>
    public void OnMoveSizeEnded(IntPtr hwnd)
    {
        if (hwnd != _pendingHwnd) return;
        _pendingHwnd = IntPtr.Zero;
        StopPendingTimer();
        if (!PrimaryButtonDown() || !Win32.GetCursorPos(out var p))
        {
            Decide("ボタンが離されていたので移動しません");
            return;
        }
        Start(hwnd, p.X, p.Y);
        if (_hwnd != IntPtr.Zero) StartPolling();
    }

    /// <summary>カーソル位置がサイズ変更の枠の上か（そうでなければ移動とみなす）。</summary>
    private static bool IsResizeOperation(IntPtr hwnd, POINT cur)
    {
        long hit = WindowInspector.HitTest(hwnd, cur);
        if (hit >= Win32.HTLEFT && hit <= Win32.HTBOTTOMRIGHT) return true;
        if (hit != -999) return false;
        // 判定できないときは位置で推測: 見た目の枠の外側や縁の近くならサイズ変更
        var v = WindowInspector.GetVisualRect(hwnd);
        const int edge = 6;
        return cur.X < v.Left + edge || cur.X >= v.Right - edge || cur.Y < v.Top + 3 || cur.Y >= v.Bottom - edge;
    }

    /// <summary>画面の更新ごとにカーソルとボタンを読み、UI スレッドへ渡す。</summary>
    private void StartPolling()
    {
        _polling = true;
        var t = new Thread(() =>
        {
            int lastX = int.MinValue, lastY = int.MinValue;
            try
            {
                while (_polling)
                {
                    if (Win32.DwmFlush() != 0) Thread.Sleep(8); // 画面の更新タイミングに合わせる
                    if (!Win32.GetCursorPos(out var p)) continue;
                    if (!PrimaryButtonDown())
                    {
                        _polling = false;
                        Post(() => End(p.X, p.Y));
                        break;
                    }
                    if (p.X == lastX && p.Y == lastY) continue;
                    (lastX, lastY) = (p.X, p.Y);
                    Interlocked.Exchange(ref _latestX, p.X);
                    Interlocked.Exchange(ref _latestY, p.Y);
                    if (Interlocked.Exchange(ref _movePending, 1) == 0)
                        Post(() => { Interlocked.Exchange(ref _movePending, 0); Move(Volatile.Read(ref _latestX), Volatile.Read(ref _latestY)); });
                }
            }
            catch (Exception ex)
            {
                _polling = false;
                Log.Error("ドラッグ中の読み取りに失敗", ex);
                Post(Abort);
            }
        }) { IsBackground = true, Name = "DpiBridge.DragPoll" };
        t.Start();
    }

    // ---------------- ドラッグ開始 ----------------

    private void Start(IntPtr hwnd, int x, int y)
    {
        try
        {
            if (!Win32.IsWindow(hwnd)) return;

            _hwnd = hwnd;
            _startWindowRect = WindowInspector.GetWindowRect(hwnd);
            _liveVisual = WindowInspector.GetVisualRect(hwnd);
            _margins = FrameMargins.Between(_startWindowRect, _liveVisual);
            _home = SizingEngine.MonitorFromPoint(_monitors.Monitors, _liveVisual.CenterX, _liveVisual.CenterY);
            if (_home is null || _liveVisual.IsEmpty) { Abort(); return; }

            _widthMm = _liveVisual.Width / _home.PxPerMm;
            _heightMm = _liveVisual.Height / _home.PxPerMm;
            _grabFx = Math.Clamp((x - _liveVisual.Left) / (double)_liveVisual.Width, 0, 1);
            _grabFy = Math.Clamp((y - _liveVisual.Top) / (double)_liveVisual.Height, 0, 1);
            _resizable = WindowInspector.IsResizableTopLevel(hwnd);
            _ghostMode = false;
        }
        catch (Exception ex)
        {
            Log.Error("ドラッグ開始の処理に失敗", ex);
            Abort();
        }
    }

    // ---------------- ドラッグ中 ----------------

    private void Move(int x, int y)
    {
        if (_hwnd == IntPtr.Zero || _home is null) return;
        if ((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0) { Cancel(); return; }
        var monitors = _monitors.Monitors;
        var cursorMon = SizingEngine.MonitorFromPoint(monitors, x, y) ?? _home;

        // 本物のウィンドウをそのまま動かした場合の位置（大きさは今のまま）
        var live = RectI.FromXywh(
            (int)Math.Round(x - _grabFx * _liveVisual.Width), (int)Math.Round(y - _grabFy * _liveVisual.Height),
            _liveVisual.Width, _liveVisual.Height);

        bool canMoveLive = cursorMon.Id == _home.Id
            ? !monitors.Any(m => m.Id != _home.Id && DragGeometry.LooksDifferent(m, _home) && DragGeometry.Intersect(live, m.Bounds) is not null)
            : !DragGeometry.LooksDifferent(cursorMon, _home)
              && !monitors.Any(m => m.Id != _home.Id && DragGeometry.LooksDifferent(m, _home) && DragGeometry.Intersect(live, m.Bounds) is not null);

        if (canMoveLive)
        {
            if (_ghostMode) LeaveGhostMode();
            MoveReal(live);
            _liveVisual = live;
            // 見た目が同じモニターへ移った場合は、そこを新しい「いる場所」とする
            if (cursorMon.Id != _home.Id && DragGeometry.Intersect(live, cursorMon.Bounds) is not null
                && SizingEngine.MonitorFromPoint(monitors, live.CenterX, live.CenterY)?.Id == cursorMon.Id)
                _home = cursorMon;
            return;
        }

        if (!_ghostMode) EnterGhostMode();
        var rect = DragGeometry.RectOnCursorMonitor(_widthMm, _heightMm, cursorMon, (x, y), _grabFx, _grabFy);
        ShowGhosts(DragGeometry.Pieces(rect, cursorMon, (x, y), monitors));
    }

    private void MoveReal(RectI visual)
    {
        var r = _margins.Expand(visual);
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);
    }

    private void EnterGhostMode()
    {
        _ghostMode = true;
        // 本物のウィンドウは「いる場所」のモニターに最も近い画面外へ退避（DPI が変わらない）。
        var spot = DragGeometry.ParkingSpot(_margins.Expand(_liveVisual), _home!, _monitors.Monitors);
        if (spot is { } s)
        {
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, s.Left, s.Top, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);
        }
    }

    private void LeaveGhostMode()
    {
        _ghostMode = false;
        HideGhosts();
    }

    private void ShowGhosts(IReadOnlyList<GhostPiece> pieces)
    {
        var used = new HashSet<string>();
        foreach (var p in pieces)
        {
            if (!_ghosts.TryGetValue(p.Monitor.Id, out var g) || g.IsDisposed)
                _ghosts[p.Monitor.Id] = g = new GhostWindow();
            if (!g.Attach(_hwnd)) continue;
            g.Present(p.Dest, SourceRect(g, p));
            used.Add(p.Monitor.Id);
        }
        foreach (var (id, g) in _ghosts)
            if (!used.Contains(id) && !g.IsDisposed) g.Detach();
    }

    /// <summary>元ウィンドウのうち、この片に映す範囲（サムネイルの座標 = ウィンドウ矩形の左上基準）。</summary>
    private RectI SourceRect(GhostWindow g, GhostPiece p)
    {
        var (sw, sh) = g.SourceSize;
        if (sw <= 0 || sh <= 0) (sw, sh) = (_startWindowRect.Width, _startWindowRect.Height);
        // 見た目の部分（影などの見えない余白を除く）
        double vl = _margins.Left, vt = _margins.Top;
        double vw = Math.Max(1, sw - _margins.Left - _margins.Right), vh = Math.Max(1, sh - _margins.Top - _margins.Bottom);
        return new RectI(
            (int)Math.Round(vl + p.SrcLeft * vw), (int)Math.Round(vt + p.SrcTop * vh),
            (int)Math.Round(vl + p.SrcRight * vw), (int)Math.Round(vt + p.SrcBottom * vh));
    }

    private void HideGhosts()
    {
        foreach (var g in _ghosts.Values) if (!g.IsDisposed) g.Detach();
    }

    // ---------------- ドロップ ----------------

    private void End(int x, int y)
    {
        if (_hwnd == IntPtr.Zero || _home is null) return;
        Move(x, y);
        if (_hwnd == IntPtr.Zero) return; // Esc で取り消された
        var monitors = _monitors.Monitors;
        var dest = SizingEngine.MonitorFromPoint(monitors, x, y) ?? _home;
        var snap = DragGeometry.DetectSnap((x, y), dest, monitors);

        if (!_ghostMode && snap == SnapZone.None)
        {
            Finish(); // 同じ見た目のモニター内での普通の移動。もう本物が正しい位置にいる。
            return;
        }

        var hwnd = _hwnd;
        var home = _home;
        bool resizable = _resizable;
        var target = snap == SnapZone.None
            ? DragGeometry.FitInto(DragGeometry.RectOnCursorMonitor(_widthMm, _heightMm, dest, (x, y), _grabFx, _grabFy), dest.WorkArea)
            : DragGeometry.SnapRect(snap, dest.WorkArea);
        if (!_resizable && snap == SnapZone.None)
        {
            // サイズを変えられないウィンドウは、位置だけ合わせて大きさはアプリに任せる
            target = DragGeometry.FitInto(RectI.FromXywh(target.Left, target.Top,
                (int)Math.Round(_liveVisual.Width * dest.Dpi / (double)home.Dpi), (int)Math.Round(_liveVisual.Height * dest.Dpi / (double)home.Dpi)), dest.WorkArea);
        }

        // プレビューで最終位置を覆ってから、本物を 1 回で配置する（途中の大きさの変化はプレビューの下に隠れる）。
        if (_ghostMode) ShowGhosts(DragGeometry.Pieces(target, dest, (x, y), monitors));
        var pre = DragGeometry.PreCompensate(target, home.Dpi, dest.Dpi);
        var preWindow = _margins.Expand(pre);
        Win32.SetWindowPos(hwnd, IntPtr.Zero, preWindow.Left, preWindow.Top, preWindow.Width, preWindow.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS | (_resizable || snap != SnapZone.None ? 0 : Win32.SWP_NOSIZE));

        string exe = Path.GetFileName(WindowInspector.GetExePath(GetPid(hwnd)) ?? "?").ToLowerInvariant();
        bool wasGhost = _ghostMode;
        Finish(keepGhosts: true);

        // アプリが DPI の変更に合わせて大きさを変え終わるのを見届けてからプレビューを消す。
        // 90ms 経ってもずれていれば直す。最終位置に収まった時点ですぐ消すので、待ち時間は最小限。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool corrected = false, maximized = false;
        var timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += (_, _) =>
        {
            try
            {
                long ms = sw.ElapsedMilliseconds;
                bool done;
                if (snap == SnapZone.Maximize)
                {
                    if (!maximized && ms >= 30) { Win32.ShowWindow(hwnd, Win32.SW_MAXIMIZE); maximized = true; }
                    done = maximized && ms >= 120;
                }
                else if (!resizable && snap == SnapZone.None) done = ms >= 90;
                else
                {
                    var now = WindowInspector.GetVisualRect(hwnd);
                    bool ok = Math.Abs(now.Width - target.Width) <= 2 && Math.Abs(now.Height - target.Height) <= 2
                              && Math.Abs(now.Left - target.Left) <= 2 && Math.Abs(now.Top - target.Top) <= 2;
                    if (!ok && !corrected && ms >= 90)
                    {
                        WindowMover.MoveTo(hwnd, target, animate: false, durationMs: 0);
                        corrected = true;
                    }
                    done = (ok && ms >= 30) || ms >= 400;
                }
                if (!done) return;
                timer.Stop();
                timer.Dispose();
                if (_hwnd == IntPtr.Zero) HideGhosts(); // 次のドラッグが始まっていなければ
                var final = WindowInspector.GetVisualRect(hwnd);
                Log.Info($"なめらか移動 {exe}: {home.FriendlyName}→{dest.FriendlyName} target={target} final={final} snap={snap} corrected={corrected} {ms}ms");
                Moved?.Invoke(new CorrectionRecord(DateTime.Now, exe, home.FriendlyName, dest.FriendlyName, _startWindowRect, final, true,
                    snap != SnapZone.None ? $"スナップ: {snap}" : wasGhost ? "なめらか移動（プレビューで運んで配置）" : "移動"));
            }
            catch (Exception ex)
            {
                timer.Stop();
                timer.Dispose();
                HideGhosts();
                Log.Error("ドロップ後の処理に失敗", ex);
            }
        };
        timer.Start();
    }

    private static uint GetPid(IntPtr hwnd) { Win32.GetWindowThreadProcessId(hwnd, out uint pid); return pid; }

    private void Cancel()
    {
        // 元の位置・大きさに戻す
        var r = _startWindowRect;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        Decide("Esc で取り消しました");
        Finish();
    }

    private void Abort()
    {
        Finish();
    }

    private void Finish(bool keepGhosts = false)
    {
        _polling = false;
        if (!keepGhosts) HideGhosts();
        _hwnd = IntPtr.Zero;
        _home = null;
        _ghostMode = false;
    }

    public void Dispose()
    {
        _monitors.Changed -= UpdateActive;
        _polling = false;
        StopPendingTimer();
        foreach (var g in _ghosts.Values) g.Dispose();
    }
}
