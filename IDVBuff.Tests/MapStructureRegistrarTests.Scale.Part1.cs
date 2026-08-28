using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;
public sealed partial class MapStructureRegistrarTests
{

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
        SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion,
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
