using System.Diagnostics;
using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3PrecisionTests(ITestOutputHelper output)
{
    [Fact]
    public void BilinearSamplingPreservesSubpixelDistanceAndPenalizesOutside()
    {
        float[] field = [0, 1, 1, 2];
        Assert.Equal(0.75d, Vpsg3PrecisionRefiner.Sample(field, 2, 2, 0.25, 0.5), 9);
        Assert.Equal(2d, Vpsg3PrecisionRefiner.Sample(field, 2, 2, 1, 1));
        Assert.True(double.IsPositiveInfinity(Vpsg3PrecisionRefiner.Sample(field, 2, 2, -0.001, 0)));
    }

    [Fact]
    public void DistanceFieldIsOptInAndFollowsLeaseLifetime()
    {
        using var reference = Reference();
        using var baseline = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, Key());
        var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, Key(), preparePrecision: true);
        Assert.True(baseline.PrecisionDistance.IsEmpty);
        Assert.Equal(reference.Width * reference.Height, floor.PrecisionDistance.Length);
        Assert.Equal(baseline.MemoryBytes + reference.Width * reference.Height * 4L + 24, floor.MemoryBytes);
        Assert.True(Vpsg3FloorIndexLease.TryCreate(floor, out var lease));
        floor.Dispose();
        Assert.False(lease!.Floor.PrecisionDistance.IsEmpty);
        lease.Dispose();
        Assert.True(floor.PrecisionDistance.IsEmpty);
    }

    [Fact]
    public void ExpiredBudgetAndMissingSupportNeverCalibrate()
    {
        using var reference = Reference();
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, Key(), preparePrecision: true);
        using var observation = Observation(reference, 0.9025d);
        var expired = new Vpsg3PrecisionBudget(Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        var result = Vpsg3PrecisionRefiner.Refine(observation, floor, 0.9, 50, 40, expired);
        Assert.False(result.Calibrated);
        Assert.Equal("budget", result.Termination);
        observation.ValidMask.SetTo(Scalar.Black);
        result = Vpsg3PrecisionRefiner.Refine(observation, floor, 0.9, 50, 40, Vpsg3PrecisionBudget.Start());
        Assert.False(result.Calibrated);
        Assert.Equal("insufficient-support", result.Termination);
    }

    [Fact]
    public void FrozenSyntheticScaleMatrixProducesMeasuredPrecisionEvidence()
    {
        using var reference = Reference();
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, Key(), preparePrecision: true);
        var rows = new List<object>();
        var calibrated = 0;
        foreach (var trueScale in new[] { 0.65d, 0.90251893665d, 1d, 1.3375d })
        {
            using var observation = Observation(reference, trueScale);
            foreach (var bias in new[] { -0.01d, 0d, 0.01d })
            {
                var seed = trueScale * (1 + bias);
                var cx = observation.Width / 2d;
                var cy = observation.Height / 2d;
                var x = cx - (cx - 50) / trueScale * seed;
                var y = cy - (cy - 40) / trueScale * seed;
                // Warm JIT independently; the measured invocation still owns its real 10ms deadline.
                Vpsg3PrecisionRefiner.Refine(observation, floor, seed, x, y, Vpsg3PrecisionBudget.Start());
                var result = Vpsg3PrecisionRefiner.Refine(observation, floor, seed, x, y, Vpsg3PrecisionBudget.Start());
                var error = result.RadiusPixels * Math.Abs(result.Scale / trueScale - 1);
                var baselineError = result.RadiusPixels * Math.Abs(seed / trueScale - 1);
                rows.Add(new { trueScale, bias, baselineError, farScaleErrorPixels = error, result });
                output.WriteLine($"s={trueScale:F6} bias={bias:F3} calibrated={result.Calibrated} error={error:F4}px ms={result.Milliseconds:F3} {result.Termination}");
                if (result.Calibrated)
                {
                    calibrated++;
                    Assert.True(error <= 1d, $"False precision claim: {error:F4}px");
                }
            }
        }
        var report = Environment.GetEnvironmentVariable("VPSG3_PRECISION_SYNTHETIC_OUTPUT");
        if (!string.IsNullOrWhiteSpace(report))
            File.WriteAllText(report, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(calibrated > 0, "Rejecting every synthetic sample is not precision improvement.");
    }

    [Fact]
    public void ThickFlatFieldCannotClaimScalePrecision()
    {
        using var lines = Reference();
        using var observation = Observation(lines, 0.9);
        using var flat = new Mat(lines.Size(), MatType.CV_8UC1, Scalar.White);
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(flat, Key(), preparePrecision: true);
        var result = Vpsg3PrecisionRefiner.Refine(observation, floor, 0.9, 50, 40, Vpsg3PrecisionBudget.Start());
        Assert.False(result.Calibrated);
        Assert.Contains(result.Termination, new[] { "scale-unobservable", "budget" });
    }

    [Fact]
    public void OutOfReferencePointsRemainPenalizedAndDoNotCalibrate()
    {
        using var reference = Reference();
        using var observation = Observation(reference, 0.9);
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, Key(), preparePrecision: true);
        var result = Vpsg3PrecisionRefiner.Refine(observation, floor, 0.9, 5000, 5000, Vpsg3PrecisionBudget.Start());
        Assert.False(result.Calibrated);
        if (result.Before is { } residual)
        {
            Assert.Equal(7.875d, residual.Loss, 8);
            Assert.Equal(6d, residual.FarP95);
        }
    }

    [Fact]
    public void SharedBudgetCannotRestartForAnotherCandidate()
    {
        var budget = new Vpsg3PrecisionBudget(Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        using var reference = Reference();
        using var observation = Observation(reference, 0.9);
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, Key(), preparePrecision: true);
        var first = Vpsg3PrecisionRefiner.Refine(observation, floor, 0.9, 50, 40, budget);
        var second = Vpsg3PrecisionRefiner.Refine(observation, floor, 0.9, 60, 50, budget);
        Assert.Equal("budget", first.Termination);
        Assert.Equal("budget", second.Termination);
        Assert.Equal(0, second.Probes);
    }

    [Fact]
    public void PrecisionShadow_AcceptsWhenRunnerUpCannotCalibrate_IfMarginPasses()
    {
        using var reference = Reference();
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, Key(), preparePrecision: true);
        using var observation = Observation(reference, 0.9025d);
        var scaleResult = new Vpsg3ScaleResult(Vpsg3ScaleStatus.Success, 0.9025d, 5.0d, 0, string.Empty);
        var spatialBest = new Vpsg3SpatialResult(0.60d, 200, 120, 4, 4, true);
        var bestCand = new Vpsg3RefinedCandidate(0.9025d, 50, 40, 0.60d, 0.60d, 0.60d, spatialBest, 1);
        var spatialWeak = new Vpsg3SpatialResult(0.20d, 200, 40, 4, 1, false);
        var weakRunner = new Vpsg3RefinedCandidate(0.9025d, 500, 400, 0.20d, 0.20d, 0.20d, spatialWeak, 1);
        var baseline = new Vpsg3BootstrapResult(true, string.Empty, 0.9025d, 50, 40, 0.60d, 0.40d, true, 4, scaleResult, bestCand, weakRunner, default);

        // Warm JIT independently for Evaluate and Verification; the measured invocation owns the 10ms deadline.
        Vpsg3PrecisionShadow.Evaluate(observation, floor, baseline);
        var shadow = Vpsg3PrecisionShadow.Evaluate(observation, floor, baseline);
        output.WriteLine($"Passed={shadow.CandidatePairPassed} Term={shadow.Termination} TotalMs={shadow.TotalMilliseconds:F3} BestCalibrated={shadow.Best?.Calibrated} BestTerm={shadow.Best?.Termination} SecondCalibrated={shadow.RunnerUp?.Calibrated} SecondTerm={shadow.RunnerUp?.Termination}");
        Assert.True(shadow.CandidatePairPassed);
        Assert.Equal("candidate-pair-passed", shadow.Termination);
        Assert.NotNull(shadow.Best);
        Assert.True(shadow.Best!.Calibrated);
    }

    [Fact]
    public void EnsurePrecisionDistance_LoadsOnDemand_AndEvictionFreesMemory()
    {
        using var reference = Reference();
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, Key(), preparePrecision: false);
        Assert.False(floor.HasPrecisionDistance);
        Assert.True(floor.PrecisionDistance.IsEmpty);
        var baseMem = floor.MemoryBytes;

        Assert.True(floor.EnsurePrecisionDistance(reference));
        Assert.True(floor.HasPrecisionDistance);
        Assert.False(floor.PrecisionDistance.IsEmpty);
        Assert.Equal(reference.Width * reference.Height, floor.PrecisionDistance.Length);
        Assert.True(floor.MemoryBytes > baseMem);

        floor.EvictPrecisionDistance();
        Assert.False(floor.HasPrecisionDistance);
        Assert.True(floor.PrecisionDistance.IsEmpty);
        Assert.Equal(baseMem, floor.MemoryBytes);
    }

    private static Vpsg3IndexCacheKey Key() => new(Guid.Empty, "1f", "precision-synthetic-v1",
        DateTimeOffset.UnixEpoch, "precision-synthetic-v1");

    private static Mat Reference()
    {
        var reference = new Mat(700, 900, MatType.CV_8UC1, Scalar.Black);
        for (var y = 55; y < 650; y += 113)
        for (var x = 40; x < 850; x += 137)
            Cv2.Rectangle(reference, new Rect(x, y, 61 + y % 17, 49 + x % 23), Scalar.White, 1);
        return reference;
    }

    private static Vpsg3LiveObservation Observation(Mat reference, double scale)
    {
        var width = (int)Math.Ceiling(reference.Width * scale) + 100;
        var height = (int)Math.Ceiling(reference.Height * scale) + 80;
        using var coordinates = new Mat();
        Cv2.FindNonZero(reference, coordinates);
        var points = Enumerable.Range(0, coordinates.Rows)
            .Where(i => i % 19 == 0)
            .Select(i => coordinates.At<Point>(i))
            .Select(p => new Point((int)Math.Round(p.X * scale + 50), (int)Math.Round(p.Y * scale + 40)))
            .Distinct().ToArray();
        var edges = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        foreach (var point in points) edges.Set(point.Y, point.X, (byte)255);
        return new(edges, new Mat(height, width, MatType.CV_8UC1, Scalar.White), width, height,
            points.Length, width * height, new MapScreenRect(0, 0, width, height), sparseEdgePoints: points);
    }
}
