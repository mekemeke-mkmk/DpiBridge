namespace DpiBridge.Core;

/// <summary>EDID から画面の実寸を読み取る。</summary>
public static class EdidParser
{
    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>
    /// 実寸(mm)を返す。まず最初の詳細タイミング記述子(mm単位)を使い、無ければ基本表示パラメータ(cm単位)を使う。
    /// 仮想ディスプレイなどで値が無い/不正なら null。
    /// </summary>
    public static (double WidthMm, double HeightMm)? TryGetPhysicalSizeMm(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || !edid[..8].SequenceEqual(Header)) return null;

        // 詳細タイミング記述子 (offset 54, 18バイト)。先頭2バイトのピクセルクロックが0でなければタイミング。
        const int dtd = 54;
        if (edid[dtd] != 0 || edid[dtd + 1] != 0)
        {
            int w = edid[dtd + 12] | ((edid[dtd + 14] & 0xF0) << 4);
            int h = edid[dtd + 13] | ((edid[dtd + 14] & 0x0F) << 8);
            if (IsPlausible(w, h)) return (w, h);
        }

        // 基本表示パラメータ (offset 21, 22) は cm 単位。
        int wcm = edid[21], hcm = edid[22];
        if (IsPlausible(wcm * 10, hcm * 10)) return (wcm * 10, hcm * 10);
        return null;
    }

    // 5cm 未満や 5m 超は仮想ディスプレイのダミー値とみなす。
    private static bool IsPlausible(int wMm, int hMm) => wMm >= 50 && hMm >= 50 && wMm <= 5000 && hMm <= 5000;
}
