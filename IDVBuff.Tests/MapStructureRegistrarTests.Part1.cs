using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;
public sealed partial class MapStructureRegistrarTests
{
    [Fact]
    public void ComputationSearchReturnsOriginalPixelTransformAfterFinalValidation()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var original = new Mat(reference, crop).Clone();
        using var computation = new Mat();
        Cv2.Resize(original, computation, new Size(225, 177),
            interpolation: InterpolationFlags.Area);
        var ratio = original.Width / (double)computation.Width;
        var viewport = new MapScreenRect(800d, 420d,
            original.Width, original.Height);
        var expectedOffsetX = viewport.X - crop.X;
        var expectedOffsetY = viewport.Y - crop.Y;
        var tuning = TestTuning();
        tuning.EnableFeatureVoting = false;
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = computation,
            OriginalLiveRoi = original,
            PhysicalPixelsPerLivePixel = ratio,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference,
                expectedOffsetX + 2d, expectedOffsetY - 2d),
            Tuning = tuning,
            ScaleSearchPolicy = MapScaleSearchPolicy.Fixed,
            RestrictSearchToLockedTransform = true,
            TrackingMode = true
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.InRange(Math.Abs(result.Transform.OffsetX - expectedOffsetX), 0d, 2d);
        Assert.InRange(Math.Abs(result.Transform.OffsetY - expectedOffsetY), 0d, 2d);
        Assert.InRange(result.Candidates[0].ChamferPixels, 0d, 3d);
        Assert.False(result.UsedFastStrategy);
    }

    [Fact]
    public void RestrictedSearchRejectsTargetOutsideConfiguredRadius()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(800d, 420d, live.Width, live.Height);
        var expectedOffsetX = viewport.X - crop.X;
        var expectedOffsetY = viewport.Y - crop.Y;
        var tuning = TestTuning();
        tuning.PreviousAlignmentSearchRadiusPixels = 24;
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());
        var request = new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(
                reference,
                offsetX: expectedOffsetX + 180d,
                offsetY: expectedOffsetY + 140d),
            Tuning = tuning,
            AllowScaleSearch = false,
            RestrictSearchToLockedTransform = true
        };

        var local = registrar.Register(request);
        var global = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = request.LockedTransform,
            Tuning = tuning,
            AllowScaleSearch = false
        });

        Assert.False(local.Accepted);
        Assert.True(local.UsedRestrictedSearch);
        Assert.True(global.Accepted, global.FailureReason);
        Assert.NotNull(global.Transform);
        Assert.InRange(Math.Abs(global.Transform.OffsetX - expectedOffsetX), 0d, 2d);
        Assert.InRange(Math.Abs(global.Transform.OffsetY - expectedOffsetY), 0d, 2d);
    }

}
