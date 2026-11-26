using System.Diagnostics;
using System.Globalization;
using DpiBridge.App.Native;
using DpiBridge.App.Services;
using DpiBridge.Core;

namespace DpiBridge.App.UI;

/// <summary>診断と設定の画面（Phase 0）。モニター情報・カーソル下ウィンドウの DPI 状態・補正履歴を表示する。</summary>
internal sealed class DiagnosticsForm : Form
{
    private readonly TrayApp _app;
    private readonly MonitorService _monitors;
    private readonly DataGridView _grid = new();
    private readonly TextBox _findings = MakeReadOnlyText();
    private readonly TextBox _windowInfo = MakeReadOnlyText();
    private readonly ListView _history = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };
    private readonly CheckBox _enabled = new() { Text = "自動補正を有効にする", AutoSize = true };
    private readonly CheckBox _animate = new() { Text = "滑らかに補正（アニメーション）", AutoSize = true };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 400 };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private IntPtr _lastHover;

    /// <summary>100% 換算での希望サイズ。実際のサイズはモニターの拡大率と作業領域に合わせて決める。</summary>
    private static readonly Size PreferredLogicalSize = new(1100, 720);
    private static readonly int[] HistoryColumnWeights = [8, 14, 22, 14, 14, 28];

    private static readonly (SizingMode Mode, string Label)[] Modes =
    [
        (SizingMode.PhysicalSize, "見た目の実寸(mm)を維持（推奨）"),
        (SizingMode.WorkAreaRatio, "画面に対する比率を維持"),
        (SizingMode.None, "補正しない（Windows 標準）"),
    ];

    public DiagnosticsForm(TrayApp app, MonitorService monitors, IEnumerable<CorrectionRecord> history)
    {
        _app = app;
        _monitors = monitors;
        SuspendLayout();
        Text = "DpiBridge — 診断と設定";
        // 100% (96 DPI) で設計し、表示するモニターの拡大率に合わせて WinForms に拡大させる。
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(520, 360);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildMonitorTab());
        tabs.TabPages.Add(BuildWindowTab());
        tabs.TabPages.Add(BuildHistoryTab());
        Controls.Add(tabs);
        Controls.Add(BuildSettingsBar());
        ResumeLayout(false);
        PerformLayout();

        foreach (var rec in history.Reverse()) AddHistory(rec);
        LoadSettingsToUi();
        FillMonitors();
        _monitors.Changed += OnMonitorsChanged;
        _hoverTimer.Tick += (_, _) => UpdateHover();
        _hoverTimer.Start();
        FormClosed += (_, _) =>
        {
            _hoverTimer.Dispose();
            _monitors.Changed -= OnMonitorsChanged;
        };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // マウスのあるモニターの中央に、そのモニターに収まる大きさで開く。
        FitToScreen(Screen.FromPoint(Cursor.Position), center: true);
        _split.SplitterDistance = Math.Max(_split.Panel1MinSize, _split.Height * 45 / 100);
        ResizeHistoryColumns();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // 別の拡大率のモニターへ移ったら、WinForms の再スケール後にそのモニターからはみ出さないよう収め直す。
        BeginInvoke(() => FitToScreen(Screen.FromControl(this), center: false));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ResizeHistoryColumns();
    }

    /// <summary>ウィンドウを指定モニターの作業領域に収める。</summary>
    private void FitToScreen(Screen screen, bool center)
    {
        if (WindowState != FormWindowState.Normal) return;
        var wa = screen.WorkingArea;
        var preferred = LogicalToDeviceUnits(PreferredLogicalSize);
        int w = Math.Min(center ? preferred.Width : Width, wa.Width * 92 / 100);
        int h = Math.Min(center ? preferred.Height : Height, wa.Height * 92 / 100);
        int x = center ? wa.Left + (wa.Width - w) / 2 : Math.Clamp(Left, wa.Left, wa.Right - w);
        int y = center ? wa.Top + (wa.Height - h) / 2 : Math.Clamp(Top, wa.Top, wa.Bottom - h);
        Bounds = new Rectangle(x, y, w, h);
    }

    private void ResizeHistoryColumns()
    {
        if (_history.Columns.Count != HistoryColumnWeights.Length || _history.ClientSize.Width <= 0) return;
        int total = HistoryColumnWeights.Sum();
        int width = _history.ClientSize.Width - 4;
        for (int i = 0; i < HistoryColumnWeights.Length; i++)
            _history.Columns[i].Width = width * HistoryColumnWeights[i] / total;
    }

    private static TextBox MakeReadOnlyText() => new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = SystemColors.Window,
    };

    // ---------------- モニタータブ ----------------

    private TabPage BuildMonitorTab()
    {
        var page = new TabPage("モニター");
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.BackgroundColor = SystemColors.Window;
        // 列は画面幅に合わせて伸縮し、文字は折り返す。見出し・行の高さは文字の大きさに合わせて自動で決める
        // （固定の高さだと拡大率が高いモニターで文字の上下が切れるため）。
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;

        void Col(string name, string header, float weight, bool editable = false)
        {
            var c = new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, ReadOnly = !editable, FillWeight = weight, MinimumWidth = 40,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            if (editable) c.DefaultCellStyle.BackColor = Color.LightYellow;
            _grid.Columns.Add(c);
        }
        Col("name", "モニター", 16);
        Col("kind", "種別", 7);
        Col("res", "解像度", 10);
        Col("scale", "拡大率", 7);
        Col("eff", "実効解像度", 10);
        Col("edid", "EDID 実寸", 14);
        Col("src", "見た目の大きさの根拠", 14);
        Col("diag", "対角インチ（入力・任意）", 9, editable: true);
        Col("native", "本来の解像度（入力・任意 例 2880x1920）", 11, editable: true);
        Col("ppmm", "px/mm", 7);
        Col("look", "同じウィンドウの見た目（メイン比）", 9);

        var help = new WrapLabel
        {
            Dock = DockStyle.Top, Padding = new Padding(6),
            Text = "まず下の「見た目キャリブレーション」を実行してください（各モニターに出る枠を実物のカードか定規に合わせるだけ）。" +
                   "仮想ディスプレイ（Surface 側）は実際の見た目の大きさを Windows から取得できないため、これが必要です。" +
                   "Moonlight の引き伸ばしや Windows の拡大率の違いも含めて実測するので、拡大率が同じでも見た目が揃うように補正されます。" +
                   "黄色の欄（対角インチ・本来の解像度）は任意です。",
        };

        _split.Panel1.Controls.Add(_grid);
        var findBox = new GroupBox { Text = "診断結果", Dock = DockStyle.Fill };
        findBox.Controls.Add(_findings);
        _split.Panel2.Controls.Add(findBox);

        page.Controls.Add(_split);
        page.Controls.Add(help);
        return page;
    }

    private void OnMonitorsChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(FillMonitors); else FillMonitors();
    }

    private void FillMonitors()
    {
        var settings = _app.Settings;
        var mons = _monitors.Monitors;
        var edidSizes = _monitors.EdidSizes;
        var primary = mons.FirstOrDefault(m => m.IsPrimary) ?? mons.FirstOrDefault();

        _grid.Rows.Clear();
        foreach (var m in mons)
        {
            settings.Monitors.TryGetValue(m.Id, out var o);
            string edid = edidSizes.TryGetValue(m.Id, out var e)
                ? $"{e.W:0}x{e.H:0}mm ({PhysicalSize.DiagonalInches(e.W, e.H):0.0}\"){(m.IsVirtual ? " ※仮想のため不使用" : "")}"
                : "なし";
            string source = m.Source switch
            {
                ScaleSource.Calibrated => "キャリブレーション済み",
                ScaleSource.Manual => "対角インチ入力",
                ScaleSource.Edid => "EDID（キャリブレーション推奨）",
                _ => "不明 → 拡大率から推定（要キャリブレーション）",
            };
            int idx = _grid.Rows.Add(
                m.FriendlyName + (m.IsPrimary ? "（メイン）" : ""),
                m.IsVirtual ? "仮想/間接" : "物理",
                $"{m.Bounds.Width}x{m.Bounds.Height}",
                $"{m.ScalePercent:0}%",
                $"{m.Bounds.Width * 96 / m.Dpi}x{m.Bounds.Height * 96 / m.Dpi}",
                edid,
                source,
                o?.DiagonalInches?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                o is { NativeWidth: > 0, NativeHeight: > 0 } ? $"{o.NativeWidth}x{o.NativeHeight}" : "",
                $"{m.PxPerMm:0.00}{(m.HasRealScale ? "" : "（推定）")}",
                primary is null ? "" : $"{m.DipPerMm / primary.DipPerMm * 100:0}%");
            _grid.Rows[idx].Tag = m.Id;
            if (!m.HasRealScale) _grid.Rows[idx].Cells["src"].Style.ForeColor = Color.Firebrick;
        }

        var findings = DiagnosticsAnalyzer.AnalyzeMonitors(mons, settings);
        _findings.Text = findings.Count == 0
            ? "問題は見つかりませんでした。"
            : string.Join(Environment.NewLine + Environment.NewLine, findings.Select(f => $"[{SeverityLabel(f.Severity)}] {f.Message}"));
    }

    private bool ReadGridIntoSettings()
    {
        var settings = _app.Settings;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not string id) continue;
            string diagText = (row.Cells["diag"].Value as string ?? "").Trim();
            string nativeText = (row.Cells["native"].Value as string ?? "").Trim().ToLowerInvariant().Replace('×', 'x');

            double? diag = null;
            if (diagText.Length > 0)
            {
                if (!double.TryParse(diagText, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0 || d > 200)
                {
                    MessageBox.Show(this, $"対角インチの値が不正です: {diagText}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                diag = d;
            }

            int? nw = null, nh = null;
            if (nativeText.Length > 0)
            {
                var parts = nativeText.Split('x');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) || w <= 0 || h <= 0)
                {
                    MessageBox.Show(this, $"解像度は 2880x1920 の形式で入力してください: {nativeText}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                (nw, nh) = (w, h);
            }

            // キャリブレーション値を消さないよう、既存の設定は項目ごとに更新する。
            if (!settings.Monitors.TryGetValue(id, out var o)) settings.Monitors[id] = o = new MonitorOverride();
            o.DiagonalInches = diag;
            o.NativeWidth = nw;
            o.NativeHeight = nh;
            if (o.DiagonalInches is null && o.NativeWidth is null && o.CalibratedPxPerMm is null) settings.Monitors.Remove(id);
        }
        return true;
    }

    // ---------------- ウィンドウ診断タブ ----------------

    private TabPage BuildWindowTab()
    {
        var page = new TabPage("ウィンドウ診断");
        var help = new WrapLabel
        {
            Dock = DockStyle.Top, Padding = new Padding(6),
            Text = "調べたいウィンドウ（例: タスクマネージャー）の上にマウスを置くと、そのアプリの DPI 対応状況とぼやけの原因を表示します。",
        };
        page.Controls.Add(_windowInfo);
        page.Controls.Add(help);
        return page;
    }

    private void UpdateHover()
    {
        var h = WindowInspector.RootWindowUnderCursor();
        if (h == IntPtr.Zero || h == _lastHover) return;
        Win32.GetWindowThreadProcessId(h, out uint pid);
        if (pid == Environment.ProcessId) return; // 自分自身の上では更新しない
        _lastHover = h;

        var w = WindowInspector.Inspect(h);
        var mon = SizingEngine.MonitorFromPoint(_monitors.Monitors, w.VisualRect.CenterX, w.VisualRect.CenterY);
        uint monDpi = mon?.Dpi ?? w.Dpi;
        bool elevated = w.IsElevated ?? (w.ExePath is null); // 情報が取れない＝昇格プロセスの可能性大
        var findings = DiagnosticsAnalyzer.AnalyzeWindow(w.Awareness, monDpi, Win32.GetDpiForSystem(), elevated, WindowInspector.SelfElevated);

        string awarenessText = w.Awareness switch
        {
            DpiAwareness.PerMonitorV2 => "Per-Monitor v2（モニターごとにくっきり描画）",
            DpiAwareness.PerMonitor => "Per-Monitor v1",
            DpiAwareness.SystemAware => "System（システム DPI 固定。他の拡大率のモニターではぼやける）",
            DpiAwareness.Unaware => "非対応（100% 以外ではぼやける）",
            DpiAwareness.UnawareGdiScaled => "非対応（GDI スケーリング）",
            _ => "不明",
        };
        string mode = _app.Settings.ModeFor(w.ExeName).ToString();

        _windowInfo.Text = string.Join(Environment.NewLine,
        [
            $"タイトル        : {w.Title}",
            $"クラス          : {w.ClassName}",
            $"実行ファイル    : {w.ExePath ?? "（取得不可）"}",
            $"PID             : {w.ProcessId}",
            $"管理者権限      : {(w.IsElevated is bool b ? (b ? "はい" : "いいえ") : "不明（昇格している可能性）")}",
            $"DPI 対応        : {awarenessText}",
            $"ウィンドウ DPI  : {w.Dpi} ({w.Dpi * 100 / 96}%)",
            $"モニター        : {mon?.FriendlyName} ({monDpi * 100 / 96}%)",
            $"見た目の矩形    : {w.VisualRect}",
            $"補正対象        : {(WindowInspector.IsResizableTopLevel(h) ? "はい" : "いいえ（最大化・スナップ・サイズ固定など）")} / モード {mode}",
            $"DpiBridge 権限  : {(WindowInspector.SelfElevated ? "管理者" : "通常")}",
            $"直近のドラッグ判定: {MouseDragHook.LastDecision}",
            "",
            "■ 診断",
            .. findings.Select(f => $"[{SeverityLabel(f.Severity)}] {f.Message}"),
        ]);
    }

    // ---------------- 履歴タブ ----------------

    private TabPage BuildHistoryTab()
    {
        var page = new TabPage("補正履歴");
        // 幅は ResizeHistoryColumns でウィンドウ幅に合わせて配分する。
        foreach (var name in (string[])["時刻", "アプリ", "移動", "補正前", "補正後", "結果"])
            _history.Columns.Add(name);
        page.Controls.Add(_history);
        return page;
    }

    public void AddHistory(CorrectionRecord r)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => AddHistory(r)); return; }
        var item = new ListViewItem([
            r.Time.ToString("HH:mm:ss"), r.App, $"{r.From} → {r.To}",
            r.Before.ToString(), r.After.ToString(), r.Success ? r.Note ?? "補正しました" : r.Note ?? "失敗",
        ]);
        if (!r.Success) item.ForeColor = Color.Firebrick;
        _history.Items.Insert(0, item);
    }

    // ---------------- 設定バー ----------------

    private Control BuildSettingsBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6), WrapContents = true,
        };
        foreach (var (_, label) in Modes) _mode.Items.Add(label);

        var calib = new Button { Text = "見た目キャリブレーション...", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        calib.Click += (_, _) => { _grid.EndEdit(); if (ReadGridIntoSettings()) _app.StartCalibration(); };
        var reset = new Button { Text = "キャリブレーションを消去", AutoSize = true };
        reset.Click += (_, _) =>
        {
            foreach (var o in _app.Settings.Monitors.Values) { o.CalibratedPxPerMm = null; o.CalibratedAtWidth = null; }
            _app.SaveSettings();
        };
        var save = new Button { Text = "保存", AutoSize = true };
        save.Click += (_, _) => Save();
        var refresh = new Button { Text = "モニター再取得", AutoSize = true };
        refresh.Click += (_, _) => _monitors.Refresh();
        var log = new Button { Text = "ログを開く", AutoSize = true };
        log.Click += (_, _) => { if (File.Exists(Log.Path)) Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true }); };

        bar.Controls.AddRange([
            calib, reset,
            _enabled, _animate,
            new Label { Text = "既定の補正方法:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) },
            _mode, save, refresh, log,
        ]);
        return bar;
    }

    private void LoadSettingsToUi()
    {
        var s = _app.Settings;
        _enabled.Checked = s.Enabled;
        _animate.Checked = s.Animate;
        _mode.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Mode == s.DefaultMode));
    }

    private void Save()
    {
        _grid.EndEdit();
        if (!ReadGridIntoSettings()) return;
        var s = _app.Settings;
        s.Enabled = _enabled.Checked;
        s.Animate = _animate.Checked;
        s.DefaultMode = Modes[Math.Max(0, _mode.SelectedIndex)].Mode;
        _app.SaveSettings(); // → MonitorService.Reapply → FillMonitors
    }

    private static string SeverityLabel(Severity s) => s switch
    {
        Severity.Problem => "問題",
        Severity.Warning => "注意",
        _ => "情報",
    };
}
