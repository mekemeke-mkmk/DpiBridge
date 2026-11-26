namespace DpiBridge.App.UI;

/// <summary>
/// 幅に合わせて折り返し、必要な高さに自動で伸び縮みするラベル（Dock = Top 用）。
/// 標準の Label の AutoSize は折り返さないため、長い説明文が画面外に切れてしまう。
/// </summary>
internal sealed class WrapLabel : Label
{
    public WrapLabel()
    {
        AutoSize = false;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitHeight(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitHeight(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); FitHeight(); }
    protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); FitHeight(); }

    private void FitHeight()
    {
        int width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
        var size = TextRenderer.MeasureText(Text, Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        int h = size.Height + Padding.Vertical + 2;
        if (Height != h) Height = h;
    }
}
