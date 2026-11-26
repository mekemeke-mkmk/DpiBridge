using System.Text;
using DpiBridge.App.Native;
using DpiBridge.Core;

namespace DpiBridge.App.Services;

internal sealed record WindowInfo
{
    public required IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public uint ProcessId { get; init; }
    public string? ExePath { get; init; }
    public string? ExeName => ExePath is null ? null : Path.GetFileName(ExePath).ToLowerInvariant();
    public DpiAwareness Awareness { get; init; }
    public uint Dpi { get; init; }
    /// <summary>true/false が確定できない（アクセス拒否＝相手が昇格している可能性が高い）場合は null。</summary>
    public bool? IsElevated { get; init; }
    public RectI WindowRect { get; init; }
    public RectI VisualRect { get; init; }
}

internal static class WindowInspector
{
    public static RectI GetWindowRect(IntPtr hwnd) =>
        Win32.GetWindowRect(hwnd, out var r) ? r.ToRectI() : default;

    /// <summary>影などを除いた見た目の枠。取れなければ GetWindowRect。</summary>
    public static RectI GetVisualRect(IntPtr hwnd)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
            return r.ToRectI();
        return GetWindowRect(hwnd);
    }

    /// <summary>サイズ補正の対象にしてよい「普通の」トップレベルウィンドウか。</summary>
    public static bool IsResizableTopLevel(IntPtr hwnd) => WhyNotResizable(hwnd) is null;

    /// <summary>補正対象外ならその理由、対象なら null。</summary>
    public static string? WhyNotResizable(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd)) return "非表示";
        if (Win32.IsZoomed(hwnd)) return "最大化中";
        if (Win32.IsIconic(hwnd)) return "最小化中";
        if (IsArranged(hwnd)) return "スナップ配置中"; // Windows の配置を尊重する
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        if ((style & Win32.WS_CHILD) != 0) return "子ウィンドウ";
        if ((ex & Win32.WS_EX_TOOLWINDOW) != 0) return "ツールウィンドウ";
        // サイズ変更できない（ダイアログ等）は大きさを変えない。
        if ((style & Win32.WS_THICKFRAME) == 0) return "サイズ変更できないウィンドウ";
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return "非表示（クローク）";
        return null;
    }

    private static bool _arrangedUnsupported;

    private static bool IsArranged(IntPtr hwnd)
    {
        if (_arrangedUnsupported) return false;
        try { return Win32.IsWindowArranged(hwnd); }
        catch (EntryPointNotFoundException) { _arrangedUnsupported = true; return false; }
    }

    public static DpiAwareness GetAwareness(IntPtr hwnd)
    {
        try
        {
            var ctx = Win32.GetWindowDpiAwarenessContext(hwnd);
            if (ctx == IntPtr.Zero) return DpiAwareness.Unknown;
            if (Win32.AreDpiAwarenessContextsEqual(ctx, Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return DpiAwareness.PerMonitorV2;
            if (Win32.AreDpiAwarenessContextsEqual(ctx, Win32.DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED)) return DpiAwareness.UnawareGdiScaled;
            return Win32.GetAwarenessFromDpiAwarenessContext(ctx) switch
            {
                0 => DpiAwareness.Unaware,
                1 => DpiAwareness.SystemAware,
                2 => DpiAwareness.PerMonitor,
                _ => DpiAwareness.Unknown,
            };
        }
        catch (EntryPointNotFoundException)
        {
            return DpiAwareness.Unknown;
        }
    }

    public static string? GetExePath(uint pid)
    {
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Win32.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { Win32.CloseHandle(h); }
    }

    public static bool? IsProcessElevated(uint pid)
    {
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!Win32.OpenProcessToken(h, Win32.TOKEN_QUERY, out var tok)) return null;
            try
            {
                return Win32.GetTokenInformation(tok, Win32.TokenElevation, out int elevated, sizeof(int), out _) ? elevated != 0 : null;
            }
            finally { Win32.CloseHandle(tok); }
        }
        finally { Win32.CloseHandle(h); }
    }

    public static bool SelfElevated { get; } = IsProcessElevated((uint)Environment.ProcessId) == true;

    public static WindowInfo Inspect(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        var title = new StringBuilder(256);
        Win32.GetWindowText(hwnd, title, title.Capacity);
        var cls = new StringBuilder(256);
        Win32.GetClassName(hwnd, cls, cls.Capacity);
        return new WindowInfo
        {
            Handle = hwnd,
            Title = title.ToString(),
            ClassName = cls.ToString(),
            ProcessId = pid,
            ExePath = GetExePath(pid),
            Awareness = GetAwareness(hwnd),
            Dpi = Win32.GetDpiForWindow(hwnd),
            IsElevated = IsProcessElevated(pid),
            WindowRect = GetWindowRect(hwnd),
            VisualRect = GetVisualRect(hwnd),
        };
    }

    public static IntPtr RootWindowUnderCursor()
    {
        if (!Win32.GetCursorPos(out var p)) return IntPtr.Zero;
        var h = Win32.WindowFromPoint(p);
        return h == IntPtr.Zero ? h : Win32.GetAncestor(h, Win32.GA_ROOT);
    }
}
