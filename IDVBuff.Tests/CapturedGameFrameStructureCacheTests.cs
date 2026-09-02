using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class CapturedGameFrameStructureCacheTests
{
    [Theory]
    [InlineData(1333, 1062, 1003, 799)]
    [InlineData(2006, 1594, 1003, 797)]
    public void ComputationImageCapsWidthAndPreservesPhysicalCoordinates(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        using var frame = new CapturedGameFrame(
            new Mat(height, width, MatType.CV_8UC3),
            new MapScreenRect(100d, 200d, width, height),
            new MapScreenRect(100d, 200d, width, height),
            IntPtr.Zero);

        Assert.False(frame.HasCreatedComputationImage);
        Assert.Equal(expectedWidth, frame.ComputationImage.Width);
        Assert.True(frame.HasCreatedComputationImage);
        Assert.Equal(expectedHeight, frame.ComputationImage.Height);
        var ratio = frame.PhysicalPixelsPerComputationPixel;
        var physicalX = 617d;
        var computationX = (physicalX - frame.ViewportBounds.X) / ratio;
        var roundTripX = frame.ViewportBounds.X + (computationX * ratio);
        Assert.Equal(physicalX, roundTripX, 9);
        Assert.Equal(
            new Rect(
                (int)Math.Round(120d / ratio),
                (int)Math.Round(80d / ratio),
                Math.Max(1, (int)Math.Round(240d / ratio)),
                Math.Max(1, (int)Math.Round(160d / ratio))),
            frame.ToComputationRect(new Rect(120, 80, 240, 160)));
        Assert.True(ratio > 1d);
    }

    [Fact]
    public void DefaultLiveStructureIsExtractedOnlyOncePerFrozenFrame()
    {
        var image = new Mat(
            new Size(320, 240),
            MatType.CV_8UC3,
            Scalar.Black);
        Cv2.Rectangle(image, new Rect(40, 35, 220, 160), Scalar.White, -1);
        using var frame = new CapturedGameFrame(
            image,
            new MapScreenRect(0d, 0d, image.Width, image.Height),
            new MapScreenRect(0d, 0d, image.Width, image.Height),
            IntPtr.Zero);
        var preprocessor = new MapStructurePreprocessor();

        var first = frame.GetOrCreateDefaultLiveStructureFeatures(
            preprocessor,
            out var firstHit,
            out var firstMilliseconds,
            out var firstTiming);
        var second = frame.GetOrCreateDefaultLiveStructureFeatures(
            preprocessor,
            out var secondHit,
            out var secondMilliseconds,
            out var secondTiming);

        Assert.False(firstHit);
        Assert.True(secondHit);
        Assert.Same(first, second);
        Assert.Same(firstTiming, secondTiming);
        Assert.Equal(firstMilliseconds, secondMilliseconds);
        Assert.True(firstMilliseconds >= 0d);
        Assert.True(firstTiming.StructureComponentCount > 0);
        Assert.True(firstTiming.KeptStructureBoundsWidth > 0);
        Assert.True(firstTiming.KeptStructureBoundsHeight > 0);
    }

    [Fact]
    public void GenerationFingerprintForcesFullRebuildOfFrozenFrameFeatures()
    {
        var image = new Mat(
            new Size(320, 240),
            MatType.CV_8UC3,
            Scalar.Black);
        Cv2.Rectangle(image, new Rect(40, 35, 220, 160), Scalar.White, -1);
        using var frame = new CapturedGameFrame(
            image,
            new MapScreenRect(0d, 0d, image.Width, image.Height),
            new MapScreenRect(0d, 0d, image.Width, image.Height),
            IntPtr.Zero);
        var preprocessor = new MapStructurePreprocessor();
        var legacy = MapStructureGenerationTuning.CreateLegacyBaseline();
        var improved = new MapStructureGenerationTuning();

        var legacyFeatures = frame.GetOrCreateDefaultLiveStructureFeatures(
            preprocessor,
            out var legacyHit,
            out _,
            out var legacyTiming,
            generationTuning: legacy);
        var improvedFeatures = frame.GetOrCreateDefaultLiveStructureFeatures(
            preprocessor,
            out var improvedHit,
            out _,
            out var improvedTiming,
            generationTuning: improved);
        var improvedAgain = frame.GetOrCreateDefaultLiveStructureFeatures(
            preprocessor,
            out var improvedAgainHit,
            out _,
            out var improvedAgainTiming,
            generationTuning: improved);

        Assert.False(legacyHit);
        Assert.False(improvedHit);
        Assert.True(improvedAgainHit);
        Assert.NotSame(legacyFeatures, improvedFeatures);
        Assert.Same(improvedFeatures, improvedAgain);
        Assert.NotSame(legacyTiming, improvedTiming);
        Assert.Same(improvedTiming, improvedAgainTiming);
        Assert.NotEqual(
            legacyTiming.GenerationFingerprint,
            improvedTiming.GenerationFingerprint);
    }
}
