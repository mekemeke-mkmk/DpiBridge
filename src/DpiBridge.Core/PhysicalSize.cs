namespace DpiBridge.Core;

public static class PhysicalSize
{
    /// <summary>対角インチと解像度（縦横比）から表示領域の幅・高さ(mm)を求める。</summary>
    public static (double WidthMm, double HeightMm) FromDiagonalInches(double diagonalInches, int pixelWidth, int pixelHeight)
    {
        if (diagonalInches <= 0) throw new ArgumentOutOfRangeException(nameof(diagonalInches));
        if (pixelWidth <= 0 || pixelHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        double diagMm = diagonalInches * 25.4;
        double diagPx = Math.Sqrt((double)pixelWidth * pixelWidth + (double)pixelHeight * pixelHeight);
        return (diagMm * pixelWidth / diagPx, diagMm * pixelHeight / diagPx);
    }

    public static double DiagonalInches(double widthMm, double heightMm) =>
        Math.Sqrt(widthMm * widthMm + heightMm * heightMm) / 25.4;
}
