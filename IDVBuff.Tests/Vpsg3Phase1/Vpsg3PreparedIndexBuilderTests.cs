using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase1;

public sealed class Vpsg3PreparedIndexBuilderTests
{
    [Fact]
    public void BuildFromMat_ThrowsOnEmptyOrNullInput()
    {
        var key = new Vpsg3IndexCacheKey(Guid.NewGuid(), "1f", "fp", DateTimeOffset.UtcNow, "g1");

        Assert.Throws<ArgumentNullException>(() =>
            Vpsg3PreparedIndexBuilder.BuildFromMat(null!, key));

        using var empty = new Mat();
        Assert.Throws<ArgumentException>(() =>
            Vpsg3PreparedIndexBuilder.BuildFromMat(empty, key));
    }

    [Fact]
    public void BuildFromMat_BuildsValidBitsetAndCalculatesMemoryFootprint()
    {
        using var edgeMat = new Mat(200, 300, MatType.CV_8UC1, Scalar.All(0));
        // Draw some grid lines to simulate wall structures
        for (var y = 20; y < 180; y += 30)
            Cv2.Line(edgeMat, new Point(10, y), new Point(290, y), Scalar.All(255), 2);
        for (var x = 20; x < 280; x += 40)
            Cv2.Line(edgeMat, new Point(x, 10), new Point(x, 190), Scalar.All(255), 2);

        var key = new Vpsg3IndexCacheKey(Guid.NewGuid(), "1f", "fp1", DateTimeOffset.UtcNow, "gen1");
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(edgeMat, key);

        Assert.Equal(300, floor.ReferenceWidth);
        Assert.Equal(200, floor.ReferenceHeight);
        Assert.True(floor.EdgePixelCount > 500);

        // Words per row for width 300: (300 + 63) / 64 = 5
        Assert.Equal(5, floor.WordsPerRow);
        Assert.Equal(200 * 5, floor.BitsetWordCount);
        Assert.Equal(200 * 5, floor.DilatedBitsetSpan.Length);
        Assert.False(floor.DilatedBitsetMemory.IsEmpty);

        // Memory footprint: object overhead (128) + bitset (1000 * 8 + 24) = ~8152 bytes
        Assert.True(floor.MemoryBytes > 8000);
        Assert.False(floor.IsDisposed);
    }

    [Fact]
    public async Task BuildFromMatAsync_ClonesMat_SafeEvenIfCallerDisposesInputSynchronously()
    {
        var key = new Vpsg3IndexCacheKey(Guid.NewGuid(), "1f", "fp1", DateTimeOffset.UtcNow, "gen1");

        Task<Vpsg3IndexBuildResult> task;
        using (var callerMat = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(0)))
        {
            Cv2.Line(callerMat, new Point(10, 10), new Point(90, 90), Scalar.All(255), 2);
            // Initiate async build
            task = Vpsg3PreparedIndexBuilder.BuildFromMatAsync(callerMat, key);
            // callerMat is immediately disposed upon exiting this block!
        }

        var result = await task;
        Assert.True(result.Success);
        Assert.NotNull(result.Floor);

        using (result.Floor)
        {
            Assert.Equal(100, result.Floor.ReferenceWidth);
            Assert.Equal(100, result.Floor.ReferenceHeight);
            Assert.False(result.Floor.IsDisposed);
        }
    }

    [Fact]
    public async Task BuildFromFileAsync_LoadsImageAndBuildsCleanly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"vpsg3-test-{Guid.NewGuid():N}.png");
        try
        {
            using (var mat = new Mat(120, 150, MatType.CV_8UC1, Scalar.All(0)))
            {
                Cv2.Line(mat, new Point(20, 20), new Point(130, 20), Scalar.All(255), 2);
                Cv2.ImWrite(tempFile, mat);
            }

            var key = new Vpsg3IndexCacheKey(Guid.NewGuid(), "1f", "fp_file", DateTimeOffset.UtcNow, "gen1");
            var result = await Vpsg3PreparedIndexBuilder.BuildFromFileAsync(tempFile, key);

            Assert.True(result.Success);
            Assert.NotNull(result.Floor);
            using (result.Floor)
            {
                Assert.Equal(150, result.Floor.ReferenceWidth);
                Assert.Equal(120, result.Floor.ReferenceHeight);
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void ComputeReferenceScalePrior_DetectsDominantPitchForPeriodicStructures()
    {
        using var edgeMat = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        // Draw vertical stripes with strict period = 40 pixels
        for (var x = 20; x < 380; x += 40)
        {
            Cv2.Line(edgeMat, new Point(x, 10), new Point(x, 390), Scalar.All(255), 1);
        }

        var prior = Vpsg3PreparedIndexBuilder.ComputeReferenceScalePrior(
            edgeMat,
            Cv2.CountNonZero(edgeMat),
            Vpsg3TuningConfig.Default);

        Assert.True(prior.FastPathEligible);
        Assert.InRange(prior.ReferencePitch, 38.0, 42.0);
        Assert.True(prior.PeakRatio >= 2.0d);
    }

    [Fact]
    public void ComputeReferenceScalePrior_RejectsSparseEdgeImages()
    {
        using var edgeMat = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        // Draw just a single 20px line (20 pixels total, well below 300 min)
        Cv2.Line(edgeMat, new Point(10, 10), new Point(30, 10), Scalar.All(255), 1);

        var count = Cv2.CountNonZero(edgeMat);
        var prior = Vpsg3PreparedIndexBuilder.ComputeReferenceScalePrior(
            edgeMat,
            count,
            Vpsg3TuningConfig.Default);

        Assert.False(prior.FastPathEligible);
        Assert.Contains("EdgePixelCountBelowThreshold", prior.RejectReason);
    }

    [Fact]
    public async Task BuildFromMatAsync_TokenAlreadyCancelledBeforeCall_ReturnsCanceledAndDoesNotLeak()
    {
        using var edgeMat = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(255));
        var key = new Vpsg3IndexCacheKey(Guid.NewGuid(), "1f", "fp", DateTimeOffset.UtcNow, "gen1");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var result = await Vpsg3PreparedIndexBuilder.BuildFromMatAsync(edgeMat, key, cancellationToken: cts.Token);
        Assert.False(result.Success);
        Assert.Null(result.Floor);
        Assert.Contains("canceled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(edgeMat.IsDisposed);
    }

    [Fact]
    public async Task BuildFromMatAsync_TokenCancelledImmediatelyAfterClone_GuaranteesDisposal()
    {
        using var edgeMat = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(255));
        var key = new Vpsg3IndexCacheKey(Guid.NewGuid(), "1f", "fp", DateTimeOffset.UtcNow, "gen1");

        // Concurrent loop triggering cancellation right at dispatch boundary
        for (var i = 0; i < 50; i++)
        {
            using var cts = new CancellationTokenSource();
            var task = Vpsg3PreparedIndexBuilder.BuildFromMatAsync(edgeMat, key, cancellationToken: cts.Token);
            cts.Cancel();

            var result = await task;
            if (result.Success && result.Floor is not null)
            {
                result.Floor.Dispose();
            }
        }

        Assert.False(edgeMat.IsDisposed);
    }
}
