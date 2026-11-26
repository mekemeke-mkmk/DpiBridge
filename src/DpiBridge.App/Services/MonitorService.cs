using System.Runtime.InteropServices;
using DpiBridge.App.Native;
using DpiBridge.Core;
using Microsoft.Win32;

namespace DpiBridge.App.Services;

/// <summary>接続中モニターの解像度・DPI・実寸・仮想ディスプレイかどうかを集める。</summary>
internal sealed class MonitorService
{
    private readonly Func<AppSettings> _settings;
    private IReadOnlyList<MonitorGeometry> _cache = [];
    private IReadOnlyList<MonitorGeometry> _raw = [];

    public MonitorService(Func<AppSettings> settings)
    {
        _settings = settings;
        Refresh();
        // 解像度・拡大率・接続の変更で更新する。
        SystemEvents.DisplaySettingsChanged += (_, _) => Refresh();
    }

    public event Action? Changed;

    /// <summary>手動設定（実寸）を反映済みのモニター一覧。</summary>
    public IReadOnlyList<MonitorGeometry> Monitors => _cache;

    /// <summary>手動設定を反映する前（EDID 由来）のモニター一覧。</summary>
    public IReadOnlyList<MonitorGeometry> RawMonitors => _raw;

    /// <summary>EDID に書かれていた実寸（仮想ディスプレイの場合は使わないが表示用に残す）。</summary>
    public IReadOnlyDictionary<string, (double W, double H)> EdidSizes => _edid;
    private Dictionary<string, (double W, double H)> _edid = new();

    public void Refresh()
    {
        try
        {
            var edid = new Dictionary<string, (double W, double H)>();
            _raw = Enumerate(edid);
            _edid = edid;
        }
        catch (Exception ex)
        {
            Log.Error("モニター列挙に失敗", ex);
        }
        Reapply();
    }

    /// <summary>設定変更後に手動の実寸を反映し直す。</summary>
    public void Reapply()
    {
        var s = _settings();
        _cache = _raw.Select(s.Apply).ToList();
        Changed?.Invoke();
    }

    private static List<MonitorGeometry> Enumerate(Dictionary<string, (double W, double H)> edidSizes)
    {
        var targets = QueryTargets();
        var result = new List<MonitorGeometry>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (!Win32.GetMonitorInfo(hMon, ref mi)) return true;
            uint dpi = Win32.GetDpiForMonitor(hMon, Win32.MDT_EFFECTIVE_DPI, out var dx, out _) == 0 ? dx : 96;

            targets.TryGetValue(mi.szDevice, out var t);
            (double W, double H)? mm = t.DevicePath is { Length: > 0 } ? ReadEdidSize(t.DevicePath) : null;

            // 同じモニターを差し直しても設定が残るようデバイスパスを ID にする。取れなければ GDI 名。
            string id = string.IsNullOrEmpty(t.DevicePath) ? mi.szDevice : t.DevicePath;
            if (mm is { } size) edidSizes[id] = size;
            // 仮想ディスプレイの EDID はドライバーが用意したダミー（実際の Surface の大きさではない）なので使わない。
            if (t.IsVirtual) mm = null;

            result.Add(new MonitorGeometry
            {
                Id = id,
                FriendlyName = string.IsNullOrWhiteSpace(t.Name) ? mi.szDevice.TrimStart('\\', '.') : t.Name,
                Bounds = mi.rcMonitor.ToRectI(),
                WorkArea = mi.rcWork.ToRectI(),
                Dpi = dpi,
                WidthMm = mm?.W,
                HeightMm = mm?.H,
                IsVirtual = t.IsVirtual,
                IsPrimary = (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
            });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private readonly record struct TargetInfo(string Name, string DevicePath, bool IsVirtual);

    /// <summary>GDI デバイス名（\\.\DISPLAY1）→ モニター名・デバイスパス。</summary>
    private static Dictionary<string, TargetInfo> QueryTargets()
    {
        var map = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
        if (DisplayConfig.GetDisplayConfigBufferSizes(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != 0)
            return map;
        var paths = new DisplayConfig.PATH_INFO[np];
        var modes = new DisplayConfig.MODE_INFO[nm];
        if (DisplayConfig.QueryDisplayConfig(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0)
            return map;

        for (int i = 0; i < np; i++)
        {
            var p = paths[i];
            var src = new DisplayConfig.SOURCE_DEVICE_NAME
            {
                header = new()
                {
                    type = DisplayConfig.DEVICE_INFO_GET_SOURCE_NAME,
                    size = Marshal.SizeOf<DisplayConfig.SOURCE_DEVICE_NAME>(),
                    adapterId = p.sourceInfo.adapterId,
                    id = p.sourceInfo.id,
                },
                viewGdiDeviceName = "",
            };
            if (DisplayConfig.DisplayConfigGetDeviceInfo(ref src) != 0) continue;

            var tgt = new DisplayConfig.TARGET_DEVICE_NAME
            {
                header = new()
                {
                    type = DisplayConfig.DEVICE_INFO_GET_TARGET_NAME,
                    size = Marshal.SizeOf<DisplayConfig.TARGET_DEVICE_NAME>(),
                    adapterId = p.targetInfo.adapterId,
                    id = p.targetInfo.id,
                },
                monitorFriendlyDeviceName = "",
                monitorDevicePath = "",
            };
            if (DisplayConfig.DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;

            uint tech = p.targetInfo.outputTechnology;
            bool isVirtual = tech is DisplayConfig.OUTPUT_INDIRECT_VIRTUAL or DisplayConfig.OUTPUT_INDIRECT_WIRED
                             or DisplayConfig.OUTPUT_OTHER;
            // 複製表示では同じソースに複数ターゲットがあり得るので最初の1つを採用。
            map.TryAdd(src.viewGdiDeviceName, new TargetInfo(tgt.monitorFriendlyDeviceName, tgt.monitorDevicePath, isVirtual));
        }
        return map;
    }

    /// <summary>
    /// デバイスパス \\?\DISPLAY#DEL40F6#5&amp;xxxx&amp;0&amp;UID4353#{guid} から
    /// HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\DEL40F6\5&amp;xxxx&amp;0&amp;UID4353\Device Parameters\EDID を読む。
    /// </summary>
    private static (double, double)? ReadEdidSize(string devicePath)
    {
        try
        {
            var parts = devicePath.Split('#');
            if (parts.Length < 3) return null;
            string key = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters";
            using var k = Registry.LocalMachine.OpenSubKey(key);
            if (k?.GetValue("EDID") is not byte[] edid) return null;
            return EdidParser.TryGetPhysicalSizeMm(edid);
        }
        catch (Exception ex)
        {
            Log.Error($"EDID 読み取り失敗: {devicePath}", ex);
            return null;
        }
    }
}
