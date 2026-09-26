using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AutoFloorRegressionTests
{
    [Fact]
    public void FeedbackVideoFirstFloorIsNotMistakenForSecondFloorOnShortWindow()
    {
        // Original pixels from 00:10 of the supplied 1280x656 feedback video.
        // Only the header's rightmost 380 pixels are retained (no player name).
        using var header = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "auto-floor-1280x656-header.png"), ImreadModes.Grayscale);
        Assert.False(header.Empty());
        var group = FloorIndicatorTemplateRegistry.Get("hard")!;
        var before = FloorIndicatorTemplateRegistry.RecognizeDetailed(group, header, 1280d / 1920);
        Assert.Equal("2f", before.DetectedFloor);

        var scale = FloorIndicatorCaptureRegion.TemplateScale(group, new MapScreenRect(0, 0, 1280, 656));
        var after = FloorIndicatorTemplateRegistry.RecognizeDetailed(group, header, scale);
        Assert.Equal("1f", after.DetectedFloor);
        Assert.True(after.BestScore > .95);
        Assert.Equal("1f", FloorRecognitionRules.ResolveTargetFloor(false, "2f", after.DetectedFloor, "1f"));
    }

    [Theory]
    [InlineData(1280, 656, 656d / 1080)]
    [InlineData(2880, 1479, 1479d / 1080)]
    [InlineData(1920, 1080, 1)]
    [InlineData(2560, 1600, 2560d / 1920)]
    [InlineData(3440, 1440, 1440d / 1080)]
    [InlineData(1280, 1024, 1280d / 1920)]
    public void BothLayoutsAndAllFloorsWorkAcrossAspectRatios(int width, int height, double scale)
    {
        foreach (var key in new[] { "hard", "nightmare" })
        {
            var group = FloorIndicatorTemplateRegistry.Get(key)!;
            Assert.Equal(scale, FloorIndicatorCaptureRegion.TemplateScale(group,
                new MapScreenRect(200, 100, width, height)), 6);
            foreach (var (floor, filename) in group.States)
            {
                using var source = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Assets", "FloorIndicators", filename));
                using var scaled = new Mat();
                Cv2.Resize(source, scaled, new Size((int)Math.Round(source.Width * scale),
                    (int)Math.Round(source.Height * scale)), 0, 0, InterpolationFlags.Area);
                using var header = new Mat((int)Math.Round(height / 6d), width, source.Type(), Scalar.All(25));
                using (var roi = new Mat(header, new Rect(width - scaled.Width - 60, 10, scaled.Width, scaled.Height)))
                    scaled.CopyTo(roi);
                header.ConvertTo(header, -1, .7, 12);
                Assert.Equal(floor, FloorIndicatorTemplateRegistry.Recognize(group, header, out _, out _, scale));
            }
        }
    }

    [Theory]
    [InlineData(false, "1f", "2f", "2f")]
    [InlineData(false, "2f", "1f", "1f")]
    [InlineData(false, "2f", null, "2f")]
    [InlineData(false, null, null, "1f")]
    [InlineData(true, "1f", "2f", "1f")]
    [InlineData(true, "2f", "1f", "2f")]
    [InlineData(true, null, "2f", "1f")]
    public void ManualPolicyAndFreshDetectionHaveExplicitPrecedence(
        bool disabled, string? current, string? detected, string expected)
    {
        Assert.Equal(expected, FloorRecognitionRules.ResolveTargetFloor(disabled, current, detected, "1f"));
    }

    [Fact]
    public void DisableAutoFloorDefaultsOffAndSurvivesCloneSaveLoadAndNormalize()
    {
        Assert.False(new MapRuntimeSettings().DisableAutoFloor);
        var settings = new MapRuntimeSettings { DisableAutoFloor = true };
        var clone = settings.Clone();
        clone.Normalize();
        Assert.True(clone.DisableAutoFloor);
        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(JsonSerializer.Serialize(clone))!;
        restored.Normalize();
        Assert.True(restored.DisableAutoFloor);
        restored.DisableAutoFloor = false;
        restored.Normalize();
        Assert.False(restored.DisableAutoFloor);
        Assert.True(settings.DisableAutoFloor);
    }
}
