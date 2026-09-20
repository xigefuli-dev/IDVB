using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapStructureCheapRejectTests
{
    private const double ComputationRatio = 1.3160518d;
    private const double PhysicalSeedScale = 0.9910067d;

    [Fact]
    public void CheapRejectUsesSameComputationSpaceAsFormalRegistrar()
    {
        using var referenceImage = CreateStructureImage(1333, 1046);
        using var computationImage = new Mat();
        Cv2.Resize(
            referenceImage,
            computationImage,
            new Size(1003, 788),
            interpolation: InterpolationFlags.Nearest);
        using var originalLiveRoi = new Mat(
            1037,
            1320,
            MatType.CV_8UC1,
            Scalar.All(0));
        using var reference = CreateImageFeatures(referenceImage);
        using var live = CreateImageFeatures(computationImage);
        var request = new MapStructureRegistrationRequest
        {
            ReferenceImage = referenceImage,
            LiveRoi = computationImage,
            OriginalLiveRoi = originalLiveRoi,
            PhysicalPixelsPerLivePixel = ComputationRatio,
            ViewportBounds = new MapScreenRect(0d, 0d, 1320d, 1037d),
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = PhysicalSeedScale,
                ScaleY = PhysicalSeedScale,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            Tuning = new MapStructureRegistrationTuning
            {
                PreviousAlignmentSearchRadiusPixels = 8
            }
        };

        var computationRequest = MapStructureRequestSpace.ToComputationSpace(
            request,
            ComputationRatio);
        var rejected = MapStructureCheapReject.TryReject(
            computationRequest,
            reference,
            live,
            out _,
            out var reason);

        Assert.Equal(
            PhysicalSeedScale / ComputationRatio,
            computationRequest.LockedTransform.ScaleX,
            precision: 6);
        Assert.False(rejected, reason);
    }

    [Fact]
    public void ComputationNormalizedRequestMatchesRegistrarCoarseSeed()
    {
        using var live = new Mat(788, 1003, MatType.CV_8UC1, Scalar.All(0));
        using var original = new Mat(1037, 1320, MatType.CV_8UC1, Scalar.All(0));
        var request = new MapStructureRegistrationRequest
        {
            LiveRoi = live,
            OriginalLiveRoi = original,
            PhysicalPixelsPerLivePixel = ComputationRatio,
            ViewportBounds = new MapScreenRect(100d, 200d, 1320d, 1037d),
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = PhysicalSeedScale,
                ScaleY = PhysicalSeedScale,
                OffsetX = 640d,
                OffsetY = 480d,
                ScreenCenterX = 660d,
                ScreenCenterY = 518.5d,
                ReferenceCenterX = 500d,
                ReferenceCenterY = 400d,
                ReferenceWidth = 1333,
                ReferenceHeight = 1046,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            CandidateHistory =
            [
                new MapSimilarityTransform
                {
                    Scale = PhysicalSeedScale,
                    TranslationX = 640d,
                    TranslationY = 480d
                }
            ],
            DynamicIgnoreRegions = [new Rect(660, 500, 100, 80)],
            LowStructurePlan = new LowStructureAlignmentPlan(
                LowStructureAlignmentRoute.ShapeSeed,
                [PhysicalSeedScale],
                TranslationTopK: 1,
                BudgetMilliseconds: 100,
                CanDirectAccept: false,
                RecoveryBatch: 0,
                RecoveryTotalScaleCount: 1)
        };

        var computationRequest = MapStructureRequestSpace.ToComputationSpace(
            request,
            ComputationRatio);
        var expectedScale = PhysicalSeedScale / ComputationRatio;

        Assert.Equal(expectedScale, computationRequest.LockedTransform.ScaleX, 6);
        Assert.Equal(100d / ComputationRatio, computationRequest.ViewportBounds.X, 6);
        Assert.Equal(1320d / ComputationRatio, computationRequest.ViewportBounds.Width, 6);
        Assert.Equal(640d / ComputationRatio, computationRequest.LockedTransform.OffsetX, 6);
        Assert.Equal(660d / ComputationRatio, computationRequest.LockedTransform.ScreenCenterX, 6);
        Assert.Equal(expectedScale, computationRequest.CandidateHistory[0].Scale, 6);
        Assert.Equal(
            (int)Math.Round(660d / ComputationRatio),
            computationRequest.DynamicIgnoreRegions[0].X);
        Assert.Equal(
            (int)Math.Round(500d / ComputationRatio),
            computationRequest.DynamicIgnoreRegions[0].Y);
        Assert.Equal(expectedScale, computationRequest.LowStructurePlan!.Scales[0], 6);
        Assert.Equal(1d, computationRequest.PhysicalPixelsPerLivePixel);
        Assert.Null(computationRequest.OriginalLiveRoi);
    }

    [Fact]
    public void MatchingSeedPassesCheapRejectWithinBudget()
    {
        using var reference = CreateFeatures(10, 10);
        using var live = CreateFeatures(10, 10);
        using var liveImage = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        var rejected = MapStructureCheapReject.TryReject(
            CreateRequest(liveImage),
            reference,
            live,
            out var elapsedMilliseconds,
            out var reason);

        Assert.False(rejected, reason);
        Assert.InRange(elapsedMilliseconds, 0d, 50d);
    }

    [Fact]
    public void DistantSeedIsRejectedBeforeFormalRegistration()
    {
        using var reference = CreateFeatures(70, 70);
        using var live = CreateFeatures(10, 10);
        using var liveImage = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        var rejected = MapStructureCheapReject.TryReject(
            CreateRequest(liveImage),
            reference,
            live,
            out var elapsedMilliseconds,
            out var reason);

        Assert.True(rejected, reason);
        Assert.Contains("cheap-reject", reason);
        Assert.InRange(elapsedMilliseconds, 0d, 50d);
    }

    [Fact]
    public void OnePixelMatchingEdgesMustNeverBeCheapRejected()
    {
        using var reference = CreateSparseFeatures(12, 16);
        using var live = CreateSparseFeatures(12, 16);
        using var liveImage = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        var rejected = MapStructureCheapReject.TryReject(
            CreateRequest(liveImage),
            reference,
            live,
            out _,
            out var reason);

        Assert.False(rejected, reason);
    }

    [Fact]
    public void SparseMatchingStructureMustReachFormalRegistrar()
    {
        using var reference = CreateSparseFeatures(12, 16);
        using var live = CreateSparseFeatures(12, 16);
        using var liveImage = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        var rejected = MapStructureCheapReject.TryReject(
            CreateRequest(liveImage),
            reference,
            live,
            out _,
            out var reason);

        Assert.False(rejected, reason);
    }

    [Fact]
    public void BrokenDiagonalAndAntialiasedMatchingEdgesMustPass()
    {
        using var reference = CreateIrregularSparseFeatures();
        using var live = CreateIrregularSparseFeatures();
        using var liveImage = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        var rejected = MapStructureCheapReject.TryReject(
            CreateRequest(liveImage),
            reference,
            live,
            out _,
            out var reason);

        Assert.False(rejected, reason);
    }

    private static MapStructureRegistrationRequest CreateRequest(Mat live) => new()
    {
        LiveRoi = live,
        ViewportBounds = new MapScreenRect(0d, 0d, 128d, 128d),
        LockedTransform = new MapOverlayTransform
        {
            ScaleX = 1d,
            ScaleY = 1d,
            OffsetX = 0d,
            OffsetY = 0d,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        },
        Tuning = new MapStructureRegistrationTuning
        {
            PreviousAlignmentSearchRadiusPixels = 8
        }
    };

    private static MapStructureFeatures CreateFeatures(int x, int y)
    {
        var edges = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(
            edges,
            new Rect(x, y, 48, 36),
            Scalar.All(255),
            thickness: 2);
        Cv2.Line(
            edges,
            new Point(x, y + 60),
            new Point(x + 80, y + 60),
            Scalar.All(255),
            thickness: 2);
        return new MapStructureFeatures(
            new Mat(edges.Size(), MatType.CV_8UC1, Scalar.All(0)),
            edges.Clone(),
            edges);
    }

    private static MapStructureFeatures CreateImageFeatures(Mat image)
    {
        var edges = image.Clone();
        return new MapStructureFeatures(
            new Mat(edges.Size(), MatType.CV_8UC1, Scalar.All(0)),
            edges.Clone(),
            edges);
    }

    private static Mat CreateStructureImage(int width, int height)
    {
        var image = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(
            image,
            new Rect(width / 8, height / 8, width / 3, height / 3),
            Scalar.All(255),
            thickness: 3);
        Cv2.Line(
            image,
            new Point(width / 8, height * 2 / 3),
            new Point(width * 3 / 4, height * 2 / 3),
            Scalar.All(255),
            thickness: 3);
        Cv2.Line(
            image,
            new Point(width / 2, height / 5),
            new Point(width * 3 / 5, height * 4 / 5),
            Scalar.All(255),
            thickness: 3);
        return image;
    }

    private static MapStructureFeatures CreateSparseFeatures(int x, int y)
    {
        var edges = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(
            edges,
            new Rect(x, y, 48, 36),
            Scalar.All(255),
            thickness: 1);
        Cv2.Line(
            edges,
            new Point(x, y + 60),
            new Point(x + 80, y + 60),
            Scalar.All(255),
            thickness: 1);
        Cv2.Line(
            edges,
            new Point(x + 8, y + 8),
            new Point(x + 40, y + 28),
            Scalar.All(255),
            thickness: 1);
        return new MapStructureFeatures(
            new Mat(edges.Size(), MatType.CV_8UC1, Scalar.All(0)),
            edges.Clone(),
            edges);
    }

    private static MapStructureFeatures CreateIrregularSparseFeatures()
    {
        var edges = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Line(edges, new Point(12, 16), new Point(42, 16),
            Scalar.All(96), 1, LineTypes.AntiAlias);
        Cv2.Line(edges, new Point(12, 16), new Point(12, 48),
            Scalar.All(255), 1);
        Cv2.Line(edges, new Point(20, 28), new Point(40, 44),
            Scalar.All(255), 1);
        Cv2.Line(edges, new Point(52, 60), new Point(62, 60),
            Scalar.All(255), 1);
        Cv2.Line(edges, new Point(68, 60), new Point(80, 60),
            Scalar.All(255), 1);
        return new MapStructureFeatures(
            new Mat(edges.Size(), MatType.CV_8UC1, Scalar.All(0)),
            edges.Clone(),
            edges);
    }
}
