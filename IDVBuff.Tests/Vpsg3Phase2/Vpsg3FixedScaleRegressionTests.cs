using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3FixedScaleRegressionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void FixedScaleCompetitorsPreserveSeedAndAcceptedGroundTruthGeometry()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        var accepted = 0;
        var maximumError = 0d;
        try
        {
            foreach (var sample in dataset)
            {
                using var observation = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
                using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine,
                    new(Guid.NewGuid(), sample.FloorKey,
                        "fixed-scale-regression", DateTimeOffset.UnixEpoch, "test"));
                var result = Vpsg3FastBootstrapSolver.TrySolve(observation, floor, knownScaleSeed: sample.TrueScale);
                if (result.BestCandidate.ProbesEvaluated > 0)
                    Assert.Equal(sample.TrueScale, result.BestCandidate.Scale);
                if (result.RunnerUpCandidate is { } runner)
                    Assert.Equal(sample.TrueScale, runner.Scale);
                if (!result.IsAccepted)
                    continue;
                accepted++;
                var error = double.Hypot(result.OffsetX - sample.TrueOffsetX, result.OffsetY - sample.TrueOffsetY);
                maximumError = Math.Max(maximumError, error);
                Assert.True(error <= 4d, $"{sample.Id}: fixed-scale accepted translation error {error:F3}px");
            }
            Assert.True(dataset.Count >= 48);
            Assert.True(accepted > 0);
            output.WriteLine($"Fixed scale ground truth: samples={dataset.Count}, accepted={accepted}, wrongAccept=0, maximumTranslationError={maximumError:F3}px");
        }
        finally
        {
            foreach (var sample in dataset)
                sample.Dispose();
        }
    }
}
