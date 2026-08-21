using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

/// <summary>
/// EvaluateReady（仅对齐「相似度即走」画面就绪判定）测试：
/// 就绪阈值高于存在检测阈值，应接受已落定的地图帧、拒绝游戏画面帧与动画过渡帧。
/// </summary>
public sealed class MapViewportPresenceDetectorReadyTests
{
    [Fact]
    public void EvaluateReadyAcceptsSettledReferenceFrame()
    {
        using var referenceFrame = CreateHsvFrame(new Scalar(108, 100, 50));
        using var settled = CreateHsvFrame(new Scalar(108, 100, 50));
        var reference = MapViewportPresenceDetector.CreateSignature(referenceFrame);

        var result = MapViewportPresenceDetector.EvaluateReady(settled, reference);

        Assert.True(result.IsPresent);
        Assert.Equal("reference-hsv", result.Mode);
        Assert.True(
            result.Score
                >= MapViewportPresenceDetector.MinimumReadyReferenceSimilarity);
    }

    [Fact]
    public void EvaluateReadyRejectsDissimilarFrameWithReference()
    {
        using var referenceFrame = CreateHsvFrame(new Scalar(108, 100, 50));
        using var gameplay = CreateHsvFrame(new Scalar(15, 170, 100));
        var reference = MapViewportPresenceDetector.CreateSignature(referenceFrame);

        var result = MapViewportPresenceDetector.EvaluateReady(gameplay, reference);

        Assert.False(result.IsPresent);
        Assert.True(
            result.Score
                < MapViewportPresenceDetector.MinimumReadyReferenceSimilarity);
    }

    [Fact]
    public void EvaluateReadyUsesStricterBlueGrayThresholdWithoutReference()
    {
        // 70% 蓝灰 + 30% 棕褐：BlueGrayFraction ≈ 0.70，落在存在检测（0.60）与
        // 就绪检测（0.85）之间——证明就绪判定比存在判定更严。两帧完全一致，
        // 明度稳定性门槛不会掩盖这次断言要验证的占比门槛差异。
        using var partialMap = CreatePartialBlueGrayFrame();

        var presence = MapViewportPresenceDetector.Evaluate(partialMap);
        var ready = MapViewportPresenceDetector.EvaluateReady(
            partialMap,
            previousFrame: MapViewportPresenceDetector.CreateSignature(partialMap));

        Assert.True(presence.IsPresent);
        Assert.False(ready.IsPresent);
        Assert.Equal("blue-gray-fallback", presence.Mode);
        Assert.Equal("blue-gray-fallback", ready.Mode);
    }

    [Fact]
    public void EvaluateReadyRejectsFirstFrameBlueGrayFallbackWithoutPreviousFrame()
    {
        // 无参考签名且无上一帧可比——即使当前帧蓝灰占比 100%，也不能单帧放行，
        // 必须等到第二帧才能确认明度一致。
        using var map = CreateHsvFrame(new Scalar(108, 100, 50));

        var result = MapViewportPresenceDetector.EvaluateReady(map, reference: null);

        Assert.False(result.IsPresent);
        Assert.Equal("blue-gray-fallback", result.Mode);
    }

    [Fact]
    public void EvaluateReadyAcceptsBlueGrayFallbackAfterTwoConsistentFrames()
    {
        using var map = CreateHsvFrame(new Scalar(108, 100, 50));
        var previous = MapViewportPresenceDetector.CreateSignature(map);

        var result = MapViewportPresenceDetector.EvaluateReady(
            map,
            reference: null,
            previousFrame: previous);

        Assert.True(result.IsPresent);
        Assert.Equal("blue-gray-fallback", result.Mode);
    }

    [Fact]
    public void EvaluateReadyRejectsBlueGrayFallbackWhenDimmingBetweenFrames()
    {
        // 复现原始漏洞：BlueGrayFraction 对等比例调暗天然不敏感（色相/饱和度
        // 在均匀缩放下不变），若不比较相邻帧明度，淡入动画帧会被误判就绪。
        using var settled = CreateHsvFrame(new Scalar(108, 100, 50));
        using var darkened = new Mat();
        settled.ConvertTo(darkened, MatType.CV_8UC3, 0.4, 0);
        var previous = MapViewportPresenceDetector.CreateSignature(settled);

        var result = MapViewportPresenceDetector.EvaluateReady(
            darkened,
            reference: null,
            previousFrame: previous);

        Assert.False(result.IsPresent);
        Assert.Equal("blue-gray-fallback", result.Mode);
        // BlueGrayFraction 本身确实未受影响（证明漏洞根因），IsPresent 仍被
        // 明度一致性门槛挡住。
        Assert.True(
            result.BlueGrayFraction
                >= MapViewportPresenceDetector.MinimumReadyBlueGrayFraction);
    }

    [Fact]
    public void EvaluateReadyRejectsAnimationStyleFrame()
    {
        using var referenceFrame = CreateHsvFrame(new Scalar(108, 100, 50));
        using var settled = CreateHsvFrame(new Scalar(108, 100, 50));
        using var darkened = new Mat();
        settled.ConvertTo(darkened, MatType.CV_8UC3, 0.4, 0);
        var reference = MapViewportPresenceDetector.CreateSignature(referenceFrame);

        var settledResult = MapViewportPresenceDetector.EvaluateReady(
            settled,
            reference);
        var animationResult = MapViewportPresenceDetector.EvaluateReady(
            darkened,
            reference);

        Assert.True(settledResult.IsPresent);
        Assert.True(
            settledResult.Score
                >= MapViewportPresenceDetector.MinimumReadyReferenceSimilarity);
        // 动画过渡帧（调暗模拟 fade-in）：HSV 直方图不感知明度，相似度可能仍高，
        // 但明度一致性检查必须将其按未就绪拒绝。
        Assert.False(animationResult.IsPresent);
        Assert.Equal("reference-hsv", animationResult.Mode);
    }

    private static Mat CreateHsvFrame(Scalar hsvColor)
    {
        using var hsv = new Mat(
            new Size(320, 200),
            MatType.CV_8UC3,
            hsvColor);
        var bgr = new Mat();
        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
        return bgr;
    }

    private static Mat CreatePartialBlueGrayFrame()
    {
        using var hsv = new Mat(
            new Size(320, 200),
            MatType.CV_8UC3,
            new Scalar(108, 100, 50));
        Cv2.Rectangle(
            hsv,
            new Rect(224, 0, 96, 200),
            new Scalar(15, 170, 100),
            -1);
        var bgr = new Mat();
        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
        return bgr;
    }
}
