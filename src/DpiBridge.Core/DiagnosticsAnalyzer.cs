namespace DpiBridge.Core;

public enum Severity { Info, Warning, Problem }

public sealed record Finding(Severity Severity, string Message);

/// <summary>ウィンドウの DPI 認識モード。</summary>
public enum DpiAwareness
{
    Unknown,
    Unaware,
    UnawareGdiScaled,
    SystemAware,
    PerMonitor,
    PerMonitorV2,
}

public static class DiagnosticsAnalyzer
{
    /// <summary>Windows で選べる拡大率。</summary>
    public static readonly int[] ScaleSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    public static IReadOnlyList<Finding> AnalyzeMonitors(IReadOnlyList<MonitorGeometry> monitors, AppSettings settings)
    {
        var list = new List<Finding>();
        foreach (var m in monitors)
        {
            if (!m.HasRealScale)
                list.Add(new(Severity.Problem,
                    $"{Label(m)}: 見た目の大きさが不明です{(m.IsVirtual ? "（仮想ディスプレイは EDID が当てにならないため自動取得できません）" : "")}。" +
                    "このままでは Windows の拡大率の差の分しか補正されず、拡大率が同じなら何も補正されません。" +
                    "「見た目キャリブレーション」を実行してください。"));

            if (settings.Monitors.TryGetValue(m.Id, out var o) && o.NativeWidth is > 0 && o.NativeHeight is > 0
                && (o.NativeWidth != m.Bounds.Width || o.NativeHeight != m.Bounds.Height))
                list.Add(new(Severity.Problem,
                    $"{Label(m)}: Windows 上の解像度 {m.Bounds.Width}x{m.Bounds.Height} が配信先の本来の解像度 " +
                    $"{o.NativeWidth}x{o.NativeHeight} と一致していません。Moonlight 側で引き伸ばされるため全体がぼやけます。" +
                    "仮想ディスプレイの解像度・Sunshine の配信解像度・Moonlight の解像度をすべて配信先の本来の解像度に揃えてください。"));
        }

        var known = monitors.Where(m => m.HasRealScale).ToList();
        if (known.Count >= 2)
        {
            var reference = known.FirstOrDefault(m => m.IsPrimary) ?? known[0];
            foreach (var m in known.Where(m => m != reference))
            {
                // 同じ DIP 数のものの見た目の大きさ = DIP / DipPerMm なので、相対的な見た目は逆数の比。
                double look = reference.DipPerMm / m.DipPerMm;
                if (Math.Abs(look - 1) > 0.03)
                {
                    // m 側を変えるのが基本。100% 未満にはできないので、その場合は基準側を上げる。
                    double idealM = IdealScalePercent(reference, m);
                    string advice = idealM >= ScaleSteps[0] * 0.94
                        ? $"{Label(m)} の拡大率を {Snap(idealM)}%（現在 {m.ScalePercent:0}%）"
                        : $"{Label(reference)} の拡大率を {Snap(IdealScalePercent(m, reference))}%（現在 {reference.ScalePercent:0}%）";
                    list.Add(new(Severity.Info,
                        $"Windows の拡大率のままだと、{Label(m)} では {Label(reference)} と比べて文字や中身が約 {look * 100:0}% の大きさに見えます。" +
                        "ウィンドウの外枠の大きさは本アプリが揃えますが、中の文字の大きさは Windows の拡大率で決まるため、" +
                        $"文字の大きさも揃えたい場合は {advice} にしてください。"));
                }
            }
        }
        return list;
    }

    /// <summary>reference と同じ見た目サイズになる target の拡大率（Windows の選択肢に丸める）。</summary>
    public static int RecommendScale(MonitorGeometry reference, MonitorGeometry target) =>
        Snap(IdealScalePercent(reference, target));

    /// <summary>reference と同じ見た目になる target の理想の拡大率(%)。丸め前。</summary>
    public static double IdealScalePercent(MonitorGeometry reference, MonitorGeometry target) =>
        // DipPerMm を揃える: target.PxPerMm * 96 / dpi = reference.DipPerMm → dpi*100/96 が %
        target.PxPerMm * 100.0 / reference.DipPerMm;

    private static int Snap(double percent) => ScaleSteps.MinBy(s => Math.Abs(s - percent));

    public static IReadOnlyList<Finding> AnalyzeWindow(DpiAwareness awareness, uint windowMonitorDpi, uint systemDpi, bool isElevated, bool selfElevated)
    {
        var list = new List<Finding>();
        switch (awareness)
        {
            case DpiAwareness.Unaware:
            case DpiAwareness.UnawareGdiScaled:
                if (windowMonitorDpi != 96)
                    list.Add(new(Severity.Problem,
                        "このアプリは DPI 非対応のため、Windows がビットマップとして引き伸ばして表示しています（ぼやけの原因）。" +
                        "exe のプロパティ → 互換性 → 高 DPI 設定の変更 → 「高い DPI スケールの動作を上書き」で" +
                        "「アプリケーション」または「システム (拡張)」を試してください。"));
                break;
            case DpiAwareness.SystemAware:
                if (windowMonitorDpi != systemDpi)
                    list.Add(new(Severity.Problem,
                        $"このアプリはシステム DPI ({systemDpi * 100 / 96}%) でしか描画しないため、このモニター " +
                        $"({windowMonitorDpi * 100 / 96}%) では Windows が引き伸ばして表示しています（ぼやけの原因）。" +
                        "互換性設定の「高 DPI スケールの動作を上書き」→「アプリケーション」で改善する場合があります。" +
                        "なお、システム DPI はサインイン時のメインモニターで決まるため、サインアウト→サインインでも変わります。"));
                break;
            case DpiAwareness.PerMonitorV2:
            case DpiAwareness.PerMonitor:
                list.Add(new(Severity.Info,
                    "このアプリはモニターごとの DPI に対応しており、Windows 側でぼやけることはありません。" +
                    "それでもぼやけて見える場合は、配信（仮想ディスプレイ/Sunshine/Moonlight）の解像度不一致を疑ってください。"));
                break;
        }
        if (isElevated && !selfElevated)
            list.Add(new(Severity.Warning,
                "このアプリは管理者権限で動いているため、通常権限の DpiBridge からはサイズを変更できません。" +
                "DpiBridge を管理者として実行してください。"));
        return list;
    }

    private static string Label(MonitorGeometry m) =>
        string.IsNullOrWhiteSpace(m.FriendlyName) ? m.Id : m.FriendlyName + (m.IsPrimary ? "(メイン)" : "");
}
