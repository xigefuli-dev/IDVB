using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase1;

public sealed class MapCanonicalTransformMathTests
{
    private const double Tolerance = 1e-6d;

    [Fact]
    public void ReferenceToScreenAndScreenToReference_RoundTripPrecisely()
    {
        var refX = 350.25d;
        var refY = 720.75d;
        var scale = 1.15d;
        var offsetX = 120.5d;
        var offsetY = -45.25d;

        var (screenX, screenY) = MapCanonicalTransformMath.ReferenceToScreen(
            refX, refY, scale, offsetX, offsetY);

        var (recoveredX, recoveredY) = MapCanonicalTransformMath.ScreenToReference(
            screenX, screenY, scale, offsetX, offsetY);

        Assert.Equal(refX, recoveredX, Tolerance);
        Assert.Equal(refY, recoveredY, Tolerance);
    }

    [Fact]
    public void ComputeScreenOffset_MatchesPhysicalExpectations()
    {
        // Viewport at (100, 200), Query ROI bounds at (50, 60), Logical Reference at (200, 250), Scale 1.2
        var (offsetX, offsetY) = MapCanonicalTransformMath.ComputeScreenOffset(
            viewportX: 100d,
            viewportY: 200d,
            queryBoundsX: 50d,
            queryBoundsY: 60d,
            logicalReferenceX: 200d,
            logicalReferenceY: 250d,
            matchingScale: 1.2d);

        // Expected:
        // OffsetX = 100 + (50 * 1.2) - (200 * 1.2) = 100 + 60 - 240 = -80
        // OffsetY = 200 + (60 * 1.2) - (250 * 1.2) = 200 + 72 - 300 = -28
        Assert.Equal(-80d, offsetX, Tolerance);
        Assert.Equal(-28d, offsetY, Tolerance);
    }

    [Fact]
    public void ComputeActualScale_MultipliesMatchingScaleAndReferenceScale()
    {
        var matchingScale = 1.25d;
        var referenceScale = 0.5d; // 2x downsampled reference
        var actualScale = MapCanonicalTransformMath.ComputeActualScale(matchingScale, referenceScale);

        Assert.Equal(0.625d, actualScale, Tolerance);
    }

    [Fact]
    public void ComputeViewportOrigin_TranslatesScreenViewportToReferenceCoordinates()
    {
        var viewportX = 200d;
        var viewportY = 300d;
        var offsetX = 50d;
        var offsetY = 60d;
        var actualScale = 0.8d;

        var origin = MapCanonicalTransformMath.ComputeViewportOrigin(
            viewportX, viewportY, offsetX, offsetY, actualScale);

        // Expected: (200 - 50) / 0.8 = 150 / 0.8 = 187.5
        // Expected: (300 - 60) / 0.8 = 240 / 0.8 = 300.0
        Assert.Equal(187.5d, origin.X, Tolerance);
        Assert.Equal(300.0d, origin.Y, Tolerance);
    }

    [Fact]
    public void ComputationAndPhysicalTransforms_RoundTripConsistently()
    {
        var original = new MapOverlayTransform
        {
            ScaleX = 0.65d,
            ScaleY = 0.65d,
            OffsetX = 120.4d,
            OffsetY = 240.8d,
            ReferenceCenterX = 600d,
            ReferenceCenterY = 500d,
            ScreenCenterX = (600d * 0.65d) + 120.4d,
            ScreenCenterY = (500d * 0.65d) + 240.8d,
            ReferenceWidth = 1200,
            ReferenceHeight = 1000,
            OrientationDegrees = 0,
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = 4.5d,
            UsedDegenerateAxisFallback = false
        };

        var ratio = 1.5d;
        var physical = MapCanonicalTransformMath.ToPhysicalTransform(original, ratio);

        // Check scaled properties
        Assert.Equal(original.ScaleX * ratio, physical.ScaleX, Tolerance);
        Assert.Equal(original.ScaleY * ratio, physical.ScaleY, Tolerance);
        Assert.Equal(original.OffsetX * ratio, physical.OffsetX, Tolerance);
        Assert.Equal(original.OffsetY * ratio, physical.OffsetY, Tolerance);
        Assert.Equal(original.ScreenCenterX * ratio, physical.ScreenCenterX, Tolerance);
        Assert.Equal(original.ScreenCenterY * ratio, physical.ScreenCenterY, Tolerance);
        Assert.Equal(original.MaximumResidualPixels * ratio, physical.MaximumResidualPixels, Tolerance);

        // Check unscaled reference properties remain invariant
        Assert.Equal(original.ReferenceCenterX, physical.ReferenceCenterX, Tolerance);
        Assert.Equal(original.ReferenceCenterY, physical.ReferenceCenterY, Tolerance);
        Assert.Equal(original.ReferenceWidth, physical.ReferenceWidth);
        Assert.Equal(original.ReferenceHeight, physical.ReferenceHeight);

        // Round trip back to computation
        var restored = MapCanonicalTransformMath.ToComputationTransform(physical, ratio);
        Assert.Equal(original.ScaleX, restored.ScaleX, Tolerance);
        Assert.Equal(original.ScaleY, restored.ScaleY, Tolerance);
        Assert.Equal(original.OffsetX, restored.OffsetX, Tolerance);
        Assert.Equal(original.OffsetY, restored.OffsetY, Tolerance);
        Assert.Equal(original.ScreenCenterX, restored.ScreenCenterX, Tolerance);
        Assert.Equal(original.ScreenCenterY, restored.ScreenCenterY, Tolerance);
        Assert.Equal(original.MaximumResidualPixels, restored.MaximumResidualPixels, Tolerance);
    }

    [Fact]
    public void BuildOverlayTransform_CalculatesCorrectCentersAndProperties()
    {
        var transform = MapCanonicalTransformMath.BuildOverlayTransform(
            scaleX: 1.1d,
            scaleY: 1.1d,
            offsetX: 80d,
            offsetY: 120d,
            referenceWidth: 1000,
            referenceHeight: 800,
            residualPixels: 2.5d);

        Assert.Equal(500d, transform.ReferenceCenterX, Tolerance);
        Assert.Equal(400d, transform.ReferenceCenterY, Tolerance);
        Assert.Equal((500d * 1.1d) + 80d, transform.ScreenCenterX, Tolerance);
        Assert.Equal((400d * 1.1d) + 120d, transform.ScreenCenterY, Tolerance);
        Assert.Equal(2.5d, transform.MaximumResidualPixels, Tolerance);
        Assert.Equal(1000, transform.ReferenceWidth);
        Assert.Equal(800, transform.ReferenceHeight);
    }
}
