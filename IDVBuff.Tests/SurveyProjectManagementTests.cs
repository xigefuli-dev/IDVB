using IDVBuff.Survey.Application;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using IDVBuff.Survey.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace IDVBuff.Tests;

public sealed class SurveyProjectManagementTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "idvb-survey-management-tests",
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
    public async Task ConcurrentLegacyProjectReadsShareOneMigrationAndBackup()
    {
        var paths = new SurveyStoragePaths(_root);
        Guid projectId;
        await using (var coordinator = CreateCoordinator(paths))
        {
            var started = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), Guid.NewGuid(), 1, "S1", "1f", "legacy concurrent open",
                new string('5', 64), "test"))).Value!;
            projectId = started.Project.ProjectId;
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

        var repository = new SqliteSurveyProjectRepository(paths);
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => repository.GetAsync(projectId)));

        Assert.All(snapshots, snapshot => Assert.NotNull(snapshot));
        Assert.Single(Directory.EnumerateFiles(
            paths.ProjectDirectory(projectId),
            "project.schema-v4.*.bak"));
    }

    [Fact]
    public async Task ProjectCanBeRenamed()
    {
        var paths = new SurveyStoragePaths(_root);
        await using var coordinator = CreateCoordinator(paths);
        var matchId = Guid.NewGuid();
        var started = (await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(), matchId, 20, "S1", "1f", "before rename",
            new string('6', 64), "test"))).Value!;

        var renamed = await coordinator.RenameProjectAsync(new SurveyProjectRenameRequest(
            Guid.NewGuid(),
            started.Project.ProjectId,
            started.Project.Revision,
            "after rename"));
        Assert.True(renamed.Succeeded);
        Assert.Equal("after rename", renamed.Value!.Project.Name);

        var reloaded = await coordinator.GetProjectAsync(started.Project.ProjectId);
        Assert.NotNull(reloaded);
        Assert.Equal("after rename", reloaded.Project.Name);
    }

    private static SurveyCoordinator CreateCoordinator(SurveyStoragePaths paths) => new(
        new SqliteSurveyProjectRepository(paths),
        new ContentAddressedSurveyAssetStore(paths));
}
