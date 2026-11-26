using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpiBridge.Core;

/// <summary>モニターごとの手動設定（仮想ディスプレイは EDID が当てにならないので実寸を入力する）。</summary>
public sealed class MonitorOverride
{
    /// <summary>画面の対角サイズ(インチ)。例: Surface Pro なら 13。</summary>
    public double? DiagonalInches { get; set; }
    /// <summary>キャリブレーションで実測した 1mm あたりの物理ピクセル数（最優先で使う）。</summary>
    public double? CalibratedPxPerMm { get; set; }
    /// <summary>キャリブレーション時の解像度。解像度が変わったら比例して換算する。</summary>
    public int? CalibratedAtWidth { get; set; }
    /// <summary>配信先（Surface 等）の本来の解像度。配信解像度の不一致チェックに使う。</summary>
    public int? NativeWidth { get; set; }
    public int? NativeHeight { get; set; }
}

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public SizingMode DefaultMode { get; set; } = SizingMode.PhysicalSize;
    public bool Animate { get; set; } = true;
    public int AnimationMs { get; set; } = 120;
    /// <summary>移動終了から補正開始までの待ち時間。アプリ自身の DPI 再レイアウトを待つ。</summary>
    public int SettleDelayMs { get; set; } = 60;

    /// <summary>
    /// タイトルバーのドラッグを DpiBridge が行い、見た目の違うモニター間をなめらかに移動させる
    /// （途中で大きさが変わったり一部だけ拡大されたりしない）。Shift を押しながらのドラッグは Windows 標準。
    /// </summary>
    public bool SmoothDrag { get; set; } = true;

    /// <summary>更新を確認する GitHub リポジトリ（owner/repo）。</summary>
    public string UpdateRepository { get; set; } = "mekemeke-mkmk/DpiBridge";
    /// <summary>起動時と1日ごとに新しいバージョンを確認する。</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>新しいバージョンが見つかったら確認なしでインストールする。</summary>
    public bool AutoInstallUpdates { get; set; } = false;

    /// <summary>キー: モニターID（デバイスパス）。</summary>
    public Dictionary<string, MonitorOverride> Monitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>キー: exe ファイル名（小文字）。</summary>
    public Dictionary<string, SizingMode> AppModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SizingMode ModeFor(string? exeName) =>
        exeName != null && AppModes.TryGetValue(exeName, out var m) ? m : DefaultMode;

    /// <summary>手動設定を反映したモニター情報を返す。</summary>
    public MonitorGeometry Apply(MonitorGeometry m)
    {
        if (!Monitors.TryGetValue(m.Id, out var o)) return m;
        if (o.DiagonalInches is > 0)
        {
            var (w, h) = PhysicalSize.FromDiagonalInches(o.DiagonalInches.Value, m.Bounds.Width, m.Bounds.Height);
            m = m with { WidthMm = w, HeightMm = h, SizeIsManual = true };
        }
        if (o.CalibratedPxPerMm is > 0)
        {
            double v = o.CalibratedPxPerMm.Value;
            // キャリブレーション後に解像度を変えた場合、同じ画面に表示しているなら px/mm は解像度に比例する。
            if (o.CalibratedAtWidth is > 0 && o.CalibratedAtWidth != m.Bounds.Width)
                v *= m.Bounds.Width / (double)o.CalibratedAtWidth.Value;
            m = m with { CalibratedPxPerMm = v };
        }
        return m;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AppSettings FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        // デシリアライズ後は比較子が既定に戻るので作り直す。
        s.Monitors = new(s.Monitors ?? new(), StringComparer.OrdinalIgnoreCase);
        s.AppModes = new(s.AppModes ?? new(), StringComparer.OrdinalIgnoreCase);
        return s;
    }

    public static AppSettings Load(string path)
    {
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path)) : new AppSettings(); }
        catch (Exception) { return new AppSettings(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }
}
