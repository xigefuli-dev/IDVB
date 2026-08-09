using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class FullResolutionScaleTests
{
    [Fact]
    public void MatrixContainsEveryRequiredResolutionAndScaleExactlyOnce()
    {
        Assert.Equal(12, DisplayTestMatrix.Profiles.Count);
        Assert.Equal(12, DisplayTestMatrix.Profiles.Distinct().Count());
        Assert.Equal(
            new[] { (1920, 1080), (2560, 1440), (2560, 1600), (3840, 2160) },
            DisplayTestMatrix.Profiles
                .Select(profile => (profile.PixelWidth, profile.PixelHeight))
                .Distinct()
                .ToArray());
        Assert.Equal(
            new[] { 100, 125, 150 },
            DisplayTestMatrix.Profiles
                .Select(profile => profile.ScalePercent)
                .Distinct()
                .ToArray());
    }

    [Theory]
    [MemberData(nameof(DisplayTestMatrix.All), MemberType = typeof(DisplayTestMatrix))]
    public void AlignmentCalibrationMatchesItsExactPhysicalResolutionAndDpi(
        string name,
        int pixelWidth,
        int pixelHeight,
        int scalePercent,
        uint dpi)
    {
        var profile = DisplayTestMatrix.From(
            name,
            pixelWidth,
            pixelHeight,
            scalePercent,
            dpi);
        var signature = profile.CreateSignature();
        var mapId = Guid.NewGuid();
        var mapUpdatedAt = DateTimeOffset.UtcNow;
        var calibration = new MapAlignmentCalibration
        {
            MapId = mapId,
            Floor = "1f",
            MapUpdatedAt = mapUpdatedAt,
            ReferenceWidth = 1600,
            ReferenceHeight = 1200,
            UniformScale = 1.25d,
            RotationDegrees = 0d,
            ClientWidth = signature.ClientWidth,
            ClientHeight = signature.ClientHeight,
            ViewportWidth = signature.ViewportWidth,
            ViewportHeight = signature.ViewportHeight,
            Dpi = signature.Dpi,
            Confidence = 0.91d,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(96u * (uint)scalePercent / 100u, dpi);
        Assert.Equal(pixelWidth, (int)Math.Round(profile.LogicalWidth * profile.ScaleFactor));
        Assert.Equal(pixelHeight, (int)Math.Round(profile.LogicalHeight * profile.ScaleFactor));
        Assert.True(calibration.Matches(mapId, mapUpdatedAt, signature, "1f"));

        var differentScale = DisplayTestMatrix.Profiles.First(candidate =>
            candidate.PixelWidth == pixelWidth
            && candidate.PixelHeight == pixelHeight
            && candidate.Dpi != dpi);
        Assert.Equal(
            MapRecalibrationReason.None,
            MapSessionRules.GetSignatureChangeReason(
                signature,
                differentScale.CreateSignature()));
        Assert.True(calibration.Matches(
            mapId,
            mapUpdatedAt,
            differentScale.CreateSignature(),
            "1f"));

        var differentResolution = DisplayTestMatrix.Profiles.First(candidate =>
            candidate.Dpi == dpi
            && (candidate.PixelWidth != pixelWidth
                || candidate.PixelHeight != pixelHeight));
        Assert.Equal(
            MapRecalibrationReason.ResolutionChanged,
            MapSessionRules.GetSignatureChangeReason(
                signature,
                differentResolution.CreateSignature()));
    }
}
