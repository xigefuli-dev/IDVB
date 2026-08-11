using IDVBuff.Survey.Application;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using IDVBuff.Survey.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace IDVBuff.Tests;

public sealed class SurveyProjectPersistenceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "idvb-survey-tests",
        Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ObservationAndLayerEditsSurviveRepositoryRestart()
    {
        var paths = new SurveyStoragePaths(_root);
        var repository = new SqliteSurveyProjectRepository(paths);
        var assets = new ContentAddressedSurveyAssetStore(paths);
        await using var coordinator = new SurveyCoordinator(repository, assets);
        var matchId = Guid.NewGuid();
        var start = await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(),
            matchId,
            2,
            "S1",
            "1f",
            "real persistence test",
            new string('a', 64),
            "1.0.0"));
        Assert.True(start.Succeeded);
        var project = Assert.IsType<SurveyProjectSnapshot>(start.Value);

        var capture = CreateCapture(matchId, operationEpoch: 2, toggleVersion: 7);
        var observation = await coordinator.AddObservationAsync(
            CreateObservationRequest(project, capture));
        Assert.True(observation.Succeeded);
        var committed = Assert.IsType<SurveyObservationCommitResult>(observation.Value);
        Assert.Single(committed.Snapshot.Observations);
        Assert.Single(committed.Snapshot.Layers);
        Assert.Equal(SurveyObservationState.Registered, committed.Observation.State);

        var transform = new SurveyLayerTransform(125, -32, 12.5, 1.2, 0.9);
        var edited = await coordinator.EditLayerAsync(new SurveyLayerEditRequest(
            Guid.NewGuid(),
            project.Project.ProjectId,
            committed.Layer.LayerId,
            committed.Snapshot.Project.Revision,
            ManualTransformOverride: transform,
            Opacity: 0.42,
            ZOrder: 9,
            IsVisible: false));
        Assert.True(edited.Succeeded);

        var restarted = new SqliteSurveyProjectRepository(paths);
        await restarted.InitializeAsync();
        var restored = await restarted.GetAsync(project.Project.ProjectId);
        Assert.NotNull(restored);
        var layer = Assert.Single(restored.Layers);
        Assert.Equal(transform, layer.ManualTransformOverride);
        Assert.Equal(transform, layer.EffectiveTransform);
        Assert.Equal(0.42, layer.Opacity, 6);
        Assert.Equal(9, layer.ZOrder);
        Assert.False(layer.IsVisible);
    }

    [Fact]
    public async Task SameMapOpenEventIsCommittedExactlyOnce()
    {
        var paths = new SurveyStoragePaths(_root);
        var repository = new SqliteSurveyProjectRepository(paths);
        await using var coordinator = new SurveyCoordinator(
            repository,
            new ContentAddressedSurveyAssetStore(paths));
        var matchId = Guid.NewGuid();
        var start = await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(), matchId, 4, "S1", "1f", null,
            new string('b', 64), "1.0.0"));
        var project = Assert.IsType<SurveyProjectSnapshot>(start.Value);
        var capture = CreateCapture(matchId, operationEpoch: 4, toggleVersion: 11);
        var request = CreateObservationRequest(project, capture);

        var first = await coordinator.AddObservationAsync(request);
        var second = await coordinator.AddObservationAsync(request with { CommandId = Guid.NewGuid() });

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.False(first.Value!.WasAlreadyCommitted);
        Assert.True(second.Value!.WasAlreadyCommitted);
        Assert.Single(second.Value.Snapshot.Observations);
        Assert.Single(second.Value.Snapshot.Layers);
    }

    [Fact]
    public async Task StaleLayerRevisionIsRejectedWithoutOverwritingData()
    {
        var paths = new SurveyStoragePaths(_root);
        await using var coordinator = new SurveyCoordinator(
            new SqliteSurveyProjectRepository(paths),
            new ContentAddressedSurveyAssetStore(paths));
        var matchId = Guid.NewGuid();
        var start = await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(), matchId, 6, "S1", "1f", null,
            new string('c', 64), "1.0.0"));
        var project = start.Value!;
        var observation = await coordinator.AddObservationAsync(
            CreateObservationRequest(project, CreateCapture(matchId, 6, 12)));
        var committed = observation.Value!;

        var stale = await coordinator.EditLayerAsync(new SurveyLayerEditRequest(
            Guid.NewGuid(),
            project.Project.ProjectId,
            committed.Layer.LayerId,
            project.Project.Revision,
            Opacity: 0.1));

        Assert.False(stale.Succeeded);
        Assert.Equal(SurveyErrorCode.RevisionConflict, stale.ErrorCode);
        var restored = await coordinator.GetProjectAsync(project.Project.ProjectId);
        Assert.Equal(1d, Assert.Single(restored!.Layers).Opacity);
    }

    [Fact]
    public async Task DuplicateProjectGetsIndependentIdentityAndAssets()
    {
        var paths = new SurveyStoragePaths(_root);
        await using var coordinator = new SurveyCoordinator(
            new SqliteSurveyProjectRepository(paths),
            new ContentAddressedSurveyAssetStore(paths));
        var matchId = Guid.NewGuid();
        var start = await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(), matchId, 8, "S1", "1f", "source project",
            new string('e', 64), "1.0.0"));
        var source = start.Value!;
        var observation = await coordinator.AddObservationAsync(
            CreateObservationRequest(source, CreateCapture(matchId, 8, 15)));
        Assert.True(observation.Succeeded);

        var duplicated = await coordinator.DuplicateProjectAsync(source.Project.ProjectId);

        Assert.True(duplicated.Succeeded);
        var copy = duplicated.Value!;
        Assert.NotEqual(source.Project.ProjectId, copy.Project.ProjectId);
        Assert.Equal("source project 副本", copy.Project.Name);
        Assert.Equal(SurveyProjectState.NeedsReview, copy.Project.State);
        Assert.Equal(1, copy.Project.Revision);
        Assert.Null(copy.Project.PublishedRevision);
        Assert.Single(copy.Observations);
        Assert.Single(copy.Layers);
        Assert.Equal(copy.Project.ProjectId, copy.Observations[0].ProjectId);
        Assert.Equal(copy.Project.ProjectId, copy.Layers[0].ProjectId);
        Assert.Equal(observation.Value!.Observation.SourceAsset.Sha256,
            copy.Observations[0].SourceAsset.Sha256);
        await using var copiedAsset = await coordinator.OpenAssetAsync(
            copy.Project.ProjectId,
            copy.Observations[0].SourceAsset);
        using var memory = new MemoryStream();
        await copiedAsset.CopyToAsync(memory);
        Assert.Equal(OnePixelPng, memory.ToArray());
    }

    [Fact]
    public async Task FailedCaptureIsPersistedWithoutCreatingAnEmptyLayer()
    {
        var paths = new SurveyStoragePaths(_root);
        await using var coordinator = new SurveyCoordinator(
            new SqliteSurveyProjectRepository(paths),
            new ContentAddressedSurveyAssetStore(paths));
        var matchId = Guid.NewGuid();
        var start = (await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(), matchId, 9, "S1", "1f", null,
            new string('f', 64), "1.0.0"))).Value!;

        var recorded = await coordinator.RecordCaptureFailureAsync(
            new SurveyCaptureFailureRequest(
                Guid.NewGuid(),
                start.Project.ProjectId,
                start.Project.Revision,
                matchId,
                9,
                22,
                "1f",
                DateTimeOffset.UtcNow,
                SurveyErrorCode.CaptureFailed,
                "map closed while waiting"));

        Assert.True(recorded.Succeeded);
        Assert.Empty(recorded.Value!.Observations);
        Assert.Empty(recorded.Value.Layers);
        Assert.Equal(start.Project.Revision + 1, recorded.Value.Project.Revision);
        await using var connection = new SqliteConnection(
            $"Data Source={paths.DatabasePath(start.Project.ProjectId)};Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM capture_attempts WHERE project_id = $id;";
        command.Parameters.AddWithValue("$id", start.Project.ProjectId.ToString("N"));
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task MetadataCanBeEditedButArchivedProjectIsReadOnly()
    {
        var paths = new SurveyStoragePaths(_root);
        await using var coordinator = new SurveyCoordinator(
            new SqliteSurveyProjectRepository(paths),
            new ContentAddressedSurveyAssetStore(paths));
        var matchId = Guid.NewGuid();
        var started = (await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(), matchId, 10, "S1", "1f", "before",
            new string('1', 64), "1.0.0"))).Value!;
        var metadata = await coordinator.UpdateMetadataAsync(
            new SurveyProjectMetadataRequest(
                Guid.NewGuid(),
                started.Project.ProjectId,
                started.Project.Revision,
                "after",
                "S2",
                started.Floors[0].FloorId,
                "Ground"));
        Assert.True(metadata.Succeeded);
        Assert.Equal("after", metadata.Value!.Project.Name);
        Assert.Equal("S2", metadata.Value.Project.MapClass);
        Assert.Equal("Ground", metadata.Value.Floors[0].DisplayName);

        var observation = await coordinator.AddObservationAsync(
            CreateObservationRequest(
                metadata.Value,
                CreateCapture(matchId, 10, 23)));
        var archived = await coordinator.SetProjectStateAsync(
            new SurveyProjectStateRequest(
                Guid.NewGuid(),
                started.Project.ProjectId,
                observation.Value!.Snapshot.Project.Revision,
                SurveyProjectState.Archived));
        var edit = await coordinator.EditLayerAsync(new SurveyLayerEditRequest(
            Guid.NewGuid(),
            started.Project.ProjectId,
            observation.Value.Layer.LayerId,
            archived.Value!.Project.Revision,
            Opacity: 0.25));
        Assert.False(edit.Succeeded);
        Assert.Equal(SurveyErrorCode.ProjectArchived, edit.ErrorCode);
    }

    [Fact]
    public async Task ReorderCommandPersistsAnExactUniqueLayerOrder()
    {
        var paths = new SurveyStoragePaths(_root);
        await using var coordinator = new SurveyCoordinator(
            new SqliteSurveyProjectRepository(paths),
            new ContentAddressedSurveyAssetStore(paths));
        var matchId = Guid.NewGuid();
        var started = (await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(), matchId, 12, "S1", "1f", null,
            new string('2', 64), "1.0.0"))).Value!;
        var first = (await coordinator.AddObservationAsync(
            CreateObservationRequest(started, CreateCapture(matchId, 12, 30)))).Value!;
        var second = (await coordinator.AddObservationAsync(
            CreateObservationRequest(first.Snapshot, CreateCapture(matchId, 12, 31)))).Value!;
        var floor = second.Snapshot.Floors[0];

        var reordered = await coordinator.ReorderLayersAsync(new SurveyLayerOrderRequest(
            Guid.NewGuid(),
            started.Project.ProjectId,
            floor.FloorId,
            second.Snapshot.Project.Revision,
            [first.Layer.LayerId, second.Layer.LayerId]));

        Assert.True(reordered.Succeeded);
        Assert.Equal(
            [first.Layer.LayerId, second.Layer.LayerId],
            reordered.Value!.Layers.Where(layer => !layer.IsDeleted)
                .OrderByDescending(layer => layer.ZOrder)
                .Select(layer => layer.LayerId));
        Assert.Equal(2, reordered.Value.Layers.Select(layer => layer.ZOrder).Distinct().Count());
    }

    [Fact]
    public async Task OlderSchemaIsBackedUpAndMigratedWithoutLosingTheProject()
    {
        var paths = new SurveyStoragePaths(_root);
        Guid projectId;
        await using (var coordinator = new SurveyCoordinator(
            new SqliteSurveyProjectRepository(paths),
            new ContentAddressedSurveyAssetStore(paths)))
        {
            var started = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), Guid.NewGuid(), 1, "S1", "1f", "migration test",
                new string('3', 64), "1.0.0"))).Value!;
            projectId = started.Project.ProjectId;
        }

        await using (var connection = new SqliteConnection(
            $"Data Source={paths.DatabasePath(projectId)};Pooling=False"))
        {
            await connection.OpenAsync();
            var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                UPDATE meta SET value = '1' WHERE key = 'survey_schema_version';
                DROP TABLE capture_attempts;
                DROP TABLE observation_assets;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var restarted = new SqliteSurveyProjectRepository(paths);
        var restored = await restarted.GetAsync(projectId);

        Assert.NotNull(restored);
        Assert.Equal("migration test", restored.Project.Name);
        Assert.Single(Directory.EnumerateFiles(
            paths.ProjectDirectory(projectId),
            "project.schema-v1.*.bak"));
        await using var verification = new SqliteConnection(
            $"Data Source={paths.DatabasePath(projectId)};Mode=ReadOnly;Pooling=False");
        await verification.OpenAsync();
        var schema = verification.CreateCommand();
        schema.CommandText = "SELECT value FROM meta WHERE key = 'survey_schema_version';";
        Assert.Equal("5", Convert.ToString(await schema.ExecuteScalarAsync()));
        var tables = verification.CreateCommand();
        tables.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name IN ('capture_attempts', 'observation_assets');
            """;
        Assert.Equal(2L, Convert.ToInt64(await tables.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task V4MigrationKeepsExistingDisplayAssetsSelected()
    {
        var paths = new SurveyStoragePaths(_root);
        var repository = new SqliteSurveyProjectRepository(paths);
        var assets = new ContentAddressedSurveyAssetStore(paths);
        Guid projectId;
        await using (var coordinator = new SurveyCoordinator(repository, assets))
        {
            var matchId = Guid.NewGuid();
            var started = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), matchId, 1, "S1", "1f", "v4 migration",
                new string('4', 64), "test"))).Value!;
            projectId = started.Project.ProjectId;
            var committed = (await coordinator.AddObservationAsync(
                CreateObservationRequest(started, CreateCapture(matchId, 1, 1)))).Value!;
            var display = await assets.PutAsync(projectId, committed.Snapshot.Observations[0].Capture is { } capture
                ? new SurveyEncodedFrame(OnePixelPng, ".png", "image/png", 1, 1, capture)
                : throw new InvalidOperationException());
            await repository.CommitProcessingAsync(new SurveyProcessingCommitRequest(
                Guid.NewGuid(),
                projectId,
                committed.Observation.ObservationId,
                committed.Layer.LayerId,
                committed.Snapshot.Project.Revision,
                committed.Observation.State,
                committed.Observation.Quality,
                committed.Observation.ErrorCode,
                committed.Observation.ErrorMessage,
                committed.Layer.AutomaticTransform,
                null,
                DisplayAsset: display));
        }

        await using (var connection = new SqliteConnection(
            $"Data Source={paths.DatabasePath(projectId)};Pooling=False"))
        {
            await connection.OpenAsync();
            var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TABLE layer_edit_state;
                UPDATE meta SET value = '4' WHERE key = 'survey_schema_version';
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var restored = await new SqliteSurveyProjectRepository(paths).GetAsync(projectId);
        Assert.True(Assert.Single(restored!.Layers).UsesCleanedDisplay);
        Assert.NotNull(Assert.Single(restored.Observations).DisplayAsset);
    }

    private static SurveyObservationRequest CreateObservationRequest(
        SurveyProjectSnapshot project,
        SurveyCaptureContext capture) => new(
        Guid.NewGuid(),
        project.Project.ProjectId,
        project.Project.Revision,
        new SurveyEncodedFrame(
            OnePixelPng,
            ".png",
            "image/png",
            1,
            1,
            capture));

    private static SurveyCaptureContext CreateCapture(
        Guid matchId,
        long operationEpoch,
        long toggleVersion) => new(
        matchId,
        operationEpoch,
        toggleVersion,
        DateTimeOffset.UtcNow,
        1920,
        1080,
        120,
        new SurveyPixelRect(100, 100, 800, 700),
        "1f",
        new string('d', 64),
        "1.0.0");

    private static byte[] OnePixelPng { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
