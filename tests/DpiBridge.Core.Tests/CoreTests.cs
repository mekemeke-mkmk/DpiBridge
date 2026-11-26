using DpiBridge.Core;

namespace DpiBridge.Core.Tests;

public class Fixtures
{
    // 物理モニター: 27インチ 2560x1440 @100%（≒ 597x336mm）
    public static MonitorGeometry Desk => new()
    {
        Id = "desk", FriendlyName = "Desk", IsPrimary = false,
        Bounds = new RectI(-2560, 0, 0, 1440), WorkArea = new RectI(-2560, 0, 0, 1392),
        Dpi = 96, WidthMm = 597, HeightMm = 336,
    };

    // 仮想ディスプレイ（Surface 13インチ 2880x1920 @200%）。実寸は手入力で与える想定。
    public static MonitorGeometry Surface
    {
        get
        {
            var (w, h) = PhysicalSize.FromDiagonalInches(13, 2880, 1920);
            return new()
            {
                Id = "surface", FriendlyName = "Surface", IsPrimary = true, IsVirtual = true,
                Bounds = new RectI(0, 0, 2880, 1920), WorkArea = new RectI(0, 0, 2880, 1824),
                Dpi = 192, WidthMm = w, HeightMm = h,
            };
        }
    }
}

public class PhysicalSizeTests
{
    [Fact]
    public void Diagonal_RoundTrips()
    {
        var (w, h) = PhysicalSize.FromDiagonalInches(13, 2880, 1920);
        Assert.Equal(13, PhysicalSize.DiagonalInches(w, h), 3);
        Assert.Equal(1.5, w / h, 3);
    }

    [Fact]
    public void UnknownSize_FallsBackToWindowsScale()
    {
        var m = Fixtures.Surface with { WidthMm = null, HeightMm = null };
        Assert.Equal(192 / 25.4, m.PxPerMm, 6);
        Assert.Equal(96 / 25.4, m.DipPerMm, 6);
    }
}

public class EdidTests
{
    private static byte[] MakeEdid(int wMm, int hMm, int wCm, int hCm, bool withDtd = true)
    {
        var e = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(e, 0);
        e[21] = (byte)wCm; e[22] = (byte)hCm;
        if (withDtd)
        {
            e[54] = 0x01; e[55] = 0x1D; // pixel clock != 0
            e[54 + 12] = (byte)(wMm & 0xFF);
            e[54 + 13] = (byte)(hMm & 0xFF);
            e[54 + 14] = (byte)(((wMm >> 8) << 4) | (hMm >> 8));
        }
        return e;
    }

    [Fact]
    public void ReadsDetailedTimingMillimeters()
    {
        var r = EdidParser.TryGetPhysicalSizeMm(MakeEdid(597, 336, 60, 34));
        Assert.Equal((597.0, 336.0), r);
    }

    [Fact]
    public void FallsBackToCentimeters()
    {
        var r = EdidParser.TryGetPhysicalSizeMm(MakeEdid(0, 0, 60, 34, withDtd: false));
        Assert.Equal((600.0, 340.0), r);
    }

    [Fact]
    public void VirtualDisplayDummyValuesAreRejected()
    {
        Assert.Null(EdidParser.TryGetPhysicalSizeMm(MakeEdid(0, 0, 0, 0, withDtd: false)));
        Assert.Null(EdidParser.TryGetPhysicalSizeMm(new byte[10]));
    }
}

public class SizingEngineTests
{
    [Fact]
    public void PhysicalSize_KeepsMillimeters()
    {
        var src = Fixtures.Desk;
        var dst = Fixtures.Surface;
        var start = RectI.FromXywh(-2000, 200, 600, 400);
        // Windows の DPI 再スケール後（PMv2 アプリなら 2 倍）を想定した終了時矩形
        var end = RectI.FromXywh(500, 300, 1200, 800);

        var t = SizingEngine.ComputeTarget(new SizingRequest
        {
            StartVisual = start, EndVisual = end, Source = src, Destination = dst,
        });

        Assert.NotNull(t);
        double k = dst.PxPerMm / src.PxPerMm;
        Assert.Equal((int)Math.Round(600 * k), t!.Value.Width);
        Assert.Equal((int)Math.Round(400 * k), t.Value.Height);
        // 上端は保持、横は中心を保持
        Assert.Equal(end.Top, t.Value.Top);
        Assert.Equal(end.CenterX, t.Value.CenterX, 0);
    }

    [Fact]
    public void KeepsGrabPointUnderCursor()
    {
        var end = RectI.FromXywh(500, 300, 1200, 800);
        var cursor = (X: 500 + 900, Y: 300 + 20); // 右寄りのタイトルバーをつかんでいた
        var t = SizingEngine.ComputeTarget(new SizingRequest
        {
            StartVisual = RectI.FromXywh(-2000, 200, 600, 400), EndVisual = end,
            Source = Fixtures.Desk, Destination = Fixtures.Surface, Cursor = cursor,
        })!.Value;

        double fx = (cursor.X - t.Left) / (double)t.Width;
        Assert.Equal(0.75, fx, 2);
        Assert.InRange(cursor.Y - t.Top, 0, 25); // カーソルはまだタイトルバー付近
    }

    [Fact]
    public void ClampsIntoWorkArea()
    {
        var dst = Fixtures.Surface;
        var t = SizingEngine.ComputeTarget(new SizingRequest
        {
            StartVisual = RectI.FromXywh(-2500, 0, 2500, 1380),
            EndVisual = RectI.FromXywh(2000, 1000, 2500, 1380),
            Source = Fixtures.Desk, Destination = dst,
        })!.Value;

        Assert.True(t.Left >= dst.WorkArea.Left && t.Right <= dst.WorkArea.Right, t.ToString());
        Assert.True(t.Top >= dst.WorkArea.Top && t.Bottom <= dst.WorkArea.Bottom, t.ToString());
    }

    [Fact]
    public void WorkAreaRatioMode()
    {
        var src = Fixtures.Desk;
        var dst = Fixtures.Surface;
        var start = RectI.FromXywh(-2560, 0, src.WorkArea.Width / 2, src.WorkArea.Height / 2);
        var (w, h) = SizingEngine.TargetSize(new SizingRequest
        {
            StartVisual = start, EndVisual = start, Source = src, Destination = dst, Mode = SizingMode.WorkAreaRatio,
        });
        Assert.Equal(dst.WorkArea.Width / 2, w);
        Assert.Equal(dst.WorkArea.Height / 2, h);
    }

    [Fact]
    public void NoCorrection_SameMonitor_NoneMode_OrAlreadyRight()
    {
        var r = RectI.FromXywh(100, 100, 800, 600);
        Assert.Null(SizingEngine.ComputeTarget(new SizingRequest
        { StartVisual = r, EndVisual = r, Source = Fixtures.Surface, Destination = Fixtures.Surface }));
        Assert.Null(SizingEngine.ComputeTarget(new SizingRequest
        { StartVisual = r, EndVisual = r, Source = Fixtures.Desk, Destination = Fixtures.Surface, Mode = SizingMode.None }));

        // 実寸どおりの比率でモニターを設定していれば Windows の再スケールだけで既に正しい → 触らない
        var src = Fixtures.Desk;
        var dst = Fixtures.Desk with { Id = "desk2", Bounds = new RectI(0, 0, 2560, 1440), WorkArea = new RectI(0, 0, 2560, 1392) };
        var start = RectI.FromXywh(-2000, 100, 800, 600);
        var end = RectI.FromXywh(500, 100, 800, 600);
        Assert.Null(SizingEngine.ComputeTarget(new SizingRequest { StartVisual = start, EndVisual = end, Source = src, Destination = dst }));
    }

    [Fact]
    public void InterpolateEndsAtTarget()
    {
        var a = RectI.FromXywh(0, 0, 100, 100);
        var b = RectI.FromXywh(50, 60, 300, 200);
        Assert.Equal(a, SizingEngine.Interpolate(a, b, 0));
        Assert.Equal(b, SizingEngine.Interpolate(a, b, 1));
        Assert.Equal(b, SizingEngine.Interpolate(a, b, 5));
    }

    [Fact]
    public void FrameMarginsRoundTrip()
    {
        var window = new RectI(93, 100, 1107, 807);
        var visual = new RectI(100, 100, 1100, 800);
        var m = FrameMargins.Between(window, visual);
        Assert.Equal(window, m.Expand(visual));
    }

    [Fact]
    public void MonitorFromPoint_PicksNearestWhenOutside()
    {
        var mons = new[] { Fixtures.Desk, Fixtures.Surface };
        Assert.Equal("surface", SizingEngine.MonitorFromPoint(mons, 100, 100)!.Id);
        Assert.Equal("desk", SizingEngine.MonitorFromPoint(mons, -100, 100)!.Id);
        Assert.Equal("surface", SizingEngine.MonitorFromPoint(mons, 3000, 100)!.Id);
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void RecommendsScaleToMatchVisualSize()
    {
        // Surface 13" 2880x1920: 約 10.5 px/mm、Desk 27" 1440p@100%: 約 4.29 px/mm → 約 244% → 250%
        int rec = DiagnosticsAnalyzer.RecommendScale(Fixtures.Desk, Fixtures.Surface);
        Assert.Equal(250, rec);
    }

    [Fact]
    public void DetectsStreamResolutionMismatch()
    {
        var s = new AppSettings();
        s.Monitors["surface"] = new MonitorOverride { DiagonalInches = 13, NativeWidth = 2880, NativeHeight = 1920 };
        var mons = new[] { Fixtures.Desk, Fixtures.Surface with { Bounds = new RectI(0, 0, 1920, 1080), WorkArea = new RectI(0, 0, 1920, 1032) } };
        var f = DiagnosticsAnalyzer.AnalyzeMonitors(mons, s);
        Assert.Contains(f, x => x.Severity == Severity.Problem && x.Message.Contains("2880x1920"));
    }

    [Fact]
    public void WarnsAboutUnknownPhysicalSize()
    {
        var mons = new[] { Fixtures.Desk, Fixtures.Surface with { WidthMm = null, HeightMm = null } };
        var f = DiagnosticsAnalyzer.AnalyzeMonitors(mons, new AppSettings());
        Assert.Contains(f, x => x.Severity == Severity.Problem && x.Message.Contains("キャリブレーション"));
    }

    [Fact]
    public void WindowFindings()
    {
        Assert.Contains(DiagnosticsAnalyzer.AnalyzeWindow(DpiAwareness.Unaware, 192, 96, false, false), f => f.Severity == Severity.Problem);
        Assert.Contains(DiagnosticsAnalyzer.AnalyzeWindow(DpiAwareness.SystemAware, 192, 96, false, false), f => f.Severity == Severity.Problem);
        Assert.Empty(DiagnosticsAnalyzer.AnalyzeWindow(DpiAwareness.SystemAware, 96, 96, false, false));
        Assert.Contains(DiagnosticsAnalyzer.AnalyzeWindow(DpiAwareness.PerMonitorV2, 192, 96, true, false), f => f.Severity == Severity.Warning);
    }
}

public class SettingsTests
{
    [Fact]
    public void JsonRoundTrip_KeepsCaseInsensitiveKeys()
    {
        var s = new AppSettings { DefaultMode = SizingMode.WorkAreaRatio, Animate = false };
        s.Monitors[@"\\?\DISPLAY#ABC#1"] = new MonitorOverride { DiagonalInches = 13 };
        s.AppModes["taskmgr.exe"] = SizingMode.None;

        var back = AppSettings.FromJson(s.ToJson());
        Assert.Equal(SizingMode.WorkAreaRatio, back.DefaultMode);
        Assert.False(back.Animate);
        Assert.Equal(13, back.Monitors[@"\\?\display#abc#1"].DiagonalInches);
        Assert.Equal(SizingMode.None, back.ModeFor("TaskMgr.exe"));
        Assert.Equal(SizingMode.WorkAreaRatio, back.ModeFor("notepad.exe"));
        Assert.Contains("\"WorkAreaRatio\"", s.ToJson());
    }

    [Fact]
    public void ApplyOverridesPhysicalSize()
    {
        var s = new AppSettings();
        s.Monitors["surface"] = new MonitorOverride { DiagonalInches = 13 };
        var m = s.Apply(Fixtures.Surface with { WidthMm = null, HeightMm = null });
        Assert.True(m.HasPhysicalSize);
        Assert.Equal(13, PhysicalSize.DiagonalInches(m.WidthMm!.Value, m.HeightMm!.Value), 3);
    }

    [Fact]
    public void BrokenFileLoadsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dpibridge-test-{Guid.NewGuid()}.json");
        File.WriteAllText(path, "{ not json");
        try { Assert.True(AppSettings.Load(path).Enabled); }
        finally { File.Delete(path); }
    }
}

public class CalibrationTests
{
    // 両方 100%・実寸不明（ユーザー報告の状況）
    private static MonitorGeometry Desk100 => Fixtures.Desk with { WidthMm = null, HeightMm = null };
    private static MonitorGeometry Surface100 => Fixtures.Surface with { WidthMm = null, HeightMm = null, Dpi = 96 };

    [Fact]
    public void SameScaleWithoutCalibration_DoesNothing()
    {
        var start = RectI.FromXywh(-2000, 100, 800, 600);
        var end = RectI.FromXywh(500, 100, 800, 600);
        Assert.Null(SizingEngine.ComputeTarget(new SizingRequest
        { StartVisual = start, EndVisual = end, Source = Desk100, Destination = Surface100 }));
    }

    [Fact]
    public void SameScaleWithCalibration_MatchesVisualSize()
    {
        var s = new AppSettings();
        s.Monitors["desk"] = new MonitorOverride { CalibratedPxPerMm = 4.3, CalibratedAtWidth = 2560 };
        s.Monitors["surface"] = new MonitorOverride { CalibratedPxPerMm = 8.6, CalibratedAtWidth = 2880 };
        var src = s.Apply(Desk100);
        var dst = s.Apply(Surface100);
        Assert.Equal(ScaleSource.Calibrated, dst.Source);

        var start = RectI.FromXywh(-2000, 100, 600, 400);
        var end = RectI.FromXywh(500, 100, 600, 400); // 100%→100% なので Windows は大きさを変えない
        var t = SizingEngine.ComputeTarget(new SizingRequest
        { StartVisual = start, EndVisual = end, Source = src, Destination = dst })!.Value;
        Assert.Equal(1200, t.Width); // 見た目のピクセル密度が 2 倍なので 2 倍のピクセル数で同じ見た目
        Assert.Equal(800, t.Height);
    }

    [Fact]
    public void CalibrationTakesPriorityOverInchesAndScalesWithResolution()
    {
        var s = new AppSettings();
        s.Monitors["surface"] = new MonitorOverride { DiagonalInches = 13, CalibratedPxPerMm = 8.0, CalibratedAtWidth = 2880 };
        var m = s.Apply(Surface100 with { Bounds = new RectI(0, 0, 1920, 1280), WorkArea = new RectI(0, 0, 1920, 1232) });
        Assert.Equal(8.0 * 1920 / 2880, m.PxPerMm, 6);
        Assert.True(m.HasRealScale);
    }

    [Fact]
    public void DiagnosticsAfterCalibration_RecommendsScale()
    {
        var s = new AppSettings();
        s.Monitors["desk"] = new MonitorOverride { CalibratedPxPerMm = 4.3 };
        s.Monitors["surface"] = new MonitorOverride { CalibratedPxPerMm = 8.6 };
        var mons = new[] { s.Apply(Desk100), s.Apply(Surface100) };
        var f = DiagnosticsAnalyzer.AnalyzeMonitors(mons, s);
        Assert.DoesNotContain(f, x => x.Severity == Severity.Problem);
        Assert.Contains(f, x => x.Message.Contains("200%"));
    }
}

public class UpdateCheckTests
{
    private const string Release = """
    {
      "tag_name": "v0.3.1", "draft": false, "prerelease": false,
      "html_url": "https://github.com/o/r/releases/tag/v0.3.1",
      "body": "変更点",
      "assets": [
        { "name": "DpiBridge.exe", "browser_download_url": "https://github.com/o/r/releases/download/v0.3.1/DpiBridge.exe" },
        { "name": "DpiBridge.exe.sha256", "browser_download_url": "https://github.com/o/r/releases/download/v0.3.1/DpiBridge.exe.sha256" }
      ]
    }
    """;

    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("0.3", "0.3.0")]
    [InlineData("V1.2.3-beta", "1.2.3")]
    public void ParsesVersions(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateCheck.ParseVersion(tag));

    [Fact]
    public void InvalidTag_IsNull() => Assert.Null(UpdateCheck.ParseVersion("latest"));

    [Fact]
    public void ParsesRelease()
    {
        var r = UpdateCheck.ParseLatestRelease(Release)!;
        Assert.Equal(new Version(0, 3, 1), r.Version);
        Assert.EndsWith("/DpiBridge.exe", r.ExeUrl);
        Assert.EndsWith(".sha256", r.Sha256Url);
        Assert.True(UpdateCheck.IsNewer(r, new Version(0, 3, 0, 0)));
        Assert.False(UpdateCheck.IsNewer(r, new Version(0, 3, 1, 0)));
        Assert.False(UpdateCheck.IsNewer(r, new Version(1, 0, 0)));
    }

    [Fact]
    public void IgnoresPrereleaseAndMissingExe()
    {
        Assert.Null(UpdateCheck.ParseLatestRelease(Release.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(UpdateCheck.ParseLatestRelease(Release.Replace("\"DpiBridge.exe\",", "\"Other.exe\",")));
        Assert.Null(UpdateCheck.ParseLatestRelease(Release.Replace("https://github.com/o/r/releases/download/v0.3.1/DpiBridge.exe\"", "http://evil/DpiBridge.exe\"")));
    }

    [Fact]
    public void ParsesShaFile()
    {
        var h = new string('a', 63) + "B";
        Assert.Equal(h.ToLowerInvariant(), UpdateCheck.ParseSha256File($"{h}  DpiBridge.exe\n"));
        Assert.Equal(h.ToLowerInvariant(), UpdateCheck.ParseSha256File(h));
        Assert.Null(UpdateCheck.ParseSha256File("xyz"));
    }
}

public class DragGeometryTests
{
    // 左: 1920x1080 @100%（4 px/mm）、右: 2736x1824 @200%（10 px/mm）。境目は x = 1920。
    private static MonitorGeometry Low => new()
    {
        Id = "low", FriendlyName = "Low", IsPrimary = true,
        Bounds = new RectI(0, 0, 1920, 1080), WorkArea = new RectI(0, 0, 1920, 1032), Dpi = 96, CalibratedPxPerMm = 4,
    };
    private static MonitorGeometry High => new()
    {
        Id = "high", FriendlyName = "High",
        Bounds = new RectI(1920, 0, 1920 + 2736, 1824), WorkArea = new RectI(1920, 0, 1920 + 2736, 1776), Dpi = 192, CalibratedPxPerMm = 10,
    };
    private static readonly MonitorGeometry[] Both = [Low, High];

    [Fact]
    public void RectKeepsMillimetersAndGrabPoint()
    {
        var r = DragGeometry.RectOnCursorMonitor(200, 100, High, (3000, 500), 0.25, 0.05);
        Assert.Equal(2000, r.Width);
        Assert.Equal(1000, r.Height);
        Assert.Equal(3000 - 500, r.Left);
        Assert.Equal(500 - 50, r.Top);
    }

    [Fact]
    public void SharedEdgeAnchor_ProjectsCursorOntoBoundary()
    {
        Assert.Equal((1920.0, 300.0), DragGeometry.SharedEdgeAnchor(Low, High, (1800, 300)));
        Assert.Equal((1920.0, 1080.0), DragGeometry.SharedEdgeAnchor(Low, High, (1800, 1500)));
        var far = High with { Id = "far", Bounds = new RectI(5000, 0, 6000, 1000) };
        Assert.Null(DragGeometry.SharedEdgeAnchor(Low, far, (1800, 300)));
    }

    [Fact]
    public void Pieces_AreContinuousAtTheBoundary_AndUseEachMonitorsDensity()
    {
        // カーソルは左（低密度）モニター、ウィンドウは 200x100mm で右モニターへはみ出している
        var cursor = (1800, 300);
        var onLow = DragGeometry.RectOnCursorMonitor(200, 100, Low, cursor, 0.5, 0.1); // 800x400px, x=1400..2200
        var pieces = DragGeometry.Pieces(onLow, Low, cursor, Both);
        Assert.Equal(2, pieces.Count);

        var low = pieces.Single(p => p.Monitor.Id == "low");
        var high = pieces.Single(p => p.Monitor.Id == "high");
        Assert.Equal(1920, low.Dest.Right);
        Assert.Equal(1920, high.Dest.Left);
        // 元ウィンドウのどこを映すかが境目で一致する（途切れない）
        Assert.Equal(low.SrcRight, high.SrcLeft, 3);
        // 右側は 10/4 倍の密度で描かれる → 同じ割合の幅でもピクセル数は 2.5 倍
        double lowPxPerFraction = low.Dest.Width / (low.SrcRight - low.SrcLeft);
        double highPxPerFraction = high.Dest.Width / (high.SrcRight - high.SrcLeft);
        Assert.Equal(2.5, highPxPerFraction / lowPxPerFraction, 2);
        // 低密度側の片はウィンドウ本来の大きさ（拡大されていない）
        Assert.Equal(400, low.Dest.Height);
        Assert.InRange(high.Dest.Height, 999, 1001);
    }

    [Fact]
    public void Pieces_SingleMonitorWhenFullyInside()
    {
        var cursor = (3000, 600);
        var r = DragGeometry.RectOnCursorMonitor(100, 50, High, cursor, 0.5, 0.1);
        var pieces = DragGeometry.Pieces(r, High, cursor, Both);
        Assert.Single(pieces);
        Assert.Equal((0.0, 0.0, 1.0, 1.0), (pieces[0].SrcLeft, pieces[0].SrcTop, pieces[0].SrcRight, pieces[0].SrcBottom));
    }

    [Fact]
    public void TouchesOtherMonitor()
    {
        Assert.False(DragGeometry.TouchesOtherMonitor(RectI.FromXywh(100, 100, 800, 600), Low, Both));
        Assert.True(DragGeometry.TouchesOtherMonitor(RectI.FromXywh(1500, 100, 800, 600), Low, Both));
        Assert.False(DragGeometry.TouchesOtherMonitor(RectI.FromXywh(-300, 100, 800, 600), Low, Both)); // 画面外へのはみ出しは OK
    }

    [Fact]
    public void LooksDifferent()
    {
        Assert.True(DragGeometry.LooksDifferent(Low, High));
        Assert.False(DragGeometry.LooksDifferent(Low, Low with { Id = "low2" }));
        Assert.True(DragGeometry.LooksDifferent(Low, Low with { Id = "low3", CalibratedPxPerMm = 6 })); // 同じ拡大率でも密度が違えば
    }

    [Fact]
    public void ParkingSpot_IsOffscreenAndNearestToHome()
    {
        var w = RectI.FromXywh(1200, 200, 600, 400);
        var spot = DragGeometry.ParkingSpot(w, Low, Both)!.Value;
        Assert.DoesNotContain(Both, m => DragGeometry.Intersect(spot, m.Bounds) is not null);
        Assert.True(spot.Right <= 0 || spot.Top >= 1080 || spot.Bottom <= 0, spot.ToString()); // 左・下・上のどれか（右は High がある）
        Assert.Equal(600, spot.Width);

        var spotHigh = DragGeometry.ParkingSpot(RectI.FromXywh(2500, 200, 600, 400), High, Both)!.Value;
        Assert.True(spotHigh.Left >= High.Bounds.Right || spotHigh.Top >= High.Bounds.Bottom || spotHigh.Bottom <= 0, spotHigh.ToString());
    }

    [Fact]
    public void PreCompensate_ScalesByDpiRatio()
    {
        var t = RectI.FromXywh(2000, 100, 1000, 600);
        Assert.Equal(RectI.FromXywh(2000, 100, 500, 300), DragGeometry.PreCompensate(t, 96, 192));
        Assert.Equal(t, DragGeometry.PreCompensate(t, 144, 144));
    }

    [Fact]
    public void Snap_OnlyOnOuterEdges()
    {
        Assert.Equal(SnapZone.Maximize, DragGeometry.DetectSnap((500, 0), Low, Both));
        Assert.Equal(SnapZone.LeftHalf, DragGeometry.DetectSnap((0, 500), Low, Both));
        Assert.Equal(SnapZone.None, DragGeometry.DetectSnap((1919, 500), Low, Both)); // 右隣にモニターがある
        Assert.Equal(SnapZone.RightHalf, DragGeometry.DetectSnap((High.Bounds.Right - 1, 500), High, Both));
        Assert.Equal(new RectI(1920, 0, 1920 + 1368, 1776), DragGeometry.SnapRect(SnapZone.LeftHalf, High.WorkArea));
    }

    [Fact]
    public void FitInto_ShrinksAndClamps()
    {
        var r = DragGeometry.FitInto(RectI.FromXywh(1800, -50, 3000, 600), High.WorkArea);
        Assert.Equal(2736, r.Width);
        Assert.Equal(High.WorkArea.Left, r.Left);
        Assert.Equal(0, r.Top);
    }
}

public class CushionTests
{
    private static MonitorGeometry Low => new()
    {
        Id = "low", Bounds = new RectI(0, 0, 1920, 1080), WorkArea = new RectI(0, 0, 1920, 1032), Dpi = 96, CalibratedPxPerMm = 4,
    };
    private static MonitorGeometry High => new()
    {
        Id = "high", Bounds = new RectI(1920, 0, 4656, 1824), WorkArea = new RectI(1920, 0, 4656, 1776), Dpi = 192, CalibratedPxPerMm = 10,
    };
    private static readonly MonitorGeometry[] Both = [Low, High];

    [Fact]
    public void CloneStartsBeforeTouchingTheBoundary()
    {
        // 右端が境界まで 100px（= 25mm）手前
        var live = RectI.FromXywh(1020, 100, 800, 600);
        Assert.False(DragGeometry.NeedsClone(live, Low, Both, cushionMm: 0));
        Assert.False(DragGeometry.NeedsClone(live, Low, Both, cushionMm: 20));
        Assert.True(DragGeometry.NeedsClone(live, Low, Both, cushionMm: 40));
    }

    [Fact]
    public void SameLookingMonitorsNeverNeedClone()
    {
        var twin = Low with { Id = "twin", Bounds = new RectI(1920, 0, 3840, 1080) };
        Assert.False(DragGeometry.NeedsClone(RectI.FromXywh(1800, 100, 400, 300), Low, [Low, twin], 40));
    }
}

public class PhysicalAlignmentTests
{
    // 実際の構成: メイン 1920x1080@100%（4.05px/mm）の下に Surface 2736x1824@200%（10.53px/mm）、x=-386 から
    private static MonitorGeometry Main => new()
    {
        Id = "main", IsPrimary = true, Bounds = new RectI(0, 0, 1920, 1080), WorkArea = new RectI(0, 0, 1920, 1032), Dpi = 96, CalibratedPxPerMm = 4.05,
    };
    private static MonitorGeometry Surface => new()
    {
        Id = "surface", Bounds = new RectI(-386, 1080, 2350, 2904), WorkArea = new RectI(-386, 1080, 2350, 2856), Dpi = 192, CalibratedPxPerMm = 10.53,
    };
    private static readonly MonitorGeometry[] Both = [Main, Surface];

    [Fact]
    public void CentersAreAlignedByDefault()
    {
        var map = DragGeometry.PhysicalMap(Main, Surface, 0, 0)!;
        var (x, y) = map(Main.Bounds.CenterX, 1080);
        Assert.Equal(Surface.Bounds.CenterX, x, 3);
        Assert.Equal(1080, y, 3);
        // メインで 100px（約24.7mm）右 → Surface で約 260px 右
        Assert.Equal(Surface.Bounds.CenterX + 100 / 4.05 * 10.53, map(Main.Bounds.CenterX + 100, 1080).X, 3);
    }

    [Fact]
    public void Pieces_DoNotDependOnCursorPosition()
    {
        var rect = RectI.FromXywh(600, 700, 800, 600); // 下端がメインからはみ出している
        var a = DragGeometry.Pieces(rect, Main, (700, 1000), Both);
        var b = DragGeometry.Pieces(rect, Main, (1300, 1000), Both);
        Assert.Equal(a.Select(p => p.Dest), b.Select(p => p.Dest));
        var sp = a.Single(p => p.Monitor.Id == "surface");
        var mp = a.Single(p => p.Monitor.Id == "main");
        Assert.Equal(1080, mp.Dest.Bottom);
        Assert.Equal(1080, sp.Dest.Top);
        Assert.Equal(mp.SrcBottom, sp.SrcTop, 3); // 境目で絵がつながる
        // 物理的な左端が一致（メイン側の左端の mm 位置 = Surface 側の左端の mm 位置）
        double mainLeftMm = (mp.Dest.Left - Main.Bounds.CenterX) / 4.05;
        double surfLeftMm = (sp.Dest.Left - Surface.Bounds.CenterX) / 10.53;
        Assert.InRange(surfLeftMm - mainLeftMm, -0.2, 0.2);
    }

    [Fact]
    public void OffsetShiftsAlongTheEdge()
    {
        // Surface が実際には 10mm 右にある → 同じ物理位置は Surface 上で 10mm 左
        var r = DragGeometry.MapRect(RectI.FromXywh(900, 1000, 100, 100), Main, Surface, 0, 10)!.Value;
        var r0 = DragGeometry.MapRect(RectI.FromXywh(900, 1000, 100, 100), Main, Surface, 0, 0)!.Value;
        Assert.InRange(r0.Left - r.Left, 104, 107); // 10mm * 10.53px/mm
        // 往復で元に戻る
        var back = DragGeometry.MapRect(r, Surface, Main, 10, 0)!.Value;
        Assert.InRange(Math.Abs(back.Left - 900), 0, 1);
        Assert.InRange(Math.Abs(back.Top - 1000), 0, 1);
    }
}
