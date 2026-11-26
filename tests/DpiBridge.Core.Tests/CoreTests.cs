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
