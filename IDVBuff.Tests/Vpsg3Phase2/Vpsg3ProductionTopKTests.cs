using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3ProductionTopKTests
{
    [Fact]
    public void MeasureActualJointGatesForOneTwoAndThreeRefinedCandidates()
    {
        var samples = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        var accepted = new int[4];
        var wrong = new int[4];
        var evidence = new List<object>();
        try
        {
            foreach (var sample in samples)
            {
                using var observation = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
                using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine,
                    new(Guid.NewGuid(), sample.FloorKey, "test", DateTimeOffset.UnixEpoch, "test"));
                var scale = Vpsg3ScaleSolver.Solve(observation, floor);
                if (!scale.Success) continue;
                var candidates = Vpsg3TranslationSolver.GenerateCandidates(observation, floor, scale.SeedScale);
                if (candidates.Top1.RawScore < 5) continue;
                var refined = new List<Vpsg3RefinedCandidate>();
                foreach (var candidate in new[] { candidates.Top1, candidates.DistinctRunnerUp, candidates.DistinctRunnerUp2 })
                {
                    if (!candidate.HasValue) continue;
                    var c = candidate.Value;
                    var r = Vpsg3LocalRefiner.Refine(observation.SparseEdgePoints, floor, scale.SeedScale,
                        c.OffsetX, c.OffsetY, observation.ViewportBounds, observation.Width, observation.Height);
                    var spatial = Vpsg3VerificationGate.EvaluateSpatialVerification(observation.SparseEdgePoints,
                        observation.ValidMask, floor, r.RefinedScale, r.RefinedX, r.RefinedY,
                        observation.ViewportBounds, observation.Width, observation.Height);
                    refined.Add(new(r.RefinedScale, r.RefinedX, r.RefinedY, r.BestScore,
                        spatial.GlobalScore, 0, spatial, r.Probes));
                }
                var best = refined[0];
                evidence.Add(new { sample.Id, scale, candidates, refined });
                for (var k = 1; k <= 3; k++)
                {
                    Vpsg3RefinedCandidate? runner = null;
                    foreach (var r in refined.Skip(1).Take(k - 1))
                    {
                        if (double.Hypot(r.OffsetX - best.OffsetX, r.OffsetY - best.OffsetY) < 6) continue;
                        if (runner is null || r.Spatial.GlobalScore > runner.Value.Spatial.GlobalScore) runner = r;
                    }
                    var gate = Vpsg3VerificationGate.EvaluateDecision(scale, best, runner, runner.HasValue,
                        observation.ViewportBounds, floor.ReferenceWidth, floor.ReferenceHeight);
                    if (!gate.Passed) continue;
                    accepted[k - 1]++;
                    if (Math.Abs(best.Scale - sample.TrueScale) > .035 ||
                        double.Hypot(best.OffsetX - sample.TrueOffsetX, best.OffsetY - sample.TrueOffsetY) > 4)
                        wrong[k - 1]++;
                }
                var production = Vpsg3FastBootstrapSolver.TrySolve(observation, floor);
                if (production.IsAccepted)
                {
                    accepted[3]++;
                    if (Math.Abs(production.Scale - sample.TrueScale) > .035 ||
                        double.Hypot(production.OffsetX - sample.TrueOffsetX, production.OffsetY - sample.TrueOffsetY) > 4)
                        wrong[3]++;
                }
            }
            var report = "Production joint-gate ablation; candidate 1 plus up to two competitors.\n" +
                "K,Accepted,Total,WrongAccept\n" + string.Join("\n", Enumerable.Range(1, 3)
                    .Select(k => $"{k},{accepted[k - 1]},{samples.Count},{wrong[k - 1]}")) +
                $"\n3+pool-refill,{accepted[3]},{samples.Count},{wrong[3]}";
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../scratch"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "vpsg3-production-topk.csv"), report);
            File.WriteAllText(Path.Combine(directory, "vpsg3-production-topk-evidence.json"),
                System.Text.Json.JsonSerializer.Serialize(evidence,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Assert.Equal(0, wrong[2]);
            Assert.Equal(0, wrong[3]);
        }
        finally { foreach (var sample in samples) sample.Dispose(); }
    }
}
