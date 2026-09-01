using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapStructureGenerationTuningTests
{
    [Fact]
    public void DefaultLiveCompositionRestoresGradientEdges()
    {
        using var source = CreateStructureImage();
        var preprocessor = new MapStructurePreprocessor();

        using var reference = preprocessor.ProcessReference(
            source,
            ignoreRegions: null,
            generationTuning: new MapStructureGenerationTuning());
        using var legacy = preprocessor.ProcessLiveRoiDiagnostic(
            source,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            out _,
            profile: MapStructurePreprocessingProfile.EdgesOnly,
            generationTuning: MapStructureGenerationTuning.CreateLegacyBaseline());
        using var improved = preprocessor.ProcessLiveRoiDiagnostic(
            source,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            out var improvedTiming,
            profile: MapStructurePreprocessingProfile.EdgesOnly,
            generationTuning: new MapStructureGenerationTuning());

        var referencePixels = Cv2.CountNonZero(reference.Edges);
        var legacyPixels = Cv2.CountNonZero(legacy.Edges);
        var improvedPixels = Cv2.CountNonZero(improved.Edges);

        Assert.Equal(
            MapStructureEdgeComposition.GradientAndCanny,
            improvedTiming.EdgeComposition);
        Assert.True(referencePixels > legacyPixels);
        Assert.True(improvedPixels > legacyPixels);
        Assert.True(improvedPixels >= legacyPixels);

        using var missingFromLegacy = new Mat();
        Cv2.BitwiseAnd(
            reference.Edges,
            ~legacy.Edges,
            missingFromLegacy);
        Assert.True(Cv2.CountNonZero(missingFromLegacy) > 0);
    }

    [Fact]
    public void OnePixelStructureOpeningStillProducesThinGradientEdges()
    {
        using var source = CreateStructureImage();
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure().Generation;
        var preprocessor = new MapStructurePreprocessor();

        using var features = preprocessor.ProcessLiveRoiDiagnostic(
            source,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            out _,
            profile: MapStructurePreprocessingProfile.EdgesOnly,
            generationTuning: tuning);
        using var eroded = new Mat();
        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(3, 3));
        Cv2.Erode(features.Edges, eroded, kernel);

        var edgePixels = Cv2.CountNonZero(features.Edges);
        Assert.True(edgePixels > 0);
        Assert.True(Cv2.CountNonZero(eroded) < edgePixels / 10);
    }

    [Fact]
    public void GenerationTuningNormalizesAndRoundTripsThroughJson()
    {
        var tuning = new MapStructureGenerationTuning
        {
            SchemaVersion = 0,
            ReferenceEdgeComposition = (MapStructureEdgeComposition)99,
            LiveEdgeComposition = (MapStructureEdgeComposition)99,
            CannyLowThreshold = double.NaN,
            CannyHighThreshold = -10d,
            StructureCloseKernelSize = 4,
            StructureOpenKernelSize = 100,
            EdgeClosingKernelSize = 2,
            EdgeClosingIterations = 99,
            DominantClusterAttachmentDistancePixels = -2,
            DominantClusterMinimumAttachedAreaRatio = double.PositiveInfinity,
            MinimumEdgeComponentAreaPixels = 0
        };

        tuning.Normalize();

        Assert.Equal(MapStructureGenerationTuning.CurrentSchemaVersion, tuning.SchemaVersion);
        Assert.Equal(MapStructureEdgeComposition.GradientAndCanny, tuning.ReferenceEdgeComposition);
        Assert.Equal(MapStructureEdgeComposition.GradientAndCanny, tuning.LiveEdgeComposition);
        Assert.Equal(5, tuning.StructureCloseKernelSize);
        Assert.Equal(31, tuning.StructureOpenKernelSize);
        Assert.Equal(3, tuning.EdgeClosingKernelSize);
        Assert.Equal(4, tuning.EdgeClosingIterations);
        Assert.Equal(0, tuning.DominantClusterAttachmentDistancePixels);
        Assert.Equal(1, tuning.MinimumEdgeComponentAreaPixels);

        var registration = new MapStructureRegistrationTuning
        {
            SchemaVersion = 0,
            Generation = tuning
        };
        var json = JsonSerializer.Serialize(registration);
        var roundTripped = JsonSerializer.Deserialize<MapStructureRegistrationTuning>(json)!;
        roundTripped.Normalize();

        Assert.Equal(
            tuning.CacheFingerprint,
            roundTripped.Generation.CacheFingerprint);
        Assert.Equal(MapStructureRegistrationTuning.CurrentSchemaVersion, roundTripped.SchemaVersion);
    }

    [Fact]
    public void ReferenceCacheSeparatesGenerationFingerprints()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.StructureGeneration.{Guid.NewGuid():N}");
        try
        {
            using var source = CreateStructureImage();
            using var cache = new MapStructureReferenceCache(
                new MapStructurePreprocessor(),
                root);
            var mapId = Guid.NewGuid();
            var updatedAt = DateTimeOffset.UtcNow;

            using var legacy = cache.GetOrCreate(
                mapId,
                updatedAt,
                source,
                floor: "1f",
                generationTuning: MapStructureGenerationTuning.CreateLegacyBaseline());
            using var improved = cache.GetOrCreate(
                mapId,
                updatedAt,
                source,
                floor: "1f",
                generationTuning: new MapStructureGenerationTuning());

            Assert.Equal(
                0d,
                Cv2.Norm(legacy.Edges, improved.Edges, NormTypes.INF));
            var mapDirectory = Path.Combine(root, mapId.ToString("N"));
            Assert.Equal(2, Directory.GetDirectories(mapDirectory).Length);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static Mat CreateStructureImage()
    {
        var image = new Mat(
            new Size(360, 280),
            MatType.CV_8UC3,
            Scalar.Black);
        for (var x = 36; x <= 324; x += 36)
        {
            Cv2.Line(
                image,
                new Point(x, 28),
                new Point(x, 252),
                Scalar.White,
                5);
        }
        for (var y = 28; y <= 252; y += 32)
        {
            Cv2.Line(
                image,
                new Point(36, y),
                new Point(324, y),
                Scalar.White,
                5);
        }
        Cv2.Rectangle(
            image,
            new Rect(82, 72, 74, 58),
            new Scalar(255, 130, 70),
            -1);
        return image;
    }
}
