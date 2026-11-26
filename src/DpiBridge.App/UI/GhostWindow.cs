using DpiBridge.App.Native;
using DpiBridge.Core;

namespace DpiBridge.App.UI;

/// <summary>
/// ドラッグ中のウィンドウの「生きたプレビュー」を1枚のモニター上に表示する枠なしウィンドウ。
/// 中身は DWM サムネイル（Windows がウィンドウの現在の表示をそのまま合成してくれる機能）なので、動画なども動いたまま表示される。
/// </summary>
internal sealed class GhostWindow : Form
{
    private IntPtr _thumb;
    private IntPtr _source;

    public GhostWindow()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(40, 40, 44);
        Text = "DpiBridge Ghost";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 /*WS_EX_TOOLWINDOW*/ | 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000008 /*WS_EX_TOPMOST*/;
            return cp;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true; // 大きさは自分で決める（Windows の推奨サイズに変えさせない）
    }

    /// <summary>映すウィンドウを設定する。</summary>
    public bool Attach(IntPtr source)
    {
        if (_source == source && _thumb != IntPtr.Zero) return true;
        Detach();
        _ = Handle;
        if (Win32.DwmRegisterThumbnail(Handle, source, out _thumb) != 0) { _thumb = IntPtr.Zero; return false; }
        _source = source;
        return true;
    }

    /// <summary>元ウィンドウのサムネイル上の大きさ（物理ピクセル）。</summary>
    public (int W, int H) SourceSize =>
        _thumb != IntPtr.Zero && Win32.DwmQueryThumbnailSourceSize(_thumb, out var s) == 0 ? (s.cx, s.cy) : (0, 0);

    /// <summary>画面上の dest に、元ウィンドウの srcRect（元ウィンドウ左上基準のピクセル）を映す。</summary>
    public void Present(RectI dest, RectI srcRect, byte opacity = 255)
    {
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, dest.Left, dest.Top, dest.Width, dest.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        if (_thumb == IntPtr.Zero) return;
        var props = new Win32.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Win32.DWM_TNP_RECTDESTINATION | Win32.DWM_TNP_RECTSOURCE | Win32.DWM_TNP_OPACITY
                      | Win32.DWM_TNP_VISIBLE | Win32.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT { Left = 0, Top = 0, Right = dest.Width, Bottom = dest.Height },
            rcSource = new RECT { Left = srcRect.Left, Top = srcRect.Top, Right = srcRect.Right, Bottom = srcRect.Bottom },
            opacity = opacity,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };
        Win32.DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    public void SetOpacity(byte opacity)
    {
        if (_thumb == IntPtr.Zero) return;
        var props = new Win32.DWM_THUMBNAIL_PROPERTIES { dwFlags = Win32.DWM_TNP_OPACITY, opacity = opacity };
        Win32.DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    public void Detach()
    {
        if (_thumb != IntPtr.Zero) Win32.DwmUnregisterThumbnail(_thumb);
        _thumb = IntPtr.Zero;
        _source = IntPtr.Zero;
        if (IsHandleCreated) Hide();
    }

    protected override void Dispose(bool disposing)
    {
        Detach();
        base.Dispose(disposing);
    }
}
