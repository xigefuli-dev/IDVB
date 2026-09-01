using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapStructureRefinerTests
{
    [Fact]
    public void EccBudgetRequiresMinimumRemainingTime()
    {
        Assert.False(MapStructureRefiner.HasEccBudget(
            MapStructureRefiner.MinimumEccBudgetMilliseconds - 1));
        Assert.True(MapStructureRefiner.HasEccBudget(
            MapStructureRefiner.MinimumEccBudgetMilliseconds));
    }

    [Fact]
    public void DisabledEccSkipsEntireRefinementStage()
    {
        var candidate = new MapStructureCandidate
        {
            Scale = 1d,
            ReferenceX = 10,
            ReferenceY = 20,
            CompositeCost = 10d
        };
        var tuning = new MapStructureRegistrationTuning
        {
            EnableEccRefinement = false
        };

        var refined = MapStructureRefiner.RefineCandidate(
            candidate,
            null!,
            null!,
            null!,
            null!,
            tuning,
            null!,
            int.MaxValue,
            out var diagnostics);

        Assert.Same(candidate, refined);
        Assert.False(diagnostics.Executed);
        Assert.Equal("disabled", diagnostics.SkipReason);
    }

    [Fact]
    public void LargeEccInputIsDownsampledWithoutChangingZeroTranslation()
    {
        const int width = 1100;
        const int height = 700;
        using var edges = Mat.Zeros(height, width, MatType.CV_8UC1).ToMat();
        Cv2.Rectangle(edges, new Rect(80, 90, 760, 430), Scalar.White, 5);
        Cv2.Line(edges, new Point(130, 170), new Point(930, 560), Scalar.White, 4);
        using var structure = Mat.Ones(height, width, MatType.CV_8UC1).ToMat();
        structure.SetTo(Scalar.White);
        using var query = new QueryGeometry(
            1d,
            structure.Clone(),
            edges.Clone(),
            new Rect(0, 0, width, height),
            MapStructureScaleSearch.FindNonZeroPoints(edges));
        using var reference = new MapStructureFeatures(
            Mat.Zeros(height, width, MatType.CV_8UC1).ToMat(),
            structure.Clone(),
            edges.Clone());
        var candidate = new MapStructureCandidate
        {
            Scale = 1d,
            ReferenceX = 0,
            ReferenceY = 0,
            OffsetX = 25d,
            OffsetY = 40d,
            CompositeCost = 10d
        };

        var refined = MapStructureRefiner.RefineTranslationWithEcc(
            candidate,
            query,
            reference,
            out var diagnostics);

        Assert.True(diagnostics.Executed);
        Assert.True(diagnostics.Downsampled);
        Assert.True(
            diagnostics.ExecutionWidth * diagnostics.ExecutionHeight
                <= MapStructureRefiner.MaximumEccInputPixels);
        Assert.True(
            Math.Max(diagnostics.ExecutionWidth, diagnostics.ExecutionHeight)
                <= MapStructureRefiner.MaximumEccInputDimension);
        Assert.InRange(refined.OffsetX, 23d, 27d);
        Assert.InRange(refined.OffsetY, 38d, 42d);
    }

    [Fact]
    public void RestrictedTemplateEstimateAccountsForLargeQueryAndDomain()
    {
        const int width = 1100;
        const int height = 700;
        using var edges = Mat.Ones(height, width, MatType.CV_8UC1).ToMat();
        using var structure = edges.Clone();
        using var query = new QueryGeometry(
            1d,
            structure.Clone(),
            edges.Clone(),
            new Rect(0, 0, width, height),
            MapStructureScaleSearch.FindNonZeroPoints(edges));

        var estimate = MapStructureScaleSearch
            .EstimateRestrictedTemplateMilliseconds(
                query,
                new Rect(0, 0, 299, 299));

        Assert.InRange(estimate, 100, 400);
    }

    [Theory]
    [InlineData(MapAlignmentChannel.LowStructure, 100, 300, 300, true)]
    [InlineData(MapAlignmentChannel.LowStructure, 101, 300, 300, false)]
    [InlineData(MapAlignmentChannel.Standard, 200, 300, 300, true)]
    [InlineData(MapAlignmentChannel.Standard, 301, 300, 300, false)]
    public void ExpensiveRestrictedTemplateIsSkippedOnlyForLowStructure(
        MapAlignmentChannel channel,
        int estimate,
        int remaining,
        int warmBudget,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapStructureScaleSearch.ShouldRunRestrictedTemplateSearch(
                channel,
                estimate,
                remaining,
                warmBudget));
    }
}
