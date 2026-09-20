using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapLearningIncrementalTests
{
    [Theory]
    [InlineData(0.71, 0.30, 0.70, 0.10, true)]
    [InlineData(0.70, 0.09, 0.70, 0.10, true)]
    [InlineData(0.69, 0.01, 0.70, 0.10, false)]
    [InlineData(0.70, 0.11, 0.70, 0.10, false)]
    public void Candidate_ActivatesOnlyForSameSetImprovement(
        double candidateAccuracy,
        double candidateCalibration,
        double parentAccuracy,
        double parentCalibration,
        bool expected)
    {
        Assert.Equal(expected,
            MapCandidateLearningEngine.IsBetterOnSameValidationSet(
                candidateAccuracy,
                candidateCalibration,
                parentAccuracy,
                parentCalibration));
    }

    [Fact]
    public void RollingFloorHoldout_TrainsSingletonAndPreviousCorrection()
    {
        var map = Guid.NewGuid();
        var other = Guid.NewGuid();
        var first = CreateSample(map, other,
            DateTimeOffset.Parse("2026-08-30T08:00:00Z"));
        var singleton = MapCandidateLearningEngine.PartitionSpatialSamples(
            [first]);
        Assert.Single(singleton.Training);
        Assert.Empty(singleton.Validation);
        var second = CreateSample(map, other,
            DateTimeOffset.Parse("2026-08-30T08:05:00Z"));

        var rolling = MapCandidateLearningEngine.PartitionSpatialSamples(
            [first, second]);

        Assert.Equal(first.SampleId, Assert.Single(rolling.Training).SampleId);
        Assert.Equal(second.SampleId,
            Assert.Single(rolling.Validation).SampleId);
    }

    [Theory]
    [InlineData(0, 0.0, 1.0, false)]
    [InlineData(3, 1.0, 0.0, false)]
    [InlineData(4, 0.74, 0.1, false)]
    [InlineData(4, 0.75, 0.21, false)]
    [InlineData(4, 0.75, 0.20, true)]
    public void SpatialQualification_RequiresValidatedRegionQuality(
        int count,
        double accuracy,
        double error,
        bool expected) => Assert.Equal(expected,
            MapCandidateLearningEngine.MeetsSpatialQualification(
                count, accuracy, error));

    [Theory]
    [InlineData(4, 0.80, 0.11, 0.80, 0.10, false)]
    [InlineData(4, 0.79, 0.09, 0.80, 0.10, false)]
    [InlineData(4, 0.80, 0.10, 0.80, 0.10, true)]
    [InlineData(0, 0.00, 1.00, 1.00, 0.00, true)]
    public void ParentActivation_DoesNotRegressTrustedSpatialMetrics(
        int count,
        double candidateAccuracy,
        double candidateError,
        double parentAccuracy,
        double parentError,
        bool expected) => Assert.Equal(expected,
            MapCandidateLearningEngine.SpatialMetricsDoNotRegress(
                count,
                candidateAccuracy,
                candidateError,
                parentAccuracy,
                parentError));

    [Fact]
    public async Task TrainingParent_DoesNotLoadRejectedHigherMetricCandidate()
    {
        var root = CreateTemporaryDirectory();
        var repository = new MapLearningRepository(root);
        repository.EnsureCreated();
        MapModelManifest accepted;
        MapModelManifest rejected;
        using (var network = new SiameseMapNetwork())
        {
            accepted = await repository.CommitModelAsync(network,
                CreateModelDraft() with
                {
                    DatasetRootHash = "accepted",
                    ValidationAccuracy = 0.4,
                    CalibrationError = 0.3,
                    ActivatedAsBestExperimental = true
                }, CancellationToken.None);
        }
        await repository.ActivateExperimentalAsync(accepted.Version,
            CancellationToken.None);
        using (var network = new SiameseMapNetwork())
        {
            rejected = await repository.CommitModelAsync(network,
                CreateModelDraft() with
                {
                    DatasetRootHash = "rejected",
                    ValidationAccuracy = 0.99,
                    CalibrationError = 0.01,
                    ActivatedAsBestExperimental = false
                }, CancellationToken.None);
        }
        await using var engine = new MapCandidateLearningEngine(root);
        var method = typeof(MapCandidateLearningEngine).GetMethod(
            "SelectCompatibleParentAsync",
            System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!;

        var task = Assert.IsAssignableFrom<Task<string?>>(method.Invoke(
            engine, [CancellationToken.None]));
        var selected = await task;

        Assert.NotEqual(rejected.Version, selected);
        Assert.Equal(accepted.Version, selected);
    }

    private static MapLearningSampleManifest CreateSample(
        Guid selectedMap,
        Guid otherMap,
        DateTimeOffset createdAt) => new()
    {
        SampleId = Guid.NewGuid().ToString("N"),
        MatchId = Guid.NewGuid(),
        CreatedAt = createdAt,
        SelectedMapId = selectedMap,
        Candidates =
        [
            new MapLearningCandidateManifest
            {
                MapId = selectedMap,
                FloorKey = "2f",
                IsPositive = true
            },
            new MapLearningCandidateManifest
            {
                MapId = otherMap,
                FloorKey = "2f"
            }
        ]
    };

    private static MapModelManifest CreateModelDraft() => new()
    {
        DatasetRootHash = "test-dataset",
        CreatedAt = DateTimeOffset.UtcNow,
        State = MapModelVersionState.Candidate,
        HumanSelectionCount = 20,
        DistinctMapCount = 3,
        ValidationAccuracy = 0.5,
        CalibrationError = 0.5,
        ActivatedAsBestExperimental = true
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "idvb-map-learning-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
