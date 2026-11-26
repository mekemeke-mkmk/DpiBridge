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
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 400 };
    private IntPtr _lastHover;

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
        Text = "DpiBridge — 診断と設定";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);
        Size = new Size(1000, 680);
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildMonitorTab());
        tabs.TabPages.Add(BuildWindowTab());
        tabs.TabPages.Add(BuildHistoryTab());
        Controls.Add(tabs);
        Controls.Add(BuildSettingsBar());

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
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;

        void Col(string name, string header, bool editable = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, ReadOnly = !editable };
            if (editable) c.DefaultCellStyle.BackColor = Color.LightYellow;
            _grid.Columns.Add(c);
        }
        Col("name", "モニター");
        Col("kind", "種別");
        Col("res", "解像度");
        Col("scale", "拡大率");
        Col("eff", "実効解像度");
        Col("edid", "EDID 実寸");
        Col("src", "見た目の大きさの根拠");
        Col("diag", "対角インチ（入力）", editable: true);
        Col("native", "配信先の本来の解像度（入力 例 2880x1920）", editable: true);
        Col("ppmm", "px/mm");
        Col("look", "同じウィンドウの見た目（メイン比）");

        var help = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4),
            Text = "まず下の「見た目キャリブレーション」を実行してください（各モニターに出る枠を実物のカードか定規に合わせるだけ）。" +
                   "仮想ディスプレイ（Surface 側）は実際の見た目の大きさを Windows から取得できないため、これが必要です。" +
                   "Moonlight の引き伸ばしや Windows の拡大率の違いも含めて実測するので、拡大率が同じでも見た目が揃うように補正されます。" +
                   "黄色の欄（対角インチ・本来の解像度）は任意です。",
            MaximumSize = new Size(960, 0),
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 230 };
        split.Panel1.Controls.Add(_grid);
        var findBox = new GroupBox { Text = "診断結果", Dock = DockStyle.Fill };
        findBox.Controls.Add(_findings);
        split.Panel2.Controls.Add(findBox);

        page.Controls.Add(split);
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
        var help = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4),
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
            "",
            "■ 診断",
            .. findings.Select(f => $"[{SeverityLabel(f.Severity)}] {f.Message}"),
        ]);
    }

    // ---------------- 履歴タブ ----------------

    private TabPage BuildHistoryTab()
    {
        var page = new TabPage("補正履歴");
        _history.Columns.Add("時刻", 90);
        _history.Columns.Add("アプリ", 150);
        _history.Columns.Add("移動", 260);
        _history.Columns.Add("補正前", 160);
        _history.Columns.Add("補正後", 160);
        _history.Columns.Add("結果", 200);
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
            Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6), WrapContents = true,
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
