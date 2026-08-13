using System.Reflection;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests;

/// <summary>
/// 验证《待修复清单》中三条 seed 尺度偏差根因的机制层实证。
/// 非正式回归测试：用合成数据确认"互逆缩放丢边缘""门空间残差 scale 抵消"
/// 与"弱模板尺度选择性差"三个 OpenCV/几何行为，支撑根因分析。
/// </summary>
public sealed class ScaleEstimationVerificationTests
{
    private readonly ITestOutputHelper _output;

    public ScaleEstimationVerificationTests(ITestOutputHelper output) => _output = output;

    // ═══════════════════════════════════════════════════════════════
    // 实证 B：互逆缩放用 INTER_AREA 缩二值边缘后未重新二值化，
    //         精确 255 的零距离种子大量丢失（对应清单 P0 第 78 行）。
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ReciprocalScale_AreaResizeOnBinaryEdges_DropsExact255Density()
    {
        // 构造二值结构边缘：1px 细线网格，模拟地图结构参考图（墙线）。
        using var edges = new Mat(1200, 1200, MatType.CV_8UC1, Scalar.All(0));
        for (var i = 0; i < 55; i++)
        {
            var p = 30 + (i * 21);
            Cv2.Line(edges, new Point(0, p), new Point(1199, p), Scalar.All(255), 1);
            Cv2.Line(edges, new Point(p, 0), new Point(p, 1199), Scalar.All(255), 1);
        }

        const double scale = 0.563; // 互逆缩放 baselineScale < 1
        var target = new Size(
            (int)Math.Round(edges.Width * scale),
            (int)Math.Round(edges.Height * scale));

        // 生产实现路径（MapStructureRegistrar.cs:203）使用 INTER_AREA。
        using var areaDs = new Mat();
        Cv2.Resize(edges, areaDs, target, 0d, 0d, InterpolationFlags.Area);

        // 对照：最近邻插值保持二值，代表"重新二值化"后的效果。
        using var nearestDs = new Mat();
        Cv2.Resize(edges, nearestDs, target, 0d, 0d, InterpolationFlags.Nearest);

        var areaExact = CountExact255(areaDs);
        var areaNonZero = Cv2.CountNonZero(areaDs);
        var nearestExact = CountExact255(nearestDs);
        var nearestNonZero = Cv2.CountNonZero(nearestDs);

        var areaFraction = areaExact / (double)Math.Max(1, areaNonZero);
        var nearestFraction = nearestExact / (double)Math.Max(1, nearestNonZero);

        _output.WriteLine(
            $"INTER_AREA: 精确255={areaExact}/{areaNonZero} ({areaFraction:P1}), "
            + $"NEAREST: 精确255={nearestExact}/{nearestNonZero} ({nearestFraction:P1})");

        Assert.True(
            areaFraction < 0.5d,
            $"INTER_AREA 缩小后精确 255 占比 {areaFraction:P1}，"
            + "细边缘应被抗锯齿成灰度（非二值），而非保留为可作零距离种子的 255。");
        Assert.True(
            areaFraction < nearestFraction * 0.7d,
            $"INTER_AREA 精确 255 占比 {areaFraction:P1} 应显著低于最近邻 "
            + $"{nearestFraction:P1}，证明丢边缘源于 INTER_AREA 而非缩放本身。");
    }

    // ═══════════════════════════════════════════════════════════════
    // 实证 C：CalculateGateResidual 在"特征中心 == anchor 中心"时
    //         scale 项被精确抵消，残差退化为匹配框中心 vs 门中心。
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CalculateGateResidual_WhenFeatureCenterEqualsAnchorCenter_IgnoresScale()
    {
        var map = CreateMapWithFeatureCenterAtAnchor();
        var method = typeof(SideEntranceScanPipeline).GetMethod(
            "CalculateGateResidual",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // 门放在一个与匹配框中心有固定偏移的位置，让残差非零，
        // 从而能观察到"残差是否随 scale 变化"。
        var gate = new GateDetection
        {
            Score = 0.9d,
            Scale = 1d,
            ScreenBounds = new MapScreenRect(350d, 190d, 20d, 20d) // 中心 (360, 200)
        };
        var viewport = new MapScreenRect(0d, 0d, 800d, 600d);

        // 两个候选：匹配框中心相同 (320, 170)，但 MatchScale 与框尺寸成比例。
        // 这等价于"同一特征被扫出 scale=1.0 或 scale=2.0，匹配位置一致"。
        var scale1 = new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = "1f",
            MatchScale = 1.0d,
            MatchLocation = new MapScreenRect(300d, 150d, 40d, 40d) // 中心 (320,170)
        };
        var scale2 = new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = "1f",
            MatchScale = 2.0d,
            MatchLocation = new MapScreenRect(280d, 130d, 80d, 80d) // 中心 (320,170)
        };

        var residual1 = (double)method.Invoke(
            null, [scale1, gate, viewport])!;
        var residual2 = (double)method.Invoke(
            null, [scale2, gate, viewport])!;

        _output.WriteLine(
            $"scale=1.0 残差={residual1:F4}px, scale=2.0 残差={residual2:F4}px");

        // 若门空间残差真在验证 scale，residual2 应显著偏离 residual1。
        // 实测二者相等，说明 scale 被精确抵消，残差只反映中心偏移。
        Assert.Equal(residual1, residual2, 6);
    }

    // ═══════════════════════════════════════════════════════════════
    // 实证 A：弱模板（简单直角/走廊）在多尺度 CCoeffNormed 匹配下
    //         尺度响应比富纹理模板更平坦 → 尺度选择性差，易产生伪峰。
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WeakTemplate_HasFlatterScaleResponseThanRichTemplate()
    {
        const int templateSize = 128;
        const int coarseFactor = 4;
        var scales = new[]
        {
            0.55d, 0.60d, 0.66d, 0.73d, 0.80d, 0.88d,
            0.97d, 1.00d, 1.07d, 1.17d, 1.29d, 1.42d
        };

        var weakFlatness = MeasureFlatness(
            BuildWeakTemplate(templateSize), templateSize, coarseFactor, scales);
        var richFlatness = MeasureFlatness(
            BuildRichTemplate(templateSize, seed: 41), templateSize, coarseFactor, scales);

        _output.WriteLine(
            $"弱模板 flatness={weakFlatness:F3}, 富纹理模板 flatness={richFlatness:F3}");

        // 弱模板在远离正确 scale 处相对峰值保留更多分数 → flatness 更大。
        Assert.True(
            weakFlatness > richFlatness,
            $"弱模板尺度响应平坦度 {weakFlatness:F3} 应大于富纹理模板 "
            + $"{richFlatness:F3}：弱模板尺度选择性更差，更易产生伪峰。");
    }

    private static double MeasureFlatness(
        Mat template,
        int templateSize,
        int coarseFactor,
        double[] scales)
    {
        using var frame = BuildRichTemplate(700, seed: 7);
        using var planted = new Mat(frame, new Rect(210, 150, templateSize, templateSize));
        template.CopyTo(planted);

        var response = ScaleResponse(frame, template, scales, coarseFactor);
        var peak = response[Array.IndexOf(scales, 1.0d)];
        var low = response[0]; // scale = 0.55（靠近下边界）
        return low / Math.Max(0.001d, peak);
    }

    private static double[] ScaleResponse(
        Mat frame,
        Mat template,
        double[] scales,
        int coarseFactor)
    {
        using var coarse = new Mat();
        Cv2.Resize(
            frame,
            coarse,
            new Size(frame.Width / coarseFactor, frame.Height / coarseFactor),
            0d, 0d, InterpolationFlags.Area);

        return scales.Select(scale =>
        {
            var w = (int)Math.Round(template.Width * scale / coarseFactor);
            var h = (int)Math.Round(template.Height * scale / coarseFactor);
            if (w < 8 || h < 8 || w >= coarse.Width || h >= coarse.Height)
                return double.NaN;

            using var scaled = new Mat();
            Cv2.Resize(template, scaled, new Size(w, h), 0d, 0d, InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(coarse, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out _);
            return maxVal;
        }).ToArray();
    }

    private static int CountExact255(Mat mat)
    {
        using var exact = new Mat();
        Cv2.Compare(mat, Scalar.All(255), exact, CmpTypes.EQ);
        return Cv2.CountNonZero(exact);
    }

    private static MapRecord CreateMapWithFeatureCenterAtAnchor()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow,
            Floors =
            [
                new FloorDefinition
                {
                    Key = "1f",
                    DisplayName = "1F",
                    SortOrder = 1
                }
            ],
            Recognition = new MapRecognitionProfile
            {
                FirstFloor = new FloorRecognitionProfile
                {
                    FloorKey = "1f",
                    RecognitionPixelWidth = 1000,
                    RecognitionPixelHeight = 800
                }
            }
        };
        map.NormalizeRecognition();

        var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
        profile.SideEntranceFeatureCenterX = 200d;
        profile.SideEntranceFeatureCenterY = 300d;
        // anchor 归一化中心 = (200/1000, 300/800) = (0.2, 0.375)
        profile.FindAnchor("side-entrance")!.Bounds = new NormalizedRectangle
        {
            X = 0.18d,
            Y = 0.355d,
            Width = 0.04d,
            Height = 0.04d
        };
        return map;
    }

    private static Mat BuildWeakTemplate(int size)
    {
        var template = new Mat(size, size, MatType.CV_8UC1, Scalar.All(128));
        // 简单"直角 + 走廊"结构：两条粗走廊正交，低空间频率、少量边缘。
        var thick = size / 8;
        Cv2.Rectangle(
            template,
            new Rect(thick, thick, size - (2 * thick), thick),
            Scalar.All(200), -1);
        Cv2.Rectangle(
            template,
            new Rect(thick, thick, thick, size - (2 * thick)),
            Scalar.All(200), -1);
        return template;
    }

    private static Mat BuildRichTemplate(int size, int seed)
    {
        var template = new Mat(size, size, MatType.CV_8UC1, Scalar.All(128));
        var random = new Random(seed);
        for (var index = 0; index < 60; index++)
        {
            var rectWidth = random.Next(size / 10, size / 3);
            var rectHeight = random.Next(size / 10, size / 3);
            var rect = new Rect(
                random.Next(0, Math.Max(1, size - rectWidth)),
                random.Next(0, Math.Max(1, size - rectHeight)),
                rectWidth,
                rectHeight);
            Cv2.Rectangle(
                template,
                rect,
                Scalar.All(random.Next(0, 256)),
                thickness: -1);
        }
        return template;
    }
}
