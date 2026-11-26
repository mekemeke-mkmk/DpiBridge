namespace DpiBridge.Core;

/// <summary>物理ピクセル座標の矩形（Win32 RECT と同じく Right/Bottom は含まない）。</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public double CenterX => Left + Width / 2.0;
    public double CenterY => Top + Height / 2.0;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectI FromXywh(int x, int y, int w, int h) => new(x, y, x + w, y + h);

    public RectI Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

/// <summary>ウィンドウ矩形(GetWindowRect)と見た目の枠(DWM拡張フレーム)の差。影などの見えない余白。</summary>
public readonly record struct FrameMargins(int Left, int Top, int Right, int Bottom)
{
    public static FrameMargins Between(RectI windowRect, RectI visualRect) => new(
        visualRect.Left - windowRect.Left,
        visualRect.Top - windowRect.Top,
        windowRect.Right - visualRect.Right,
        windowRect.Bottom - visualRect.Bottom);

    /// <summary>見た目の矩形から SetWindowPos に渡すウィンドウ矩形を求める。</summary>
    public RectI Expand(RectI visualRect) => new(
        visualRect.Left - Left,
        visualRect.Top - Top,
        visualRect.Right + Right,
        visualRect.Bottom + Bottom);
}

/// <summary>見た目の大きさ（px/mm）の根拠。</summary>
public enum ScaleSource
{
    /// <summary>実寸不明。Windows の拡大率どおりとみなす（補正は拡大率の差の分しか働かない）。</summary>
    Estimated,
    /// <summary>モニターの EDID から取得。</summary>
    Edid,
    /// <summary>対角インチの手入力から計算。</summary>
    Manual,
    /// <summary>画面上のキャリブレーションで実測（配信時の引き伸ばしも含めて正確）。</summary>
    Calibrated,
}

/// <summary>モニター1枚の情報。座標はすべて物理ピクセル。</summary>
public sealed record MonitorGeometry
{
    public required string Id { get; init; }
    public string FriendlyName { get; init; } = "";
    public required RectI Bounds { get; init; }
    public required RectI WorkArea { get; init; }
    /// <summary>Windows の有効DPI（100%=96, 150%=144 ...）。</summary>
    public required uint Dpi { get; init; }
    /// <summary>表示領域の実寸(mm)。不明なら null。</summary>
    public double? WidthMm { get; init; }
    public double? HeightMm { get; init; }
    /// <summary>キャリブレーションで実測した「実際に目に見える 1mm あたりの物理ピクセル数」。</summary>
    public double? CalibratedPxPerMm { get; init; }
    /// <summary>WidthMm/HeightMm が手入力由来か（EDID 由来なら false）。</summary>
    public bool SizeIsManual { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsPrimary { get; init; }

    public double ScalePercent => Dpi * 100.0 / 96.0;

    public bool HasPhysicalSize => WidthMm is > 0 && HeightMm is > 0;

    /// <summary>見た目の大きさが実測・実寸で分かっているか（推定ではないか）。</summary>
    public bool HasRealScale => CalibratedPxPerMm is > 0 || HasPhysicalSize;

    public ScaleSource Source =>
        CalibratedPxPerMm is > 0 ? ScaleSource.Calibrated
        : HasPhysicalSize ? (SizeIsManual ? ScaleSource.Manual : ScaleSource.Edid)
        : ScaleSource.Estimated;

    /// <summary>
    /// 1mm あたりの物理ピクセル数。優先順位: キャリブレーション → 実寸 → 推定。
    /// 推定は「Windows の拡大率どおり 96DIP = 1インチ」とみなす（＝Per-Monitor 対応アプリ自身の再スケールと同じ）。
    /// </summary>
    public double PxPerMm =>
        CalibratedPxPerMm is > 0 ? CalibratedPxPerMm.Value
        : HasPhysicalSize ? (Bounds.Width / WidthMm!.Value + Bounds.Height / HeightMm!.Value) / 2.0
        : Dpi / 25.4;

    /// <summary>1mm あたりの DIP（論理ピクセル）数。モニター間でこの値が揃っていれば見た目の大きさが揃う。</summary>
    public double DipPerMm => PxPerMm * 96.0 / Dpi;
}
