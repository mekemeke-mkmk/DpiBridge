namespace DpiBridge.Core;

public enum SizingMode
{
    /// <summary>見た目の物理サイズ(mm)を維持する。</summary>
    PhysicalSize,
    /// <summary>作業領域に対する比率を維持する。</summary>
    WorkAreaRatio,
    /// <summary>補正しない（Windows 任せ）。</summary>
    None,
}

public sealed record SizingRequest
{
    /// <summary>移動を始めた時点の見た目の矩形（移動元モニター上）。</summary>
    public required RectI StartVisual { get; init; }
    /// <summary>移動が終わった時点の見た目の矩形（Windows の DPI 再スケール後）。</summary>
    public required RectI EndVisual { get; init; }
    public required MonitorGeometry Source { get; init; }
    public required MonitorGeometry Destination { get; init; }
    /// <summary>移動終了時のカーソル位置。つかんでいた位置を保ったまま拡縮するのに使う。null なら矩形の上端中央を基準にする。</summary>
    public (int X, int Y)? Cursor { get; init; }
    public SizingMode Mode { get; init; } = SizingMode.PhysicalSize;
    public int MinWidth { get; init; } = 120;
    public int MinHeight { get; init; } = 80;
}

public static class SizingEngine
{
    /// <summary>移動先での目標の見た目矩形を計算する。補正不要なら null。</summary>
    public static RectI? ComputeTarget(SizingRequest r)
    {
        if (r.Mode == SizingMode.None || r.StartVisual.IsEmpty || r.EndVisual.IsEmpty) return null;
        if (r.Source.Id == r.Destination.Id) return null;

        var (w, h) = TargetSize(r);
        var work = r.Destination.WorkArea;
        w = Math.Clamp(w, Math.Min(r.MinWidth, work.Width), work.Width);
        h = Math.Clamp(h, Math.Min(r.MinHeight, work.Height), work.Height);

        // 位置: つかんでいた点（カーソル）の相対位置を保つ。
        var end = r.EndVisual;
        double fx, fy;
        double ax, ay;
        if (r.Cursor is var (cx, cy) && end.Contains(cx, cy))
        {
            fx = (cx - end.Left) / (double)end.Width;
            ax = cx;
            // 縦はタイトルバーの位置を保つため上端からの距離を比例で縮める（下に行き過ぎないよう比率で）。
            fy = (cy - end.Top) / (double)end.Height;
            ay = cy;
        }
        else
        {
            fx = 0.5; ax = end.CenterX;
            fy = 0.0; ay = end.Top;
        }

        int left = (int)Math.Round(ax - fx * w);
        int top = (int)Math.Round(ay - fy * h);
        var target = RectI.FromXywh(left, top, w, h);
        target = ClampInto(target, work);

        // ほとんど変化しないなら触らない（無駄な再描画を避ける）。
        if (Math.Abs(target.Width - end.Width) <= 2 && Math.Abs(target.Height - end.Height) <= 2
            && Math.Abs(target.Left - end.Left) <= 2 && Math.Abs(target.Top - end.Top) <= 2)
            return null;
        return target;
    }

    public static (int W, int H) TargetSize(SizingRequest r)
    {
        var s = r.StartVisual;
        switch (r.Mode)
        {
            case SizingMode.WorkAreaRatio:
                return (
                    (int)Math.Round(s.Width / (double)r.Source.WorkArea.Width * r.Destination.WorkArea.Width),
                    (int)Math.Round(s.Height / (double)r.Source.WorkArea.Height * r.Destination.WorkArea.Height));
            default:
                double k = r.Destination.PxPerMm / r.Source.PxPerMm;
                return ((int)Math.Round(s.Width * k), (int)Math.Round(s.Height * k));
        }
    }

    /// <summary>矩形を作業領域内に収める（大きすぎる場合は呼び出し側でサイズを先に縮めておくこと）。</summary>
    public static RectI ClampInto(RectI r, RectI work)
    {
        int dx = 0, dy = 0;
        if (r.Right > work.Right) dx = work.Right - r.Right;
        if (r.Left + dx < work.Left) dx = work.Left - r.Left;
        if (r.Bottom > work.Bottom) dy = work.Bottom - r.Bottom;
        if (r.Top + dy < work.Top) dy = work.Top - r.Top;
        return r.Offset(dx, dy);
    }

    /// <summary>アニメーション用の補間（ease-out cubic）。t は 0..1。</summary>
    public static RectI Interpolate(RectI from, RectI to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        double e = 1 - Math.Pow(1 - t, 3);
        int L(int a, int b) => (int)Math.Round(a + (b - a) * e);
        return new RectI(L(from.Left, to.Left), L(from.Top, to.Top), L(from.Right, to.Right), L(from.Bottom, to.Bottom));
    }

    /// <summary>点を含むモニター。どれにも含まれなければ最も近いもの。</summary>
    public static MonitorGeometry? MonitorFromPoint(IReadOnlyList<MonitorGeometry> monitors, double x, double y)
    {
        MonitorGeometry? best = null;
        double bestDist = double.MaxValue;
        foreach (var m in monitors)
        {
            if (m.Bounds.Contains(x, y)) return m;
            double dx = Math.Max(Math.Max(m.Bounds.Left - x, 0), x - m.Bounds.Right);
            double dy = Math.Max(Math.Max(m.Bounds.Top - y, 0), y - m.Bounds.Bottom);
            double d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = m; }
        }
        return best;
    }
}
