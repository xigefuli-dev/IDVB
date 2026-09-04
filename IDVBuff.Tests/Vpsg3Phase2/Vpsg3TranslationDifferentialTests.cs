using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3TranslationDifferentialTests
{
    [Fact]
    public void PackedGridMatchesScalarIncludingWordEdgesAnd256Hits()
    {
        using var line = new Mat(71, 139, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(line, new Rect(2, 3, 134, 64), Scalar.White, 3);
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(line,
            new(Guid.NewGuid(), "1f", "test", DateTimeOffset.UnixEpoch, "test"));
        foreach (var stride in new[] { 1, 3, 4, 7, 65 })
        {
            var points = Enumerable.Range(0, 256).Select(i => new Point(i % 5, i % 3)).ToArray();
            var columns = 139 / stride + 1;
            var scores = new int[columns * (71 / stride + 1)];
            Vpsg3TranslationSolver.ScoreCoarseGrid(points, floor, 139, 71, stride, scores);
            for (var y = 0; y <= 71; y += stride)
                for (var x = 0; x <= 139; x += stride)
                    Assert.Equal(points.Count(p => floor.IsHitK3(p.X + x, p.Y + y)),
                        scores[y / stride * columns + x / stride]);
        }
    }

    [Fact]
    public void FullCorpusPreservesEveryCandidateScoreOffsetRankAndRunnerUp()
    {
        var samples = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        var oldTranslation = new List<double>();
        var newTranslation = new List<double>();
        var oldRefine = new List<double>();
        var newRefine = new List<double>();
        try
        {
            foreach (var sample in samples)
            {
                using var observation = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
                using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine,
                    new(Guid.NewGuid(), sample.FloorKey, "test", DateTimeOffset.UnixEpoch, "test"));
                var scale = Vpsg3ScaleSolver.Solve(observation, floor);
                if (!scale.Success) continue;
                for (var pass = 0; pass < 4; pass++)
                {
                    var start = System.Diagnostics.Stopwatch.GetTimestamp();
                    var oldCandidates = Vpsg3TranslationScalarBaseline.GenerateCandidates(observation, floor, scale.SeedScale);
                    var oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    start = System.Diagnostics.Stopwatch.GetTimestamp();
                    var newCandidates = Vpsg3TranslationSolver.GenerateCandidates(observation, floor, scale.SeedScale);
                    var newMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Assert.Equal(oldCandidates, newCandidates);
                    if (pass > 0) { oldTranslation.Add(oldMs); newTranslation.Add(newMs); }
                }
                var candidates = Vpsg3TranslationSolver.GenerateCandidates(observation, floor, scale.SeedScale);
                foreach (var candidate in new[] { candidates.Top1, candidates.DistinctRunnerUp, candidates.DistinctRunnerUp2 })
                {
                    if (!candidate.HasValue) continue;
                    var c = candidate.Value;
                    var start = System.Diagnostics.Stopwatch.GetTimestamp();
                    var oldResult = Vpsg3RefinerScalarBaseline.Refine(observation.SparseEdgePoints, floor, scale.SeedScale,
                        c.OffsetX, c.OffsetY, observation.ViewportBounds, observation.Width, observation.Height);
                    oldRefine.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    start = System.Diagnostics.Stopwatch.GetTimestamp();
                    var newResult = Vpsg3LocalRefiner.Refine(observation.SparseEdgePoints, floor, scale.SeedScale,
                        c.OffsetX, c.OffsetY, observation.ViewportBounds, observation.Width, observation.Height);
                    newRefine.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    Assert.Equal(oldResult, newResult);
                }
            }
            static double P(List<double> values, double p) => values.Order().ElementAt((int)((values.Count - 1) * p));
            var report = $"Same-process scalar/optimized differential; all candidate and joint transform values equal.\n" +
                $"T3 old P50/P95: {P(oldTranslation, .5):F3}/{P(oldTranslation, .95):F3} ms\n" +
                $"T3 new P50/P95: {P(newTranslation, .5):F3}/{P(newTranslation, .95):F3} ms\n" +
                $"Refiner old P50/P95: {P(oldRefine, .5):F3}/{P(oldRefine, .95):F3} ms per candidate\n" +
                $"Refiner new P50/P95: {P(newRefine, .5):F3}/{P(newRefine, .95):F3} ms per candidate\n";
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../scratch"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "vpsg3-paired-performance.txt"), report);
        }
        finally
        {
            foreach (var sample in samples) sample.Dispose();
        }
    }
}
