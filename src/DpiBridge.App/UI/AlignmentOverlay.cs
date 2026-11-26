using DpiBridge.Core;

namespace DpiBridge.App.UI;

/// <summary>
/// 左右の位置合わせ。全モニターに全画面のオーバーレイを出し、基準のモニター（メイン）の中央に縦線を引く。
/// ほかのモニターには「物理的に同じ位置」と計算した場所に縦線を引くので、その線が実際の机の上で
/// メインの線の真下（または真横）に来るよう矢印キーで動かしてもらう。
/// 結果は各モニターの AlignOffsetMm として保存し、ドラッグ中の複製を物理的な位置でつなぐのに使う。
/// </summary>
internal sealed class AlignmentSession
{
    private readonly List<AlignmentOverlay> _overlays = new();
    private readonly Action<IReadOnlyDictionary<string, double>> _onSave;
    private bool _closing;

    public MonitorGeometry Reference { get; }
    public IReadOnlyList<MonitorGeometry> Monitors { get; }
    public Dictionary<string, double> Offsets { get; }

    public AlignmentSession(IReadOnlyList<MonitorGeometry> monitors, AppSettings settings, Action<IReadOnlyDictionary<string, double>> onSave)
    {
        Monitors = monitors;
        Reference = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        Offsets = monitors.ToDictionary(m => m.Id, m => settings.AlignOffsetFor(m.Id));
        _onSave = onSave;
        foreach (var m in monitors) _overlays.Add(new AlignmentOverlay(this, m));
    }

    public void Show()
    {
        foreach (var o in _overlays) o.Show();
        _overlays.FirstOrDefault(o => o.Monitor.Id != Reference.Id)?.Activate();
    }

    /// <summary>このモニター上で、基準の線と物理的に同じ位置の x（または y）。隣り合っていなければ中央。</summary>
    public (bool Vertical, double Pos) LineOn(MonitorGeometry m)
    {
        var r = Reference;
        if (m.Id == r.Id) return (true, r.Bounds.CenterX);
        var map = DragGeometry.PhysicalMap(r, m, Offsets[r.Id], Offsets[m.Id]);
        if (map is null) return (true, m.Bounds.CenterX);
        bool sideBySide = r.Bounds.Right == m.Bounds.Left || r.Bounds.Left == m.Bounds.Right;
        if (sideBySide)
        {
            // 横に並んでいるときは、基準の高さ中央の横線を写す
            var (_, y) = map(r.Bounds.CenterX, r.Bounds.CenterY);
            return (false, y);
        }
        var (x, _) = map(r.Bounds.CenterX, r.Bounds.CenterY);
        return (true, x);
    }

    public void Nudge(MonitorGeometry m, double mm)
    {
        if (m.Id == Reference.Id) return;
        Offsets[m.Id] = Math.Round(Offsets[m.Id] + mm, 1);
        foreach (var o in _overlays) o.Invalidate();
    }

    public void Save()
    {
        if (_closing) return;
        var result = new Dictionary<string, double>(Offsets);
        CloseAll();
        _onSave(result);
    }

    public void CloseAll()
    {
        if (_closing) return;
        _closing = true;
        foreach (var o in _overlays.ToList()) o.Close();
    }
}

internal sealed class AlignmentOverlay : Form
{
    private readonly AlignmentSession _session;
    private readonly float _s;
    public MonitorGeometry Monitor { get; }

    public AlignmentOverlay(AlignmentSession session, MonitorGeometry monitor)
    {
        _session = session;
        Monitor = monitor;
        _s = monitor.Dpi / 96f;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(28, 28, 32);
        Bounds = new Rectangle(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height);
        Text = "DpiBridge 位置合わせ";
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        Bounds = new Rectangle(Monitor.Bounds.Left, Monitor.Bounds.Top, Monitor.Bounds.Width, Monitor.Bounds.Height);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = e.Shift ? 0.2 : e.Control ? 5 : 1;
        var (vertical, _) = _session.LineOn(Monitor);
        switch (e.KeyCode)
        {
            // 線を右（下）へ動かす ＝ このモニターが実際には左（上）にある、という補正
            case Keys.Right when vertical: _session.Nudge(Monitor, -step); break;
            case Keys.Left when vertical: _session.Nudge(Monitor, +step); break;
            case Keys.Down when !vertical: _session.Nudge(Monitor, -step); break;
            case Keys.Up when !vertical: _session.Nudge(Monitor, +step); break;
            case Keys.Enter: _session.Save(); break;
            case Keys.Escape: _session.CloseAll(); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    protected override void OnMouseClick(MouseEventArgs e) => Activate();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var font = new Font("Yu Gothic UI", 15 * _s, GraphicsUnit.Pixel);
        using var big = new Font("Yu Gothic UI", 20 * _s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fg = new SolidBrush(Color.WhiteSmoke);
        using var dim = new SolidBrush(Color.FromArgb(170, 170, 180));
        bool isRef = Monitor.Id == _session.Reference.Id;

        var (vertical, pos) = _session.LineOn(Monitor);
        double ppmm = Monitor.PxPerMm;
        using var line = new Pen(isRef ? Color.Gold : Color.DeepSkyBlue, Math.Max(2, 3 * _s));
        using var tick = new Pen(isRef ? Color.Gold : Color.DeepSkyBlue, Math.Max(1, 1.5f * _s));
        if (vertical)
        {
            float x = (float)(pos - Monitor.Bounds.Left);
            g.DrawLine(line, x, 0, x, ClientSize.Height);
            // 境目付近に 10mm ごとの目盛り（物理的な長さが両方の画面で同じになる）
            for (int mm = -100; mm <= 100; mm += 10)
            {
                float tx = x + (float)(mm * ppmm);
                g.DrawLine(tick, tx, 0, tx, (mm % 50 == 0 ? 40 : 20) * _s);
                g.DrawLine(tick, tx, ClientSize.Height, tx, ClientSize.Height - (mm % 50 == 0 ? 40 : 20) * _s);
            }
        }
        else
        {
            float y = (float)(pos - Monitor.Bounds.Top);
            g.DrawLine(line, 0, y, ClientSize.Width, y);
            for (int mm = -100; mm <= 100; mm += 10)
            {
                float ty = y + (float)(mm * ppmm);
                g.DrawLine(tick, 0, ty, (mm % 50 == 0 ? 40 : 20) * _s, ty);
                g.DrawLine(tick, ClientSize.Width, ty, ClientSize.Width - (mm % 50 == 0 ? 40 : 20) * _s, ty);
            }
        }

        float left = 40 * _s, top = ClientSize.Height / 3f;
        g.DrawString(isRef ? $"位置合わせ — 基準: {Monitor.FriendlyName}" : $"位置合わせ — {Monitor.FriendlyName}", big, fg, left, top);
        string help = isRef
            ? "この黄色い線が基準です。ほかのモニターの青い線を、机の上で見てこの線の延長上に来るよう合わせてください。"
            : (vertical
                ? "青い線が、上（または下）のモニターの黄色い線のちょうど延長上に来るよう ← → で動かしてください。"
                : "青い線が、横のモニターの黄色い線のちょうど延長上に来るよう ↑ ↓ で動かしてください。") +
              "\nShift: 細かく（0.2mm）　Ctrl: 大きく（5mm）　Enter: 保存　Esc: キャンセル" +
              "\n目盛りは 10mm 間隔です（両方の画面で同じ長さに見えていれば、大きさのキャリブレーションも合っています）。";
        g.DrawString(help, font, dim, new RectangleF(left, top + 40 * _s, ClientSize.Width - 80 * _s, 140 * _s));
        if (!isRef)
            g.DrawString($"位置の補正: {_session.Offsets[Monitor.Id]:+0.0;-0.0;0} mm", font, fg, left, top + 190 * _s);
    }
}
