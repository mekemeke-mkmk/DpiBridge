namespace DpiBridge.Core;

/// <summary>
/// モニター1枚に表示するゴースト（ドラッグ中のウィンドウのプレビュー）の一片。
/// Dest はそのモニター上の表示位置（物理ピクセル）、Src* は元ウィンドウの見た目の矩形のうち、どの範囲を映すか（0..1 の割合）。
/// </summary>
public sealed record GhostPiece(MonitorGeometry Monitor, RectI Dest, double SrcLeft, double SrcTop, double SrcRight, double SrcBottom);

public enum SnapZone { None, Maximize, LeftHalf, RightHalf }

/// <summary>
/// ドラッグ中のウィンドウを「1枚の紙が複数の画面をまたいで滑っていく」ように見せるための幾何計算。
/// 各モニターでは、そのモニターの実際の見た目の密度（px/mm）でゴーストを描き、モニターの境目でつなげる。
/// </summary>
public static class DragGeometry
{
    public static RectI? Intersect(RectI a, RectI b)
    {
        var r = new RectI(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
        return r.IsEmpty ? null : r;
    }

    /// <summary>カーソルのあるモニター上での、ウィンドウの見た目の矩形（大きさは実寸 mm を維持）。</summary>
    public static RectI RectOnCursorMonitor(double widthMm, double heightMm, MonitorGeometry cursorMonitor,
        (int X, int Y) cursor, double grabFx, double grabFy)
    {
        int w = Math.Max(1, (int)Math.Round(widthMm * cursorMonitor.PxPerMm));
        int h = Math.Max(1, (int)Math.Round(heightMm * cursorMonitor.PxPerMm));
        int left = (int)Math.Round(cursor.X - grabFx * w);
        int top = (int)Math.Round(cursor.Y - grabFy * h);
        return RectI.FromXywh(left, top, w, h);
    }

    /// <summary>
    /// モニター c と m が辺を共有していれば、カーソルをその辺に投影した点（＝ 2 枚をつなぐ基準点）。共有していなければ null。
    /// </summary>
    public static (double X, double Y)? SharedEdgeAnchor(MonitorGeometry c, MonitorGeometry m, (int X, int Y) cursor)
    {
        var a = c.Bounds; var b = m.Bounds;
        int vTop = Math.Max(a.Top, b.Top), vBottom = Math.Min(a.Bottom, b.Bottom);
        int hLeft = Math.Max(a.Left, b.Left), hRight = Math.Min(a.Right, b.Right);
        if (vBottom > vTop)
        {
            if (a.Right == b.Left) return (a.Right, Math.Clamp(cursor.Y, vTop, vBottom));
            if (a.Left == b.Right) return (a.Left, Math.Clamp(cursor.Y, vTop, vBottom));
        }
        if (hRight > hLeft)
        {
            if (a.Bottom == b.Top) return (Math.Clamp(cursor.X, hLeft, hRight), a.Bottom);
            if (a.Top == b.Bottom) return (Math.Clamp(cursor.X, hLeft, hRight), a.Top);
        }
        return null;
    }

    /// <summary>矩形を点 (ax, ay) を中心に k 倍する。</summary>
    public static RectI ScaleAbout(RectI r, double ax, double ay, double k) => new(
        (int)Math.Round(ax + (r.Left - ax) * k),
        (int)Math.Round(ay + (r.Top - ay) * k),
        (int)Math.Round(ax + (r.Right - ax) * k),
        (int)Math.Round(ay + (r.Bottom - ay) * k));

    /// <summary>
    /// カーソルのあるモニター c 上の矩形 rectOnC から、各モニターに表示するゴーストの片を求める。
    /// c に隣接するモニターには、境目の基準点を中心にそのモニターの密度で拡縮した矩形を使うので、境目で途切れずにつながる。
    /// </summary>
    public static IReadOnlyList<GhostPiece> Pieces(RectI rectOnC, MonitorGeometry c, (int X, int Y) cursor, IReadOnlyList<MonitorGeometry> monitors)
    {
        var list = new List<GhostPiece>();
        foreach (var m in monitors)
        {
            RectI full;
            if (m.Id == c.Id) full = rectOnC;
            else if (SharedEdgeAnchor(c, m, cursor) is var (ax, ay)) full = ScaleAbout(rectOnC, ax, ay, m.PxPerMm / c.PxPerMm);
            else continue;

            if (Intersect(full, m.Bounds) is not { } piece || full.IsEmpty) continue;
            list.Add(new GhostPiece(m, piece,
                (piece.Left - full.Left) / (double)full.Width,
                (piece.Top - full.Top) / (double)full.Height,
                (piece.Right - full.Left) / (double)full.Width,
                (piece.Bottom - full.Top) / (double)full.Height));
        }
        return list;
    }

    /// <summary>r が monitor 以外のモニターに少しでも重なるか。</summary>
    public static bool TouchesOtherMonitor(RectI r, MonitorGeometry monitor, IReadOnlyList<MonitorGeometry> monitors) =>
        monitors.Any(m => m.Id != monitor.Id && Intersect(r, m.Bounds) is not null);

    /// <summary>2 つのモニター間を移るとき、見た目が変わる（＝ゴーストで運ぶ必要がある）か。</summary>
    public static bool LooksDifferent(MonitorGeometry a, MonitorGeometry b) =>
        a.Dpi != b.Dpi || Math.Abs(a.PxPerMm / b.PxPerMm - 1) > 0.02;

    /// <summary>点と矩形の距離の2乗（矩形内なら 0）。</summary>
    public static double DistanceSquared(RectI r, double x, double y)
    {
        double dx = Math.Max(Math.Max(r.Left - x, 0), x - r.Right);
        double dy = Math.Max(Math.Max(r.Top - y, 0), y - r.Bottom);
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// ドラッグ中に実ウィンドウを隠しておく画面外の位置。どのモニターにも重ならず、最も近いモニターが home になる場所
    /// （Windows はウィンドウの DPI を最も近いモニターで決めるので、隠している間に大きさが変わらない）。無ければ null。
    /// </summary>
    public static RectI? ParkingSpot(RectI windowRect, MonitorGeometry home, IReadOnlyList<MonitorGeometry> monitors, int gap = 64)
    {
        var b = home.Bounds;
        int w = windowRect.Width, h = windowRect.Height;
        int y = Math.Clamp(windowRect.Top, b.Top, Math.Max(b.Top, b.Bottom - h));
        int x = Math.Clamp(windowRect.Left, b.Left, Math.Max(b.Left, b.Right - w));
        RectI[] candidates =
        [
            RectI.FromXywh(b.Left - w - gap, y, w, h),  // 左
            RectI.FromXywh(b.Right + gap, y, w, h),     // 右
            RectI.FromXywh(x, b.Bottom + gap, w, h),    // 下
            RectI.FromXywh(x, b.Top - h - gap, w, h),   // 上
        ];
        foreach (var c in candidates)
        {
            if (monitors.Any(m => Intersect(c, m.Bounds) is not null)) continue;
            // 最も近いモニターが home であること
            if (monitors.All(m => m.Id == home.Id || NearestEdgeGap(c, m) > NearestEdgeGap(c, home)))
                return c;
        }
        return null;
    }

    private static double NearestEdgeGap(RectI a, MonitorGeometry m)
    {
        var r = m.Bounds;
        double dx = Math.Max(0, Math.Max(r.Left - a.Right, a.Left - r.Right));
        double dy = Math.Max(0, Math.Max(r.Top - a.Bottom, a.Top - r.Bottom));
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// DPI の違うモニターへ移すとき、Windows（とアプリ）が DPI の比率で大きさを変えることを見越して、
    /// 先に逆数倍しておいた矩形（左上は固定）。
    /// </summary>
    public static RectI PreCompensate(RectI target, uint fromDpi, uint toDpi)
    {
        if (fromDpi == toDpi || fromDpi == 0 || toDpi == 0) return target;
        double k = fromDpi / (double)toDpi;
        return RectI.FromXywh(target.Left, target.Top, Math.Max(1, (int)Math.Round(target.Width * k)), Math.Max(1, (int)Math.Round(target.Height * k)));
    }

    /// <summary>ドロップ時の最終矩形: 作業領域より大きければ縮め、はみ出していれば中に入れる。</summary>
    public static RectI FitInto(RectI r, RectI work)
    {
        int w = Math.Min(r.Width, work.Width), h = Math.Min(r.Height, work.Height);
        return SizingEngine.ClampInto(RectI.FromXywh(r.Left, r.Top, w, h), work);
    }

    /// <summary>画面の外周の端で離したときのスナップ（上端=最大化、左右端=半分）。隣にモニターがある辺は対象外。</summary>
    public static SnapZone DetectSnap((int X, int Y) cursor, MonitorGeometry c, IReadOnlyList<MonitorGeometry> monitors, int edge = 2)
    {
        var b = c.Bounds;
        bool Free(int x, int y) => !monitors.Any(m => m.Id != c.Id && m.Bounds.Contains(x, y));
        if (cursor.Y <= b.Top + edge - 1 && Free(cursor.X, b.Top - 1)) return SnapZone.Maximize;
        if (cursor.X <= b.Left + edge - 1 && Free(b.Left - 1, cursor.Y)) return SnapZone.LeftHalf;
        if (cursor.X >= b.Right - edge && Free(b.Right, cursor.Y)) return SnapZone.RightHalf;
        return SnapZone.None;
    }

    public static RectI SnapRect(SnapZone zone, RectI work) => zone switch
    {
        SnapZone.LeftHalf => new RectI(work.Left, work.Top, work.Left + work.Width / 2, work.Bottom),
        SnapZone.RightHalf => new RectI(work.Left + work.Width / 2, work.Top, work.Right, work.Bottom),
        _ => work,
    };
}
