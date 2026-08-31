using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class MapStructureRegistrarTests
{
    [Fact]
    public void TinyUniformScaleSearchRecoversScaleAndTranslation()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var source = new Mat(reference, crop);
        const double expectedScale = 1.02d;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * expectedScale),
                (int)Math.Round(source.Height * expectedScale)),
            0d,
            0d,
            InterpolationFlags.Nearest);
        var viewport = new MapScreenRect(600d, 300d, live.Width, live.Height);
        var tuning = TestTuning();
        tuning.ScaleSearchRadius = 0.04d;
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(
                reference,
                offsetX: viewport.X - crop.X,
                offsetY: viewport.Y - crop.Y),
            Tuning = tuning,
            AllowScaleSearch = true
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.True(result.ScaleHypothesisCount > 1);
        Assert.InRange(result.Transform.ScaleX, 1.009d, 1.021d);
        Assert.InRange(
            Math.Abs(result.Transform.OffsetX - (viewport.X - (crop.X * result.Transform.ScaleX))),
            0d,
            // AKAZE consensus votes are quantized to descriptor keypoints;
            // allow the same sub-pixel/refinement rounding margin as the
            // three-pixel registration target.
            3.5d);
    }

    [Fact]
    public void SingleStraightCorridorIsRejectedAsInsufficient()
    {
        using var reference = new Mat(new Size(420, 300), MatType.CV_8UC3, Scalar.Black);
        Cv2.Line(reference, new Point(40, 150), new Point(380, 150), Scalar.White, 8);
        using var live = new Mat(new Size(260, 80), MatType.CV_8UC3, Scalar.Black);
        Cv2.Line(live, new Point(10, 40), new Point(250, 40), Scalar.White, 8);
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = new MapScreenRect(0d, 0d, live.Width, live.Height),
            LockedTransform = Locked(reference),
            Tuning = TestTuning(),
            AllowScaleSearch = false
        });

        Assert.False(result.Accepted);
        Assert.Contains(
            result.RejectionReason,
            new[]
            {
                MapStructureRejectionReason.InsufficientStructure,
                MapStructureRejectionReason.InconsistentStructure,
                MapStructureRejectionReason.AmbiguousCandidates
            });
    }

    [Fact]
    public void RepeatedRoomGroupsAreRejectedAsAmbiguous()
    {
        using var reference = new Mat(new Size(560, 260), MatType.CV_8UC3, Scalar.Black);
        DrawRepeatedGroup(reference, 25);
        DrawRepeatedGroup(reference, 315);
        using var live = new Mat(reference, new Rect(20, 30, 205, 190)).Clone();
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = new MapScreenRect(0d, 0d, live.Width, live.Height),
            LockedTransform = Locked(reference, -20d, -30d),
            Tuning = TestTuning(),
            AllowScaleSearch = false
        });

        Assert.False(result.Accepted);
        Assert.Equal(
            MapStructureRejectionReason.AmbiguousCandidates,
            result.RejectionReason);
    }

    [Fact]
    public void AdjacentScalesAtSameReferenceLocationAreOneAlignmentBasin()
    {
        var tuning = TestTuning();
        tuning.ScaleSearchRadius = 0.15d;
        tuning.Normalize();
        var first = new MapStructureCandidate
        {
            Scale = 1.0174d,
            ReferenceX = 217,
            ReferenceY = 219,
            OffsetX = 337.8d,
            OffsetY = 346.9d
        };
        var adjacentScale = new MapStructureCandidate
        {
            Scale = 1.0471d,
            ReferenceX = 223,
            ReferenceY = 222,
            OffsetX = 324.6d,
            OffsetY = 337.2d
        };
        var otherLocation = adjacentScale with
        {
            ReferenceX = 315,
            ReferenceY = 219
        };

        Assert.True(StructureRegistrationRules.IsSameAlignmentBasin(
            first,
            adjacentScale,
            tuning));
        Assert.False(StructureRegistrationRules.IsSameAlignmentBasin(
            first,
            otherLocation,
            tuning));
    }

    [Theory]
    [InlineData(0.82d)]
    [InlineData(1.18d)]
    public void WideRecoverySearchAcceptsScaleBeyondFixedFifteenPercentGate(
        double plantedScale)
    {
        // 回归：二楼无门恢复路径用 ±0.30 搜索半径从不可靠 seed（中性 1.0）
        // 恢复真实 scale。正确 scale 偏离 seed 超过 15% 时，固定 15% 的
        // scale 一致性门会误拒为 ScaleChangeTooLarge（"缩放超出安全范围"）；
        // 门限必须覆盖搜索实际探索的范围。
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var source = new Mat(reference, crop);
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * plantedScale),
                (int)Math.Round(source.Height * plantedScale)),
            0d,
            0d,
            InterpolationFlags.Nearest);
        var viewport = new MapScreenRect(600d, 300d, live.Width, live.Height);
        var tuning = TestTuning();
        tuning.ScaleSearchRadius = 0.30d;
        // 模拟全局恢复：禁止固定 scale 快速粗搜索与单假设早停。
        tuning.EnableFastAlignment = false;
        tuning.DisableScaleEarlyTermination = true;
        tuning.Normalize();
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(
                reference,
                offsetX: viewport.X - crop.X,
                offsetY: viewport.Y - crop.Y),
            Tuning = tuning,
            AllowScaleSearch = true
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.NotEqual(
            MapStructureRejectionReason.ScaleChangeTooLarge,
            result.RejectionReason);
        Assert.InRange(
            result.Transform.ScaleX,
            plantedScale - 0.03d,
            plantedScale + 0.03d);
        Assert.InRange(
            Math.Abs(result.Transform.OffsetX
                - (viewport.X - (crop.X * result.Transform.ScaleX))),
            0d,
            3.5d);
        Assert.InRange(
            Math.Abs(result.Transform.OffsetY
                - (viewport.Y - (crop.Y * result.Transform.ScaleY))),
            0d,
            3.5d);
    }

    [Fact]
    public void IncrementalLowStructureRecoveryBatchAcceptsScaleFarFromNeutralSeed()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var source = new Mat(reference, crop);
        const double plantedScale = 0.448984819d;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * plantedScale),
                (int)Math.Round(source.Height * plantedScale)),
            0d,
            0d,
            InterpolationFlags.Nearest);
        var viewport = new MapScreenRect(600d, 300d, live.Width, live.Height);
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure(
            new IDVBuff.Core.Models.LowStructureConfig
            {
                MaximumChamferPixels = 4.0d,
                MinimumEdgeCoverage = 0.45d,
                MinimumOccupancyCoverage = 0.20d,
                MinimumCandidateMargin = 0.02d,
                MinimumConsistentPartitions = 2,
                MinimumEdgePixels = 40,
                MinimumSpanPixels = 16
            });
        var recoveryPlan = LowStructureAlignmentPlan.IncrementalRecovery(
            [plantedScale, 0.72d, 1.38d],
            batch: 1,
            LowStructureAlignmentPlan.CreateConfig(tuning));
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(
                reference,
                offsetX: viewport.X - crop.X,
                offsetY: viewport.Y - crop.Y),
            Tuning = tuning,
            Channel = MapAlignmentChannel.LowStructure,
            ScaleSearchPolicy = MapScaleSearchPolicy.Search,
            LowStructurePlan = recoveryPlan,
            ForceBestCandidate = false
        });

        Assert.True(
            result.Accepted,
            $"{result.FailureReason}; rejection={result.RejectionReason}; "
            + $"candidates={result.Candidates.Count}; edges={result.QueryEdgePixels}; "
            + $"bounds={result.QueryBoundsWidth}x{result.QueryBoundsHeight}; "
            + $"oversized={result.OversizedHypothesisCount}; "
            + $"searchMs={result.SearchMilliseconds:F1}; "
            + CandidateMetrics(result));
        Assert.NotNull(result.Transform);
        Assert.NotEqual(
            MapStructureRejectionReason.ScaleChangeTooLarge,
            result.RejectionReason);
        Assert.Equal(3, result.ScaleHypothesisCount);
        Assert.InRange(result.Transform.ScaleX, 0.42d, 0.48d);
        Assert.InRange(result.SearchMilliseconds, 0d, 500d);
    }

    [Fact]
    public void SparseLowStructureScaleSelectorUsesLocalFitInsteadOfExploredBounds()
    {
        using var referenceImage = BuildReference();
        var crop = new Rect(25, 20, 255, 210);
        using var source = new Mat(referenceImage, crop);
        const double plantedScale = 0.56d;
        using var liveImage = new Mat();
        Cv2.Resize(
            source,
            liveImage,
            new Size(
                (int)Math.Round(source.Width * plantedScale),
                (int)Math.Round(source.Height * plantedScale)),
            interpolation: InterpolationFlags.Nearest);
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure(
            new IDVBuff.Core.Models.LowStructureConfig
            {
                MinimumEdgePixels = 20,
                MinimumSpanPixels = 12
            });
        var preprocessor = new MapStructurePreprocessor();
        using var reference = preprocessor.ProcessReference(
            referenceImage,
            null,
            tuning.Generation);
        using var live = preprocessor.ProcessLiveRoi(
            liveImage,
            null,
            null,
            generateVisibleMask: true,
            profile: MapStructurePreprocessingProfile.EdgesOnly,
            generationTuning: tuning.Generation);

        var ranked = MapStructureLowScaleSelector.Rank(live, reference, tuning);

        Assert.NotEmpty(ranked);
        Assert.InRange(ranked[0], plantedScale - 0.08d, plantedScale + 0.08d);
        var exploredWidthRatio = liveImage.Width / (double)referenceImage.Width;
        Assert.True(Math.Abs(ranked[0] - plantedScale)
            < Math.Abs(exploredWidthRatio - plantedScale));
    }

    [Fact]
    public void FixedLowStructureScaleUsesTheSameContentDomainAsSearch()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var source = new Mat(reference, crop);
        const double plantedScale = 0.448984819d;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * plantedScale),
                (int)Math.Round(source.Height * plantedScale)),
            0d,
            0d,
            InterpolationFlags.Nearest);
        var viewport = new MapScreenRect(600d, 300d, live.Width, live.Height);
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure(
            new IDVBuff.Core.Models.LowStructureConfig
            {
                MaximumChamferPixels = 3.0d,
                MinimumEdgeCoverage = 0.45d,
                MinimumOccupancyCoverage = 0.20d,
                MinimumCandidateMargin = 0.02d,
                MinimumConsistentPartitions = 2,
                MinimumEdgePixels = 40,
                MinimumSpanPixels = 16
            });
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = plantedScale,
                ScaleY = plantedScale,
                OffsetX = viewport.X - (crop.X * plantedScale),
                OffsetY = viewport.Y - (crop.Y * plantedScale),
                ReferenceWidth = reference.Width,
                ReferenceHeight = reference.Height,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            Tuning = tuning,
            Channel = MapAlignmentChannel.LowStructure,
            ScaleSearchPolicy = MapScaleSearchPolicy.Fixed,
            RestrictSearchToLockedTransform = false
        });

        Assert.True(
            result.Accepted,
            $"{result.FailureReason}; rejection={result.RejectionReason}; "
            + $"best={result.BestScore:F3}; candidates={result.Candidates.Count}; "
            + CandidateMetrics(result));
        Assert.Equal(1, result.ScaleHypothesisCount);
        Assert.NotNull(result.Transform);
        Assert.Equal(plantedScale, result.Transform!.ScaleX, 8);
    }

    private static string CandidateMetrics(MapStructureRegistrationResult result) =>
        string.Join(
            " | ",
            result.Candidates.Select((candidate, index) =>
                $"#{index}:scale={candidate.Scale:F6},xy={candidate.ReferenceX},{candidate.ReferenceY},"
                + $"ch={candidate.ChamferPixels:F3},rch={candidate.ReverseChamferPixels:F3},"
                + $"edge={candidate.EdgeCoverage:F3},occ={candidate.OccupancyCoverage:F3},"
                + $"ref={candidate.ReferenceCoverage:F3},proj={candidate.ProjectionCorrelation:F3},"
                + $"cost={candidate.CompositeCost:F3}"));

    [Fact]
    public void ForcedBestCandidateAcceptsAmbiguousRepeatedRooms()
    {
        using var reference = new Mat(
            new Size(560, 260),
            MatType.CV_8UC3,
            Scalar.Black);
        DrawRepeatedGroup(reference, 25);
        DrawRepeatedGroup(reference, 315);
        using var live = new Mat(
            reference,
            new Rect(20, 30, 205, 190)).Clone();
        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = new MapScreenRect(
                    0d,
                    0d,
                    live.Width,
                    live.Height),
                LockedTransform = Locked(reference, -20d, -30d),
                Tuning = TestTuning(),
                AllowScaleSearch = false,
                ForceBestCandidate = true
            });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.True(result.WasForcedBestCandidate);
        Assert.Equal(
            MapStructureRejectionReason.AmbiguousCandidates,
            result.RejectionReason);
    }

    [Fact]
    public void ForcedBestCandidateStillRejectsWhenNoCandidateExists()
    {
        using var reference = BuildReference();
        using var live = new Mat(
            new Size(300, 220),
            MatType.CV_8UC3,
            Scalar.Black);
        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = new MapScreenRect(
                    0d,
                    0d,
                    live.Width,
                    live.Height),
                LockedTransform = Locked(reference),
                Tuning = TestTuning(),
                AllowScaleSearch = false,
                ForceBestCandidate = true
            });

        Assert.False(result.Accepted);
        Assert.Null(result.Transform);
        Assert.False(result.WasForcedBestCandidate);
        Assert.Equal(
            MapStructureRejectionReason.InsufficientStructure,
            result.RejectionReason);
    }
}
