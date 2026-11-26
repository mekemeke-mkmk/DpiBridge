using DpiBridge.App.Services;
using DpiBridge.Core;

namespace DpiBridge.App.UI;

/// <summary>タスクトレイ常駐。設定の保持・監視の開始・診断画面の表示を担当する。</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly MonitorService _monitors;
    private readonly WindowTracker _tracker;
    private readonly List<CorrectionRecord> _history = new();
    private DiagnosticsForm? _diag;
    private readonly System.Windows.Forms.Timer _updateTimer = new();
    private ReleaseInfo? _pendingRelease;
    private bool _installing;
    private Action? _balloonAction;
    private readonly ToolStripMenuItem _updateItem;
    private readonly Control _invoker = new();
    private readonly IDisposable _showListener;
    private readonly SmoothDragController _smoothDrag;

    public AppSettings Settings { get; private set; }

    public TrayApp(LaunchMode mode = LaunchMode.Manual)
    {
        bool justUpdated = mode == LaunchMode.Updated;
        _ = _invoker.Handle; // UI スレッドに処理を戻すためのハンドルを作っておく
        Settings = AppSettings.Load(AppPaths.Settings);
        _monitors = new MonitorService(() => Settings);
        _tracker = new WindowTracker(_monitors, () => Settings);
        _tracker.Corrected += rec =>
        {
            _history.Insert(0, rec);
            if (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
            _diag?.AddHistory(rec);
        };

        _smoothDrag = new SmoothDragController(_monitors, () => Settings, _invoker);
        _tracker.TakeOver = _smoothDrag.TryTakeOver;
        _tracker.MoveSizeEnded = _smoothDrag.OnMoveSizeEnded;
        _smoothDrag.Moved += rec =>
        {
            _history.Insert(0, rec);
            if (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
            _diag?.AddHistory(rec);
        };
        var smoothItem = new ToolStripMenuItem("モニター間をなめらかに移動（Shift+ドラッグで標準動作）")
        { Checked = Settings.SmoothDrag, CheckOnClick = true };
        smoothItem.CheckedChanged += (_, _) => { Settings.SmoothDrag = smoothItem.Checked; SaveSettings(); };

        _enabledItem = new ToolStripMenuItem("自動補正を有効にする") { Checked = Settings.Enabled, CheckOnClick = true };
        _enabledItem.CheckedChanged += (_, _) => { Settings.Enabled = _enabledItem.Checked; SaveSettings(); UpdateTooltip(); };

        _updateItem = new ToolStripMenuItem("更新を確認...", null, async (_, _) => await CheckForUpdatesAsync(manual: true));
        var autoInstall = new ToolStripMenuItem("更新を自動でインストール") { Checked = Settings.AutoInstallUpdates, CheckOnClick = true };
        autoInstall.CheckedChanged += (_, _) => { Settings.AutoInstallUpdates = autoInstall.Checked; SaveSettings(); };
        var startup = new ToolStripMenuItem("Windows のサインイン時に起動") { Checked = SafeStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) =>
        {
            try { StartupRegistration.Set(startup.Checked); }
            catch (Exception ex) { Log.Error("自動起動の設定に失敗", ex); startup.Checked = SafeStartupEnabled(); }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"DpiBridge {UpdateService.CurrentVersion}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(smoothItem);
        menu.Items.Add("診断・設定を開く...", null, (_, _) => ShowDiagnostics());
        menu.Items.Add("見た目キャリブレーション...", null, (_, _) => StartCalibration());
        menu.Items.Add("左右の位置合わせ...", null, (_, _) => StartAlignment());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_updateItem);
        menu.Items.Add(autoInstall);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("診断情報をコピー（問い合わせ用）", null, (_, _) => CopyDiagnostics());
        menu.Items.Add("設定フォルダーを開く", null, (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.Folder);
            System.Diagnostics.Process.Start("explorer.exe", AppPaths.Folder);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowDiagnostics();
        _icon.BalloonTipClicked += (_, _) => { var a = _balloonAction; _balloonAction = null; a?.Invoke(); };
        _icon.BalloonTipClosed += (_, _) => _balloonAction = null;
        UpdateTooltip();

        // 初回起動時や見た目の大きさが不明なモニターがあるときは診断画面を自動で開いて知らせる。
        var unknown = _monitors.Monitors.Where(m => !m.HasRealScale).ToList();
        bool needsSetup = !File.Exists(AppPaths.Settings) || unknown.Count > 0;
        // 手動で起動したときは必ず画面を出す（トレイに隠れて「起動しない」ように見えるのを防ぐ）。
        if (mode == LaunchMode.Manual || (mode == LaunchMode.Startup && needsSetup))
        {
            ShowDiagnostics();
            if (unknown.Count > 0)
                Balloon(StartCalibration, "DpiBridge: キャリブレーションが必要です",
                    $"{string.Join("、", unknown.Select(m => m.FriendlyName))} の見た目の大きさが分からないため、" +
                    "拡大率が同じモニター間では補正が働きません。「見た目キャリブレーション」を実行してください。",
                    ToolTipIcon.Warning);
        }
        if (justUpdated)
            Balloon(null, "DpiBridge を更新しました", $"バージョン {UpdateService.CurrentVersion} になりました。設定はそのまま引き継がれています。", ToolTipIcon.Info);

        // 更新確認: 起動 20 秒後に1回、その後は 12 時間ごと。
        _updateTimer.Interval = 20_000;
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 12 * 60 * 60 * 1000;
            if (Settings.CheckForUpdates) await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();

        StartupRegistration.Repair();

        // 2つ目の起動（exe をもう一度ダブルクリック等）が来たら画面を前に出す。
        _showListener = SingleInstance.Listen(() => _invoker.BeginInvoke(ShowDiagnostics));

        Log.Info($"起動 mode={mode} elevated={WindowInspector.SelfElevated} monitors={_monitors.Monitors.Count}");
    }

    private static bool SafeStartupEnabled()
    {
        try { return StartupRegistration.IsEnabled; } catch { return false; }
    }

    private void Balloon(Action? onClick, string title, string text, ToolTipIcon icon)
    {
        _balloonAction = onClick;
        _icon.ShowBalloonTip(10000, title, text, icon);
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_installing) return;
        _updateItem.Enabled = false;
        try
        {
            var release = await UpdateService.CheckAsync(Settings.UpdateRepository);
            if (release is null)
            {
                if (manual)
                    MessageBox.Show($"最新版です（{UpdateService.CurrentVersion}）。", "DpiBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _pendingRelease = release;
            _updateItem.Text = $"バージョン {release.Version} に更新...";
            if (Settings.AutoInstallUpdates && !manual) await InstallAsync(release, confirm: false);
            else if (manual) await InstallAsync(release, confirm: true);
            else
                Balloon(() => _ = InstallAsync(release, confirm: true), $"DpiBridge {release.Version} があります",
                    "ここをクリックすると更新します（数秒で終わり、設定は引き継がれます）。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("更新の確認に失敗", ex);
            if (manual)
                MessageBox.Show($"更新を確認できませんでした。\n{ex.Message}", "DpiBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _updateItem.Enabled = !_installing;
        }
    }

    private async Task InstallAsync(ReleaseInfo release, bool confirm)
    {
        if (_installing) return;
        if (confirm)
        {
            string notes = string.IsNullOrWhiteSpace(release.Notes) ? "" : "\n\n" + Truncate(release.Notes.Trim(), 1200);
            var answer = MessageBox.Show(
                $"DpiBridge {UpdateService.CurrentVersion} → {release.Version} に更新しますか？{notes}",
                "DpiBridge の更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
        }
        _installing = true;
        _updateItem.Enabled = false;
        try
        {
            await UpdateService.InstallAsync(release, new Progress<string>(m => _icon.Text = Truncate("DpiBridge: " + m, 63)));
            ExitThread(); // 新しい exe が起動済み。自分は終了する。
        }
        catch (Exception ex)
        {
            _installing = false;
            _updateItem.Enabled = true;
            UpdateTooltip();
            Log.Error("更新に失敗", ex);
            MessageBox.Show($"更新できませんでした（今のバージョンのまま動作します）。\n{ex.Message}", "DpiBridge",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CopyDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"DpiBridge {UpdateService.CurrentVersion} / 管理者権限={WindowInspector.SelfElevated} / OS={Environment.OSVersion.Version}");
        sb.AppendLine($"自動補正={Settings.Enabled} なめらか移動={Settings.SmoothDrag} 既定モード={Settings.DefaultMode}");
        foreach (var m in _monitors.Monitors)
            sb.AppendLine($"モニター {m.FriendlyName}{(m.IsPrimary ? "(メイン)" : "")}: {m.Bounds} 拡大率{m.ScalePercent:0}% {m.PxPerMm:0.00}px/mm 根拠={m.Source} 仮想={m.IsVirtual}");
        sb.AppendLine($"直近のドラッグ判定: {SmoothDragController.LastDecision}");
        sb.AppendLine("--- ログ（最後の 40 行）---");
        try
        {
            if (File.Exists(Log.Path))
                foreach (var line in File.ReadLines(Log.Path).TakeLast(40)) sb.AppendLine(line);
        }
        catch (Exception ex) { sb.AppendLine("ログを読めませんでした: " + ex.Message); }
        try
        {
            Clipboard.SetText(sb.ToString());
            Balloon(null, "診断情報をコピーしました", "チャットなどに貼り付けて送ってください。", ToolTipIcon.Info);
        }
        catch (Exception ex) { Log.Error("クリップボードへのコピーに失敗", ex); }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void UpdateTooltip() =>
        _icon.Text = Settings.Enabled ? "DpiBridge（自動補正: 有効）" : "DpiBridge（自動補正: 停止中）";

    public void SaveSettings()
    {
        try { Settings.Save(AppPaths.Settings); }
        catch (Exception ex) { Log.Error("設定の保存に失敗", ex); }
        _monitors.Reapply();
        _smoothDrag?.UpdateActive();
        if (_enabledItem.Checked != Settings.Enabled) _enabledItem.Checked = Settings.Enabled;
    }

    private CalibrationSession? _calibration;

    public void StartCalibration()
    {
        _calibration?.CloseAll();
        _monitors.Refresh();
        _calibration = new CalibrationSession(_monitors.Monitors, results =>
        {
            foreach (var (id, pxPerMm) in results)
            {
                var mon = _monitors.Monitors.FirstOrDefault(m => m.Id == id);
                if (!Settings.Monitors.TryGetValue(id, out var o)) Settings.Monitors[id] = o = new MonitorOverride();
                o.CalibratedPxPerMm = pxPerMm;
                o.CalibratedAtWidth = mon?.Bounds.Width;
            }
            SaveSettings();
            Log.Info("キャリブレーション保存: " + string.Join(", ", results.Select(r => $"{r.Key}={r.Value:0.000}px/mm")));
            _calibration = null;
            ShowDiagnostics();
        });
        _calibration.Show();
    }

    private AlignmentSession? _alignment;

    public void StartAlignment()
    {
        _alignment?.CloseAll();
        _monitors.Refresh();
        if (_monitors.Monitors.Count < 2) return;
        _alignment = new AlignmentSession(_monitors.Monitors, Settings, offsets =>
        {
            foreach (var (id, mm) in offsets)
            {
                if (!Settings.Monitors.TryGetValue(id, out var o)) Settings.Monitors[id] = o = new MonitorOverride();
                o.AlignOffsetMm = mm == 0 ? null : mm;
            }
            SaveSettings();
            Log.Info("位置合わせ保存: " + string.Join(", ", offsets.Select(o => $"{o.Key}={o.Value:0.0}mm")));
            _alignment = null;
        });
        _alignment.Show();
    }

    private void ShowDiagnostics()
    {
        if (_diag is { IsDisposed: false })
        {
            if (_diag.WindowState == FormWindowState.Minimized) _diag.WindowState = FormWindowState.Normal;
            _diag.Show();
            _diag.Activate();
            return;
        }
        _diag = new DiagnosticsForm(this, _monitors, _history);
        _diag.Show();
    }

    protected override void ExitThreadCore()
    {
        _showListener.Dispose();
        _smoothDrag.Dispose();
        _updateTimer.Dispose();
        _tracker.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _diag?.Close();
        base.ExitThreadCore();
    }
}
