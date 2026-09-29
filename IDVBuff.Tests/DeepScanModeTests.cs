using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class DeepScanModeTests
{
    [Fact]
    public void ReservedModeKeepsQualityPolicyAndExistingDeadline()
    {
        var quality = ScanExecutionPolicy.For(ScanPerformanceMode.Quality);
        var deep = ScanExecutionPolicy.For(ScanPerformanceMode.DeepScan);
        Assert.Equal(ScanPerformanceMode.DeepScan, deep.Mode);
        Assert.Equal(quality, deep with { Mode = ScanPerformanceMode.Quality });
        Assert.InRange(deep.BudgetMilliseconds, 1, 1000);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("\"DeepScan\"")]
    public void SavedModeSurvivesReadNormalizeCloneAndWrite(string value)
    {
        var settings = JsonSerializer.Deserialize<MapRuntimeSettings>("{\"ScanPerformanceMode\":" + value + "}")!;
        settings.Normalize();
        var copy = settings.Clone();
        Assert.Equal(ScanPerformanceMode.DeepScan, copy.ScanPerformanceMode);
        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(JsonSerializer.Serialize(copy))!;
        Assert.Equal(ScanPerformanceMode.DeepScan, restored.ScanPerformanceMode);
        Assert.Equal(0, (int)ScanPerformanceMode.Fast);
        Assert.Equal(1, (int)ScanPerformanceMode.Balanced);
        Assert.Equal(2, (int)ScanPerformanceMode.Quality);
    }

    [Fact]
    public void SwitchingModeDoesNotMutateInFlightScanOrReuseQualityFrame()
    {
        var bounds = new MapScreenRect(0, 0, 20, 20);
        using var frame = new CapturedGameFrame(new Mat(20, 20, MatType.CV_8UC3, Scalar.Black),
            bounds, bounds, new IntPtr(1));
        using var cache = new ScanObservationFrameCache();
        cache.Remember(frame, "catalog", ScanPerformanceMode.Quality);
        Assert.False(cache.Matches(frame, "catalog", ScanPerformanceMode.DeepScan));
        using var current = ScanExecutionContext.Enter(ScanPerformanceMode.Quality);
        using (var next = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan))
        {
            Assert.Equal(ScanPerformanceMode.DeepScan, next.Policy.Mode);
            Assert.Equal(ScanPerformanceMode.Quality, current.Policy.Mode);
        }
        Assert.Same(current, ScanExecutionContext.Current);
    }

    [Fact]
    public void DeepScanUsesQualityFullEvidenceVerificationRatherThanEarlyExit()
    {
        using var image = new Mat(600, 800, MatType.CV_8UC3, new Scalar(30, 25, 22));
        Cv2.Rectangle(image, new Rect(80, 130, 200, 140), new Scalar(110, 97, 88), -1);
        Cv2.Rectangle(image, new Rect(390, 120, 250, 200), new Scalar(110, 97, 88), -1);
        var viewport = new MapScreenRect(0, 0, 800, 600);
        using var frame = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(ScanPerformanceMode.DeepScan));
        using var reference = frame.Observation.ObservedEdges.Clone();
        Cv2.Rectangle(reference, new Rect(0, 0, 340, 600), Scalar.Black, -1);
        var index = ScanStructureIndex.Get(reference);
        var transform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1 };
        ScanIdentityEvidence Verify(ScanPerformanceMode mode)
        {
            using var context = ScanExecutionContext.Enter(mode);
            return ScanIdentityVerifier.Verify(frame, index, transform, viewport, context);
        }
        var quality = Verify(ScanPerformanceMode.Quality);
        var deep = Verify(ScanPerformanceMode.DeepScan);
        Assert.Equal(ScanIdentityState.Excluded, deep.State);
        Assert.Equal(quality, deep);
    }
}
