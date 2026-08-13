using IDVBuff.Survey.Application;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using IDVBuff.Survey.Persistence.Sqlite;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(SurveyLayerEditingTestCollection.Name)]
public sealed class SurveyImportTests
{
    [Fact]
    public async Task FirstImportBecomesRegisteredFloorRoot()
    {
        var root = CreateTempRoot();
        try
        {
            await using var coordinator = CreateCoordinator(root);
            var started = (await coordinator.StartAsync(CreateStart("import root test"))).Value!;

            var result = (await coordinator.ImportObservationAsync(
                CreateImport(started, CreateSolidPng(64, 64, new Scalar(20, 60, 200)), "1f"))).Value!;

            Assert.False(result.WasAlreadyCommitted);
            Assert.Equal(SurveyObservationState.Registered, result.Observation.State);
            Assert.Equal(1d, result.Observation.Quality);
            Assert.Equal(SurveyErrorCode.None, result.Observation.ErrorCode);
            Assert.Equal(0, result.Layer.ZOrder);
            Assert.False(result.Layer.IsDeleted);
            Assert.True(result.Layer.IsVisible);
            Assert.Equal(SurveyLayerTransform.Identity, result.Layer.EffectiveTransform);
            Assert.Contains(result.Snapshot.Floors,
                floor => string.Equals(floor.FloorKey, "1f", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SecondImportRemainsUnregistered()
    {
        var root = CreateTempRoot();
        try
        {
            await using var coordinator = CreateCoordinator(root);
            var started = (await coordinator.StartAsync(CreateStart("import unregistered test"))).Value!;
            var first = (await coordinator.ImportObservationAsync(
                CreateImport(started, CreateSolidPng(64, 64, new Scalar(20, 60, 200)), "1f"))).Value!;

            var second = (await coordinator.ImportObservationAsync(
                CreateImport(first.Snapshot, CreateSolidPng(64, 64, new Scalar(200, 60, 20)), "1f"))).Value!;

            Assert.Equal(SurveyObservationState.Unregistered, second.Observation.State);
            Assert.Equal(0d, second.Observation.Quality);
            Assert.Equal(SurveyErrorCode.RegistrationRejected, second.Observation.ErrorCode);
            Assert.Equal(1, second.Layer.ZOrder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportTargetsChosenFloor()
    {
        var root = CreateTempRoot();
        try
        {
            await using var coordinator = CreateCoordinator(root);
            var started = (await coordinator.StartAsync(CreateStart("import floor test"))).Value!;
            var first = (await coordinator.ImportObservationAsync(
                CreateImport(started, CreateSolidPng(64, 64, Scalar.Gray), "1f"))).Value!;

            var second = (await coordinator.ImportObservationAsync(
                CreateImport(first.Snapshot, CreateSolidPng(64, 64, Scalar.White), "2f"))).Value!;

            Assert.NotEqual(first.Observation.FloorId, second.Observation.FloorId);
            Assert.Equal("2f", second.Observation.Capture.FloorKey);
            Assert.Contains(second.Snapshot.Floors,
                floor => string.Equals(floor.FloorKey, "2f", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IdenticalImportToSameFloorDeduplicates()
    {
        var root = CreateTempRoot();
        try
        {
            await using var coordinator = CreateCoordinator(root);
            var started = (await coordinator.StartAsync(CreateStart("import dedup test"))).Value!;
            var bytes = CreateSolidPng(64, 64, new Scalar(90, 140, 30));
            var first = (await coordinator.ImportObservationAsync(
                CreateImport(started, bytes, "1f"))).Value!;

            var second = (await coordinator.ImportObservationAsync(
                CreateImport(first.Snapshot, bytes, "1f"))).Value!;

            Assert.True(second.WasAlreadyCommitted);
            Assert.Single(second.Snapshot.Observations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IdenticalImportToDifferentFloorIsNotDeduplicated()
    {
        var root = CreateTempRoot();
        try
        {
            await using var coordinator = CreateCoordinator(root);
            var started = (await coordinator.StartAsync(CreateStart("import cross-floor test"))).Value!;
            var bytes = CreateSolidPng(64, 64, new Scalar(60, 160, 90));
            var first = (await coordinator.ImportObservationAsync(
                CreateImport(started, bytes, "1f"))).Value!;

            var second = (await coordinator.ImportObservationAsync(
                CreateImport(first.Snapshot, bytes, "2f"))).Value!;

            Assert.False(second.WasAlreadyCommitted);
            Assert.Equal(2, second.Snapshot.Observations.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportRejectsArchivedProject()
    {
        var root = CreateTempRoot();
        try
        {
            await using var coordinator = CreateCoordinator(root);
            var started = (await coordinator.StartAsync(CreateStart("import archived test"))).Value!;
            var archived = (await coordinator.SetProjectStateAsync(
                new SurveyProjectStateRequest(
                    Guid.NewGuid(),
                    started.Project.ProjectId,
                    started.Project.Revision,
                    SurveyProjectState.Archived))).Value!;

            var result = await coordinator.ImportObservationAsync(
                CreateImport(archived, CreateSolidPng(64, 64, Scalar.Black), "1f"));

            Assert.False(result.Succeeded);
            Assert.Equal(SurveyErrorCode.ProjectArchived, result.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportPreservesRuntimeState()
    {
        var root = CreateTempRoot();
        try
        {
            await using var coordinator = CreateCoordinator(root);
            var started = (await coordinator.StartAsync(CreateStart("import runtime test"))).Value!;
            Assert.Equal(SurveyRuntimeState.WaitingForMapOpen, coordinator.Status.RuntimeState);

            var result = (await coordinator.ImportObservationAsync(
                CreateImport(started, CreateSolidPng(64, 64, Scalar.White), "1f"))).Value!;

            Assert.Equal(SurveyRuntimeState.WaitingForMapOpen, coordinator.Status.RuntimeState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportWorksWithoutActiveGameSession()
    {
        var root = CreateTempRoot();
        try
        {
            await using var coordinator = CreateCoordinator(root);
            var matchId = Guid.NewGuid();
            var started = (await coordinator.StartAsync(CreateStart("import offline test", matchId))).Value!;
            var ended = (await coordinator.EndAsync(new SurveyEndRequest(
                Guid.NewGuid(),
                started.Project.ProjectId,
                started.Project.Revision,
                matchId,
                1))).Value!;

            var result = await coordinator.ImportObservationAsync(
                CreateImport(ended, CreateSolidPng(64, 64, Scalar.White), "1f"));

            Assert.True(result.Succeeded);
            Assert.False(result.Value!.WasAlreadyCommitted);
            Assert.Equal(SurveyRuntimeState.Inactive, coordinator.Status.RuntimeState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportLayerNameFallbackAndOverride()
    {
        var root = CreateTempRoot();
        try
        {
            await using var coordinator = CreateCoordinator(root);
            var started = (await coordinator.StartAsync(CreateStart("import name test"))).Value!;
            var fallback = (await coordinator.ImportObservationAsync(
                CreateImport(started, CreateSolidPng(64, 64, Scalar.Gray), "1f"))).Value!;
            Assert.Equal("测绘图层 1", fallback.Layer.Name);

            var overrideResult = (await coordinator.ImportObservationAsync(
                CreateImport(fallback.Snapshot, CreateSolidPng(64, 64, Scalar.White), "1f", "my floor"))).Value!;
            Assert.Equal("my floor", overrideResult.Layer.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportRoundTripsThroughSnapshot()
    {
        var root = CreateTempRoot();
        try
        {
            await using var coordinator = CreateCoordinator(root);
            var started = (await coordinator.StartAsync(CreateStart("import roundtrip test"))).Value!;
            var imported = (await coordinator.ImportObservationAsync(
                CreateImport(started, CreateSolidPng(64, 64, Scalar.White), "1f"))).Value!;

            var read = (await coordinator.GetProjectAsync(imported.Snapshot.Project.ProjectId))!;
            var observation = read.Observations.Single(item => item.ObservationId == imported.Observation.ObservationId);
            Assert.Equal(Guid.Empty, observation.Capture.MatchId);
            Assert.Equal(0, observation.Capture.OperationEpoch);
            Assert.Equal(0, observation.Capture.MapToggleVersion);
            Assert.Equal("1f", observation.Capture.FloorKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SurveyCoordinator CreateCoordinator(string root)
    {
        var paths = new SurveyStoragePaths(root);
        var repository = new SqliteSurveyProjectRepository(paths);
        var assets = new ContentAddressedSurveyAssetStore(paths);
        return new SurveyCoordinator(repository, assets);
    }

    private static SurveyStartRequest CreateStart(string name, Guid? matchId = null) => new(
        Guid.NewGuid(),
        matchId ?? Guid.NewGuid(),
        1,
        "S1",
        "1f",
        name,
        new string('a', 64),
        "test");

    private static SurveyObservationImportRequest CreateImport(
        SurveyProjectSnapshot project,
        byte[] bytes,
        string floorKey,
        string? layerName = null) => new(
        Guid.NewGuid(),
        project.Project.ProjectId,
        project.Project.Revision,
        new SurveyEncodedFrame(
            bytes,
            ".png",
            "image/png",
            64,
            64,
            new SurveyCaptureContext(
                Guid.Empty,
                0,
                0,
                DateTimeOffset.UtcNow,
                64,
                64,
                96,
                new SurveyPixelRect(0, 0, 64, 64),
                floorKey,
                project.Project.ConfigDigest,
                project.Project.AlgorithmVersion)),
        layerName);

    private static byte[] CreateSolidPng(int width, int height, Scalar color)
    {
        using var image = new Mat(new Size(width, height), MatType.CV_8UC3, color);
        Cv2.ImEncode(".png", image, out var bytes);
        return bytes;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "idvb-survey-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
