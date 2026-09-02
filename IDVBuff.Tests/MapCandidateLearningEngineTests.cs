using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.Runtime.CompilerServices;

namespace IDVBuff.Tests;

public sealed partial class MapCandidateLearningEngineTests
{
    [Fact]
    public void TrainingEntryPoint_DoesNotUseCallerAsyncStateMachine()
    {
        var method = typeof(MapCandidateLearningEngine).GetMethod(
            nameof(MapCandidateLearningEngine.TrainNowAsync))!;

        Assert.Null(method.GetCustomAttributes(
            typeof(AsyncStateMachineAttribute), inherit: false).SingleOrDefault());
    }

    [Fact]
    public void Preprocessing_IsDeterministicAndTwoChannel128Square()
    {
        using var image = new Mat(71, 133, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(20, 12, 60, 35), Scalar.White, 2);

        var first = MapLearningPreprocessor.CreateInput(image);
        var second = MapLearningPreprocessor.CreateInput(image);

        Assert.Equal(2 * 128 * 128, first.Length);
        Assert.Equal(first, second);
        Assert.Contains(first, value => value > 0f);
    }

    [Fact]
    public void TrainingPreprocessing_IsDeterministicAcrossResolutionVariants()
    {
        using var image = new Mat(311, 311, MatType.CV_8UC3, Scalar.Black);
        for (var x = 100; x < 210; x += 13)
            Cv2.Line(image, new Point(x, 95), new Point(x, 215),
                Scalar.White, 1);

        var first = MapLearningPreprocessor.CreateTrainingInputs(image);
        var second = MapLearningPreprocessor.CreateTrainingInputs(image);

        Assert.Equal(9, first.Count);
        Assert.All(first, input => Assert.Equal(2 * 128 * 128, input.Length));
        Assert.Equal(first[0], second[0]);
        Assert.Equal(first[1], second[1]);
        Assert.Equal(first[8], second[8]);
        Assert.NotEqual(first[0], first[8]);
    }

    [Fact]
    public void CanonicalObservation_IsCenteredSquare500AndResolutionIndependent()
    {
        using var source1080 = new Mat(720, 1000, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(source1080, new Rect(280, 140, 440, 440), Scalar.White, -1);
        using var source4k = new Mat();
        Cv2.Resize(source1080, source4k, new Size(2000, 1440),
            interpolation: InterpolationFlags.Nearest);

        using var canonical1080 =
            MapLearningPreprocessor.CreateCanonicalObservation(source1080);
        using var canonical4k =
            MapLearningPreprocessor.CreateCanonicalObservation(source4k);
        var input1080 = MapLearningPreprocessor.CreateInput(canonical1080);
        var input4k = MapLearningPreprocessor.CreateInput(canonical4k);

        Assert.Equal(new Size(500, 500), canonical1080.Size());
        Assert.Equal(new Size(500, 500), canonical4k.Size());
        Assert.Equal(input1080, input4k);
    }

    [Fact]
    public void ReferenceTiles_PreserveFloorLocationAndMultipleScales()
    {
        using var floor = new Mat(500, 500, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(floor, new Rect(35, 40, 70, 60), Scalar.White, -1);

        var tiles = MapLearningPreprocessor.CreateReferenceTiles(floor);

        Assert.Equal(14, tiles.Count);
        Assert.Contains(tiles, tile => tile.CenterX < 0.3d
            && tile.CenterY < 0.3d && tile.Input.Any(value => value > 0f));
        Assert.Contains(tiles, tile => tile.Extent == 1d);
        Assert.All(tiles, tile =>
            Assert.Equal(2 * 128 * 128, tile.Input.Length));
    }

    [Fact]
    public void SpatialTower_DistinguishesEqualStructuresAtDifferentLocations()
    {
        using var upperLeft = new Mat(128, 128,
            MatType.CV_8UC3, Scalar.Black);
        using var lowerRight = new Mat(128, 128,
            MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(upperLeft, new Rect(12, 14, 30, 24), Scalar.White, -1);
        Cv2.Rectangle(lowerRight, new Rect(86, 90, 30, 24), Scalar.White, -1);
        var inputs = new[]
        {
            MapLearningPreprocessor.CreateInput(upperLeft),
            MapLearningPreprocessor.CreateInput(lowerRight)
        };
        using var network = new SiameseMapNetwork();
        using var tensor = SiameseMapNetwork.ToTensor(inputs);
        using var noGrad = TorchSharp.torch.no_grad();
        using var embeddings = network.EncodeLive(tensor);
        using var first = embeddings.narrow(0, 0, 1);
        using var second = embeddings.narrow(0, 1, 1);
        using var difference = (first - second).abs().sum();

        Assert.True(difference.item<float>() > 0.0001f);
    }

    [Fact]
    public void ReferenceEncoding_KeepsEntireRectangularFloor()
    {
        using var floor = new Mat(200, 400, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(floor, new Rect(0, 60, 30, 80), Scalar.White, -1);
        Cv2.Rectangle(floor, new Rect(370, 60, 30, 80), Scalar.White, -1);

        var bytes = MapLearningPreprocessor.EncodeReferenceFloorPng(floor);
        using var encoded = Cv2.ImDecode(bytes, ImreadModes.Color);

        Assert.Equal(new Size(500, 500), encoded.Size());
        Assert.True(encoded.At<Vec3b>(250, 5).Item0 > 0);
        Assert.True(encoded.At<Vec3b>(250, 494).Item0 > 0);
    }

    [Fact]
    public async Task ModelOnlyWithoutModel_PreservesTraditionalOrderAndReportsFallback()
    {
        var root = CreateTemporaryDirectory();
        await using var engine = new MapCandidateLearningEngine(root);
        await engine.InitializeAsync();
        using var live = new Mat(128, 128, MatType.CV_8UC3, Scalar.Black);
        var choices = new[]
        {
            CreateChoice(sequence: 7, confidence: 0.9),
            CreateChoice(sequence: 2, confidence: 0.1),
            CreateChoice(sequence: 5, confidence: 0.6)
        };

        var result = await engine.ScoreAsync(
            live, choices, MapCandidateDecisionMode.ModelOnly);

        Assert.False(result.ModelAvailable);
        Assert.Equal([7, 2, 5], result.Choices
            .Select(choice => choice.Recognition.Map.SequenceNumber));
        Assert.True(result.FellBackToTraditionalOrdering);
        Assert.All(result.Choices, choice =>
        {
            Assert.Null(choice.ModelProbability);
            Assert.NotEmpty(choice.ModelFailureReason);
        });
    }

    [Fact]
    public async Task EmptyTrainingSet_DoesNotCreateOrPromoteCurrentVersion()
    {
        var root = CreateTemporaryDirectory();
        await using var engine = new MapCandidateLearningEngine(root);
        await engine.InitializeAsync();

        var result = await engine.TrainNowAsync();

        Assert.False(result.Trained);
        Assert.False(result.Promoted);
        Assert.False(File.Exists(Path.Combine(root, "CURRENT")));
        Assert.Contains("人工标注", result.Reason);
    }

    [Fact]
    public async Task UnqualifiedModelOnly_ScoresButCannotReorderTraditionalChoices()
    {
        var root = CreateTemporaryDirectory();
        var referencePath = Path.Combine(root, "reference.png");
        using (var reference = new Mat(500, 500,
                   MatType.CV_8UC3, Scalar.White))
            Cv2.ImWrite(referencePath, reference);
        var repository = new MapLearningRepository(root);
        repository.EnsureCreated();
        using (var network = new SiameseMapNetwork())
        {
            var candidate = await repository.CommitModelAsync(network,
                CreateModelDraft(qualified: false), CancellationToken.None);
            await repository.RestoreAsync(candidate.Version, "test",
                CancellationToken.None);
        }
        await using var engine = new MapCandidateLearningEngine(root);
        await engine.InitializeAsync();
        using var live = new Mat(500, 500, MatType.CV_8UC3, Scalar.Black);
        var choices = new[]
        {
            CreateChoice(9, 0.9, referencePath),
            CreateChoice(2, 0.5, referencePath),
            CreateChoice(5, 0.2, referencePath)
        };

        var result = await engine.ScoreAsync(
            live, choices, MapCandidateDecisionMode.ModelOnly);

        Assert.True(result.ModelAvailable);
        Assert.False(result.ModelQualified);
        Assert.True(result.FellBackToTraditionalOrdering);
        Assert.Equal([9, 2, 5], result.Choices.Select(choice =>
            choice.Recognition.Map.SequenceNumber));
        Assert.All(result.Choices, choice =>
            Assert.NotNull(choice.ModelMatchedCenterX));
    }

    [Fact]
    public async Task TrainingParent_SelectsBestCompatibleCandidateWithoutCurrent()
    {
        var root = CreateTemporaryDirectory();
        var repository = new MapLearningRepository(root);
        repository.EnsureCreated();
        MapModelManifest weaker;
        MapModelManifest better;
        using (var network = new SiameseMapNetwork())
        {
            weaker = await repository.CommitModelAsync(network,
                CreateModelDraft(qualified: false) with
                {
                    DatasetRootHash = "weaker",
                    ValidationAccuracy = 0.4,
                    CalibrationError = 0.3
                }, CancellationToken.None);
        }
        using (var network = new SiameseMapNetwork())
        {
            better = await repository.CommitModelAsync(network,
                CreateModelDraft(qualified: false) with
                {
                    DatasetRootHash = "better",
                    ValidationAccuracy = 0.7,
                    CalibrationError = 0.2
                }, CancellationToken.None);
        }
        await repository.ActivateExperimentalAsync(better.Version,
            CancellationToken.None);
        await using var engine = new MapCandidateLearningEngine(root);
        var method = typeof(MapCandidateLearningEngine).GetMethod(
            "SelectCompatibleParentAsync",
            System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!;

        var task = Assert.IsAssignableFrom<Task<string?>>(method.Invoke(
            engine, [CancellationToken.None]));
        var selected = await task;

        Assert.NotEqual(weaker.Version, selected);
        Assert.Equal(better.Version, selected);
    }

    [Fact]
    public void MatchSplit_IsDeterministicAndNeverSplitsOneMatch()
    {
        var method = typeof(MapLearningRepository).GetMethod(
            "ResolveSplit",
            System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!;
        var match = Guid.Parse("5480f5ed-2484-49e3-b00f-6f86a6c96144");

        var first = method.Invoke(null, [match]);
        var second = method.Invoke(null, [match]);

        Assert.Equal(first, second);
        Assert.Contains((string)first!, new[] { "train", "validation" });
    }

    [Fact]
    public void LaterCorrectionBecomesOnlyAuthoritativeViewForMatch()
    {
        var matchId = Guid.NewGuid();
        var wrongMap = Guid.NewGuid();
        var correctedMap = Guid.NewGuid();
        var earlier = CreateSample(matchId, wrongMap, correctedMap,
            DateTimeOffset.Parse("2026-08-30T08:00:00Z"));
        var correction = CreateSample(matchId, correctedMap, wrongMap,
            DateTimeOffset.Parse("2026-08-30T08:05:00Z"));
        var effective = MapLearningSampleRules.LatestPerMatch(
            [earlier, correction]);

        var authoritative = Assert.Single(effective);
        Assert.Equal(correction.SampleId, authoritative.SampleId);
        Assert.Equal(correctedMap, authoritative.SelectedMapId);
        Assert.Single(authoritative.Candidates,
            candidate => candidate.IsPositive && candidate.MapId == correctedMap);
    }

    [Fact]
    public async Task HumanSelectionCreatesOnePositiveAndHardNegatives()
    {
        var root = CreateTemporaryDirectory();
        var referencePath = Path.Combine(root, "reference.png");
        using (var reference = new Mat(128, 128, MatType.CV_8UC3, Scalar.White))
            Cv2.ImWrite(referencePath, reference);
        var choices = new[]
        {
            CreateChoice(sequence: 1, confidence: 0.8, referencePath: referencePath),
            CreateChoice(sequence: 2, confidence: 0.7, referencePath: referencePath),
            CreateChoice(sequence: 3, confidence: 0.6, referencePath: referencePath)
        };
        var selectedMap = choices[1].Recognition.Map.Id;
        using var live = new Mat(128, 128, MatType.CV_8UC3, Scalar.Black);
        await using var engine = new MapCandidateLearningEngine(root);
        await engine.InitializeAsync();

        await engine.RecordHumanSelectionAsync(
            Guid.NewGuid(), live, choices, selectedMap);

        var repository = new MapLearningRepository(root);
        var sample = Assert.Single(await repository.LoadSamplesAsync(
            CancellationToken.None));
        Assert.Equal(selectedMap, sample.SelectedMapId);
        Assert.Single(sample.Candidates, candidate => candidate.IsPositive);
        Assert.Equal(2, sample.Candidates.Count(candidate => !candidate.IsPositive));
        Assert.All(sample.Candidates, candidate =>
            Assert.Equal("1f", candidate.FloorKey));
        Assert.Equal(2, sample.SchemaVersion);
        Assert.All(sample.Candidates, candidate =>
        {
            Assert.Equal("floor", candidate.ReferenceScope);
            Assert.Equal(500, candidate.ReferenceWidth);
            Assert.Equal(500, candidate.ReferenceHeight);
        });
    }

    [Fact]
    public async Task ExportedTrainingPackagePassesIntegrityAndPrivacyValidation()
    {
        var root = CreateTemporaryDirectory();
        var referencePath = Path.Combine(root, "reference.png");
        using (var reference = new Mat(720, 960, MatType.CV_8UC3, Scalar.White))
            Cv2.ImWrite(referencePath, reference);
        var choices = new[]
        {
            CreateChoice(sequence: 1, confidence: 0.8, referencePath: referencePath),
            CreateChoice(sequence: 2, confidence: 0.7, referencePath: referencePath)
        };
        var packagePath = Path.Combine(root, "training.zip");
        using var live = new Mat(640, 900, MatType.CV_8UC3, Scalar.Black);
        await using var engine = new MapCandidateLearningEngine(root);
        await engine.InitializeAsync();
        await engine.RecordHumanSelectionAsync(
            Guid.NewGuid(), live, choices, choices[0].Recognition.Map.Id);

        await engine.ExportAsync(packagePath);
        var validation = MapLearningExportValidator.Validate(packagePath);

        Assert.True(validation.IsValid, validation.Message);
        Assert.Equal(1, validation.SampleCount);
        Assert.Matches("^[0-9a-f]{64}$", validation.DatasetFingerprint);
    }

    [Fact]
    public async Task CorruptCurrentModelFallsBackToLastKnownGood()
    {
        var root = CreateTemporaryDirectory();
        var repository = new MapLearningRepository(root);
        repository.EnsureCreated();
        using var stableNetwork = new SiameseMapNetwork();
        var stable = await repository.CommitModelAsync(stableNetwork,
            CreateModelDraft(qualified: true), CancellationToken.None);
        await repository.PromoteAsync(stable, CancellationToken.None);
        using var badNetwork = new SiameseMapNetwork();
        var bad = await repository.CommitModelAsync(badNetwork,
            CreateModelDraft(qualified: false), CancellationToken.None);
        Assert.Matches(@"^m01\.0-\d{2}\.\d{2}\.\d{2}\.0001-[0-9a-f]{8}$",
            stable.Version);
        Assert.Matches(@"^m01\.0-\d{2}\.\d{2}\.\d{2}\.0002-[0-9a-f]{8}$",
            bad.Version);
        await File.WriteAllTextAsync(repository.CurrentReferencePath, bad.Version);
        await File.AppendAllTextAsync(Path.Combine(
            repository.GetModelDirectory(bad.Version),
            SiameseMapNetwork.WeightFileNames[0]), "broken");

        await using var engine = new MapCandidateLearningEngine(root);
        await engine.InitializeAsync();

        Assert.Equal(stable.Version, engine.Status.CurrentVersion);
        Assert.Equal(stable.Version, engine.Status.LastKnownGoodVersion);
        Assert.Contains("校验失败", engine.Status.LastRollbackReason);
    }

    private static MapRecognitionChoice CreateChoice(
        int sequence,
        double confidence,
        string referencePath = "")
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            SequenceNumber = sequence,
            Title = $"Map {sequence}",
            Class = "S1"
        };
        map.NormalizeRecognition();
        return new MapRecognitionChoice
        {
            Recognition = new RuntimeMapRecognition
            {
                Map = map,
                FloorImagePath = referencePath,
                Result = new MapRecognitionResult
                {
                    MapId = map.Id,
                    Floor = "1f",
                    Confidence = confidence
                }
            },
            EvidenceScore = confidence
        };
    }

    private static MapModelManifest CreateModelDraft(bool qualified) => new()
    {
        DatasetRootHash = "test-dataset",
        CreatedAt = DateTimeOffset.UtcNow,
        State = MapModelVersionState.Candidate,
        IsQualified = qualified,
        HumanSelectionCount = 20,
        DistinctMapCount = 3,
        ValidationAccuracy = qualified ? 0.96 : 0.5,
        CalibrationError = qualified ? 0.05 : 0.5,
        ActivatedAsBestExperimental = true
    };

    private static MapLearningSampleManifest CreateSample(
        Guid matchId,
        Guid selectedMap,
        Guid otherMap,
        DateTimeOffset createdAt) => new()
    {
        SampleId = Guid.NewGuid().ToString("N"),
        MatchId = matchId,
        CreatedAt = createdAt,
        SelectedMapId = selectedMap,
        Candidates =
        [
            new MapLearningCandidateManifest
            {
                MapId = selectedMap,
                IsPositive = true
            },
            new MapLearningCandidateManifest
            {
                MapId = otherMap,
                IsPositive = false
            }
        ]
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "idvb-map-learning-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
