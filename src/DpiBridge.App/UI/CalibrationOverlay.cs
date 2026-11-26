using DpiBridge.Core;

namespace DpiBridge.App.UI;

/// <summary>
/// 見た目キャリブレーション。全モニターに同時に全画面のオーバーレイを出し、
/// 実物のカード（85.60×53.98mm）や定規に合わせて枠の大きさを調整してもらうことで、
/// 「そのモニターで実際に目に見える 1mm あたりの物理ピクセル数」を実測する。
/// Moonlight による引き伸ばしや Windows の拡大率の違いもすべて含めて測れる。
/// </summary>
internal sealed class CalibrationSession
{
    public const double CardWidthMm = 85.60;
    public const double CardHeightMm = 53.98;

    private readonly List<CalibrationOverlay> _overlays = new();
    private readonly Action<IReadOnlyDictionary<string, double>> _onSave;
    private bool _closing;

    public CalibrationSession(IEnumerable<MonitorGeometry> monitors, Action<IReadOnlyDictionary<string, double>> onSave)
    {
        _onSave = onSave;
        foreach (var m in monitors) _overlays.Add(new CalibrationOverlay(this, m));
    }

    public void Show()
    {
        foreach (var o in _overlays) o.Show();
        _overlays.FirstOrDefault()?.Activate();
    }

    public void Save()
    {
        if (_closing) return;
        var result = _overlays.ToDictionary(o => o.Monitor.Id, o => o.PxPerMm);
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

internal sealed class CalibrationOverlay : Form
{
    private readonly CalibrationSession _session;
    private readonly float _uiScale;
    private readonly List<(RectangleF Rect, string Label, Action Action)> _buttons = new();
    private readonly double _initial;

    public MonitorGeometry Monitor { get; }
    public double PxPerMm { get; private set; }

    public CalibrationOverlay(CalibrationSession session, MonitorGeometry monitor)
    {
        _session = session;
        Monitor = monitor;
        PxPerMm = _initial = monitor.PxPerMm;
        _uiScale = monitor.Dpi / 96f;

        // このプロセスは Per-Monitor v2 なので座標・サイズは物理ピクセル。自動スケーリングはしない。
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(32, 32, 36);
        Bounds = new Rectangle(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height);
        Text = "DpiBridge キャリブレーション";
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Windows の推奨サイズに変えられないよう、常にモニター全体を覆う。
        e.Cancel = true;
        Bounds = new Rectangle(Monitor.Bounds.Left, Monitor.Bounds.Top, Monitor.Bounds.Width, Monitor.Bounds.Height);
    }

    private void Adjust(double factor)
    {
        PxPerMm = Math.Clamp(PxPerMm * factor, 0.5, 60);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = e.Shift ? 0.001 : e.Control ? 0.02 : 0.005;
        switch (e.KeyCode)
        {
            case Keys.Right: case Keys.Up: case Keys.Add: case Keys.Oemplus: Adjust(1 + step); break;
            case Keys.Left: case Keys.Down: case Keys.Subtract: case Keys.OemMinus: Adjust(1 / (1 + step)); break;
            case Keys.R: PxPerMm = _initial; Invalidate(); break;
            case Keys.Enter: _session.Save(); break;
            case Keys.Escape: _session.CloseAll(); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        double step = ModifierKeys.HasFlag(Keys.Shift) ? 0.001 : ModifierKeys.HasFlag(Keys.Control) ? 0.02 : 0.005;
        Adjust(e.Delta > 0 ? 1 + step : 1 / (1 + step));
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        foreach (var b in _buttons)
            if (b.Rect.Contains(e.Location)) { b.Action(); return; }
        Activate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = _uiScale;
        using var font = new Font("Yu Gothic UI", 15 * s, GraphicsUnit.Pixel);
        using var big = new Font("Yu Gothic UI", 20 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fg = new SolidBrush(Color.WhiteSmoke);
        using var dim = new SolidBrush(Color.FromArgb(170, 170, 180));

        float cx = ClientSize.Width / 2f, cy = ClientSize.Height / 2f;

        // カード枠（実寸 85.60 x 53.98mm）
        float cw = (float)(CalibrationSession.CardWidthMm * PxPerMm);
        float ch = (float)(CalibrationSession.CardHeightMm * PxPerMm);
        var card = new RectangleF(cx - cw / 2, cy - ch / 2 - 20 * s, cw, ch);
        using (var cardFill = new SolidBrush(Color.FromArgb(60, 90, 160)))
            g.FillRectangle(cardFill, card);
        using (var pen = new Pen(Color.White, Math.Max(1, 2 * s)))
            g.DrawRectangle(pen, card.X, card.Y, card.Width, card.Height);
        var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("85.6 × 54 mm\n（ICカード・クレジットカードの大きさ）", font, fg, card, center);

        // 10cm の目盛り
        float rulerY = card.Bottom + 40 * s;
        float len = (float)(100 * PxPerMm);
        float rx = cx - len / 2;
        using (var pen = new Pen(Color.Gold, Math.Max(1, 2 * s)))
        {
            g.DrawLine(pen, rx, rulerY, rx + len, rulerY);
            for (int mm = 0; mm <= 100; mm++)
            {
                float x = rx + (float)(mm * PxPerMm);
                float h = mm % 10 == 0 ? 14 * s : mm % 5 == 0 ? 9 * s : 5 * s;
                g.DrawLine(pen, x, rulerY, x, rulerY + h);
                if (mm % 10 == 0)
                    g.DrawString((mm / 10).ToString(), font, fg, x - 5 * s, rulerY + 15 * s);
            }
        }
        g.DrawString("10 cm", font, dim, rx + len + 10 * s, rulerY - 10 * s);

        // 説明
        float top = 40 * s;
        g.DrawString($"見た目キャリブレーション — {Monitor.FriendlyName}", big, fg, 40 * s, top);
        string help =
            "実物のカードを画面に当てて青い枠がぴったり同じ大きさになるよう調整してください（定規なら黄色い線を 10cm に）。\n" +
            "すべてのモニターで同じことをすると、モニター間で見た目の大きさが揃います。\n" +
            "マウスホイール / ← → キー: 調整（Shift: 微調整、Ctrl: 粗調整）　R: 元に戻す　Enter: 全モニター保存　Esc: キャンセル";
        g.DrawString(help, font, dim, new RectangleF(40 * s, top + 36 * s, ClientSize.Width - 80 * s, 100 * s));

        string info = $"現在 {PxPerMm:0.000} px/mm（解像度 {Monitor.Bounds.Width}x{Monitor.Bounds.Height}、Windows 拡大率 {Monitor.ScalePercent:0}%、" +
                      $"開始時 {_initial:0.000}{(Monitor.HasRealScale ? "" : "＝推定値")}）";
        g.DrawString(info, font, fg, 40 * s, ClientSize.Height - 150 * s);

        // ボタン
        _buttons.Clear();
        float bw = 130 * s, bh = 44 * s, gap = 14 * s;
        float by = ClientSize.Height - 100 * s;
        float bx = 40 * s;
        void Button(string label, Action action)
        {
            var r = new RectangleF(bx, by, bw, bh);
            using var b = new SolidBrush(Color.FromArgb(70, 70, 80));
            g.FillRectangle(b, r);
            using var p = new Pen(Color.FromArgb(140, 140, 150));
            g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            g.DrawString(label, font, fg, r, center);
            _buttons.Add((r, label, action));
            bx += bw + gap;
        }
        Button("小さく −", () => Adjust(1 / 1.005));
        Button("大きく ＋", () => Adjust(1.005));
        Button("元に戻す", () => { PxPerMm = _initial; Invalidate(); });
        Button("保存 (Enter)", _session.Save);
        Button("キャンセル (Esc)", _session.CloseAll);
    }
}
