using System.Drawing.Drawing2D;

namespace DpiBridge.App.UI;

/// <summary>トレイで見つけやすいよう、専用のアイコンをその場で描く（大小2枚のモニター）。</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        const int n = 64;
        using var bmp = new Bitmap(n, n);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(0, 120, 212));
            using (var path = Rounded(new RectangleF(1, 1, n - 2, n - 2), 14)) g.FillPath(bg, path);
            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.FillRectangle(white, 9, 22, 18, 13);   // 小さいモニター
            g.FillRectangle(white, 33, 13, 23, 31);  // 大きいモニター
            g.FillRectangle(white, 14, 38, 8, 3);
            g.FillRectangle(white, 40, 47, 9, 4);
            g.DrawLine(pen, 12, 53, 28, 53);         // 矢印（移動）
            g.DrawLine(pen, 24, 49, 28, 53);
            g.DrawLine(pen, 24, 57, 28, 53);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
