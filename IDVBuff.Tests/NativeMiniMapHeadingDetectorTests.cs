using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class NativeMiniMapHeadingDetectorTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 90, 90)]
    [InlineData(90, 0, -90)]
    [InlineData(350, 10, 20)]
    [InlineData(10, 350, -20)]
    [InlineData(180, 0, -180)]
    [InlineData(0, 180, -180)]
    [InlineData(270, 90, -180)]
    public void ShortestDelta_CalculatesCorrectAngleDifferences(double from, double to, double expected)
    {
        var delta = NativeMiniMapHeadingDetector.ShortestDelta(from, to);
        Assert.Equal(expected, delta, 2);
    }

    [Fact]
    public void EmptyOrInvalidImage_ReturnsNull()
    {
        using var detector = new NativeMiniMapHeadingDetector();

        using var empty = new Mat();
        var resultEmpty = detector.Detect(empty, out var reasonEmpty);
        Assert.Null(resultEmpty);
        Assert.Equal("marker_not_found", reasonEmpty);

        using var singleChannel = new Mat(100, 100, MatType.CV_8UC1, new Scalar(0));
        var resultSingle = detector.Detect(singleChannel, out var reasonSingle);
        Assert.Null(resultSingle);
        Assert.Equal("marker_not_found", reasonSingle);
    }

    [Fact]
    public void SolidColorImage_ReturnsMarkerNotFound()
    {
        using var detector = new NativeMiniMapHeadingDetector();
        using var image = new Mat(150, 150, MatType.CV_8UC3, new Scalar(30, 30, 30));

        var result = detector.Detect(image, out var reason);
        Assert.Null(result);
        Assert.Equal("marker_not_found", reason);
    }

    [Fact]
    public void RealDiagnosticImages_ProducesAccurateAndStableHeadings()
    {
        var diagDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVB", "诊断模式", "对局 2", "原生小地图");
        if (!Directory.Exists(diagDir)) return;

        using var detector = new NativeMiniMapHeadingDetector();
        var files = Directory.GetFiles(diagDir, "*.png")
            .Where(f => !f.EndsWith("_heading.png", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0) return;

        var detectedCount = 0;
        double? straightRun010408 = null;
        double? straightRun010418 = null;
        double? straightRun010423 = null;

        foreach (var file in files)
        {
            using var img = Cv2.ImRead(file, ImreadModes.Color);
            if (img.Empty()) continue;

            var res = detector.Detect(img, out var reason);
            Assert.NotNull(res);
            Assert.Equal("accepted", reason);
            Assert.True(res.Confidence >= 0.5);
            Assert.True(res.ConeContrast >= 5.0);
            detectedCount++;

            var filename = Path.GetFileName(file);
            if (filename.Contains("010408")) straightRun010408 = res.Degrees;
            else if (filename.Contains("010418")) straightRun010418 = res.Degrees;
            else if (filename.Contains("010423")) straightRun010423 = res.Degrees;
        }

        Assert.Equal(files.Count, detectedCount);

        // Verify straight running stability: headings during straight run must not deviate
        if (straightRun010408 is { } h1 && straightRun010418 is { } h2 && straightRun010423 is { } h3)
        {
            Assert.True(Math.Abs(NativeMiniMapHeadingDetector.ShortestDelta(h1, h2)) < 2.0);
            Assert.True(Math.Abs(NativeMiniMapHeadingDetector.ShortestDelta(h2, h3)) < 2.0);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 0)]
    [InlineData(350, 0)]
    [InlineData(40, 45)]
    [InlineData(50, 45)]
    [InlineData(89, 90)]
    [InlineData(136, 135)]
    [InlineData(182, 180)]
    [InlineData(220, 225)]
    [InlineData(273, 270)]
    [InlineData(318, 315)]
    public void SnapTo8Directions_SnapsToClosestOctant_WhenNoPreviousSnapped(double angle, double expected)
    {
        var snapped = NativeMiniMapHeadingDetector.SnapTo8Directions(angle);
        Assert.Equal(expected, snapped);
    }

    [Fact]
    public void SnapTo8Directions_AppliesHysteresis_PreventsJitterAtBoundaries()
    {
        // 初始吸附在 0°
        double? current = 0.0;

        // 在 22.5° 正常分界点附近微晃（23°、25°），由于默认 4.0° 迟滞（上限 26.5°），保持 0°
        Assert.Equal(0.0, NativeMiniMapHeadingDetector.SnapTo8Directions(23.0, current));
        Assert.Equal(0.0, NativeMiniMapHeadingDetector.SnapTo8Directions(25.0, current));

        // 超过迟滞死区（27°），成功切换到 45°
        current = NativeMiniMapHeadingDetector.SnapTo8Directions(27.0, current);
        Assert.Equal(45.0, current);

        // 切换到 45° 后，微晃回 23°，由于 45° 的维持范围为 45 ± 26.5° (18.5° ~ 71.5°)，保持 45° 不退回 0°
        Assert.Equal(45.0, NativeMiniMapHeadingDetector.SnapTo8Directions(23.0, current));
    }

    [Fact]
    public void RealSession1_CorrectlyLocksPlayer3AndRejectsTeammates()
    {
        var diagDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVB", "诊断模式", "对局 1", "原生小地图");
        if (!Directory.Exists(diagDir)) return;

        var files = Directory.GetFiles(diagDir, "*.png")
            .Where(f => !f.EndsWith("_heading.png", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0) return;

        using var detector = new NativeMiniMapHeadingDetector();
        var acceptedCount = 0;
        foreach (var file in files)
        {
            using var img = Cv2.ImRead(file, ImreadModes.Color);
            if (img.Empty()) continue;

            var result = detector.Detect(img, out var reason);
            if (result is not null)
            {
                Assert.Equal("accepted", reason);
                // 必须且只能是 3 号玩家（用户自身），绝不能被 4 号队友或 2 号抢走
                Assert.Equal(PlayerSlot.Player3, result.Slot);
                Assert.InRange(result.Confidence, 0.40, 1.0);
                acceptedCount++;
            }
        }

        // 验证整局比赛中绝大多数帧均能成功检出（>= 90% 覆盖率）
        Assert.True(acceptedCount >= (int)(files.Count * 0.90), $"检出率不足：{acceptedCount}/{files.Count}");
    }
}
