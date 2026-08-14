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
            Tuning = TestTuning(),
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

    [Fact]
    public void DerivedCacheDoesNotWriteIntoMapDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"idvbuff-structure-cache-{Guid.NewGuid():N}");
        var mapDirectory = Path.Combine(root, "maps", "map-one");
        var cacheDirectory = Path.Combine(root, "cache");
        Directory.CreateDirectory(mapDirectory);
        var sentinel = Path.Combine(mapDirectory, "maps.json");
        File.WriteAllText(sentinel, "sentinel");
        using var reference = BuildReference();
        try
        {
            var cache = new MapStructureReferenceCache(
                new MapStructurePreprocessor(),
                cacheDirectory);
            using var first = cache.GetOrCreate(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                reference);

            Assert.Equal("sentinel", File.ReadAllText(sentinel));
            Assert.Single(Directory.GetFiles(mapDirectory));
            Assert.NotEmpty(Directory.GetFiles(cacheDirectory, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static MapStructureRegistrationTuning TestTuning() => new()
    {
        MinimumEdgePixels = 50,
        MinimumSpanPixels = 18,
        MinimumConsistentPartitions = 2,
        TopCandidateCount = 6,
        MaximumChamferPixels = 3.5d,
        MinimumEdgeCoverage = 0.50d,
        MinimumOccupancyCoverage = 0.35d,
        MinimumCandidateMargin = 0.025d,
        ScaleSearchRadius = 0.02d,
        ScaleSearchStep = 0.01d,
        EnableFastAlignment = false,
        FeatureRatioThreshold = 0.78d
    };

    private static MapOverlayTransform Locked(
        Mat reference,
        double offsetX = 0d,
        double offsetY = 0d) =>
        new()
        {
            ScaleX = 1d,
            ScaleY = 1d,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceWidth = reference.Width,
            ReferenceHeight = reference.Height,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };

    // ═══════════════════════════════════════════════════════════════
    // P2-1: ProcessCachedReference ownership
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ProcessCachedReference_DisposeDoesNotInvalidateCache()
    {
        // P2-1: The caller owns their clone. Disposing it must not
        // affect the internal cached instance or subsequent lookups.
        var preprocessor = new MapStructurePreprocessor();
        using var reference = BuildReference();
        var referencePath = $"cache-test-{Guid.NewGuid():N}";

        // First call — generates and caches.
        var first = preprocessor.ProcessCachedReference(
            reference, referencePath, out _, out var cacheHit1);
        Assert.NotNull(first);
        Assert.False(cacheHit1);

        // Dispose the returned object.
        first.Dispose();

        // Second call — must hit cache and return a valid, independent clone.
        var second = preprocessor.ProcessCachedReference(
            reference, referencePath, out _, out var cacheHit2);
        Assert.NotNull(second);
        Assert.True(cacheHit2, "Second call must hit cache after first Dispose");

        // The second instance must not be the same object as the first
        // (would indicate shared mutable state).
        Assert.NotSame(first, second);

        // Dispose the second — cache must remain valid for a third lookup.
        second.Dispose();

        var third = preprocessor.ProcessCachedReference(
            reference, referencePath, out _, out var cacheHit3);
        Assert.NotNull(third);
        Assert.True(cacheHit3, "Third call must still hit cache after second Dispose");
        Assert.NotSame(second, third);

        third.Dispose();
        MapStructurePreprocessor.ClearReferenceCache();
    }

    [Fact]
    public void LiveAndReferenceStructureDescriptorsUseCompatibleAkazeLayout()
    {
        var preprocessor = new MapStructurePreprocessor();
        using var source = BuildReference();
        using var reference = preprocessor.ProcessReference(source, null);
        using var live = preprocessor.ProcessLiveRoi(source);

        Assert.False(reference.Descriptors.Empty());
        Assert.False(live.Descriptors.Empty());
        Assert.Equal(reference.Descriptors.Type(), live.Descriptors.Type());
        Assert.Equal(reference.Descriptors.Cols, live.Descriptors.Cols);
        Assert.Equal(61, reference.Descriptors.Cols);
    }
}
