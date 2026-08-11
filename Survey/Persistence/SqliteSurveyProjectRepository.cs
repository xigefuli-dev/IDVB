using System.Globalization;
using System.Collections.Concurrent;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using Microsoft.Data.Sqlite;
using static IDVBuff.Survey.Persistence.Sqlite.SqliteSurveyCommands;

namespace IDVBuff.Survey.Persistence.Sqlite;

public sealed partial class SqliteSurveyProjectRepository : ISurveyProjectRepository
{
    private readonly SurveyStoragePaths _paths;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _projectOpenGates = new();

    public SqliteSurveyProjectRepository(SurveyStoragePaths paths)
    {
        _paths = paths;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_paths.RootDirectory);
        return Task.CompletedTask;
    }

    public async Task<SurveyProjectSnapshot> CreateAsync(
        SurveyStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateStartRequest(request);
        var projectId = Guid.NewGuid();
        Directory.CreateDirectory(_paths.ProjectDirectory(projectId));
        Directory.CreateDirectory(_paths.AssetsDirectory(projectId));
        Directory.CreateDirectory(_paths.TemporaryDirectory(projectId));
        await using var connection = await OpenAsync(projectId, create: true, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var projectCommand = connection.CreateCommand();
        projectCommand.Transaction = (SqliteTransaction)transaction;
        projectCommand.CommandText = """
            INSERT INTO projects(
                project_id, schema_version, name, map_class, state, created_utc,
                updated_utc, revision, config_digest, algorithm_version,
                active_floor_key, published_revision)
            VALUES(
                $project_id, $schema_version, $name, $map_class, $state, $created_utc,
                $updated_utc, 1, $config_digest, $algorithm_version, $floor_key, NULL);
            """;
        projectCommand.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        projectCommand.Parameters.AddWithValue("$schema_version", SurveyDatabaseSchema.CurrentVersion);
        projectCommand.Parameters.AddWithValue("$name", CreateProjectName(request.Name, now));
        projectCommand.Parameters.AddWithValue("$map_class", request.MapClass.Trim());
        projectCommand.Parameters.AddWithValue("$state", (int)SurveyProjectState.Draft);
        projectCommand.Parameters.AddWithValue("$created_utc", FormatDate(now));
        projectCommand.Parameters.AddWithValue("$updated_utc", FormatDate(now));
        projectCommand.Parameters.AddWithValue("$config_digest", request.ConfigDigest);
        projectCommand.Parameters.AddWithValue("$algorithm_version", request.AlgorithmVersion);
        projectCommand.Parameters.AddWithValue("$floor_key", NormalizeFloorKey(request.FloorKey));
        await projectCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var floorCommand = connection.CreateCommand();
        floorCommand.Transaction = (SqliteTransaction)transaction;
        floorCommand.CommandText = """
            INSERT INTO floors(
                floor_id, project_id, floor_key, display_name, sort_order, root_layer_id)
            VALUES($floor_id, $project_id, $floor_key, $display_name, 0, NULL);
            """;
        floorCommand.Parameters.AddWithValue("$floor_id", Guid.NewGuid().ToString("N"));
        floorCommand.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        floorCommand.Parameters.AddWithValue("$floor_key", NormalizeFloorKey(request.FloorKey));
        floorCommand.Parameters.AddWithValue("$display_name", request.FloorKey.Trim());
        await floorCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertRevisionAsync(
            connection,
            (SqliteTransaction)transaction,
            projectId,
            1,
            request.CommandId,
            "CreateProject",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SurveyProjectSnapshot?> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.DatabasePath(projectId)))
            return null;
        await using var connection = await OpenAsync(projectId, create: false, cancellationToken).ConfigureAwait(false);
        return await SqliteSurveyProjectReader.ReadSnapshotAsync(
            connection,
            projectId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SurveyProjectSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<SurveyProjectSummary>();
        if (!Directory.Exists(_paths.RootDirectory))
            return result;
        foreach (var directory in Directory.EnumerateDirectories(_paths.RootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var projectId))
                continue;
            try
            {
                var snapshot = await GetAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (snapshot is null)
                    continue;
                result.Add(ToSummary(snapshot));
            }
            catch (Exception exception) when (IsCatalogReadFailure(exception))
            {
                // One unreadable legacy/corrupt project must not take down the catalog.
            }
        }
        return result.OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    public async Task<SurveyObservationCommitResult> CommitObservationAsync(
        SurveyObservation observation,
        SurveyMapLayer layer,
        long expectedRevision,
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        ValidateObservation(observation, layer);
        await using var connection = await OpenAsync(
            observation.ProjectId,
            create: false,
            cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var currentRevision = await ReadRevisionAsync(
            connection,
            (SqliteTransaction)transaction,
            observation.ProjectId,
            cancellationToken).ConfigureAwait(false);
        var existingLayerId = await FindLayerByIdempotencyKeyAsync(
            connection,
            (SqliteTransaction)transaction,
            observation.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existingLayerId is { } duplicateLayerId)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadRequiredAsync(connection, observation.ProjectId, cancellationToken).ConfigureAwait(false);
            var duplicateLayer = existing.Layers.Single(item => item.LayerId == duplicateLayerId);
            var duplicateObservation = existing.Observations.Single(
                item => item.ObservationId == duplicateLayer.ObservationId);
            return new SurveyObservationCommitResult(existing, duplicateObservation, duplicateLayer, true);
        }
        EnsureRevision(observation.ProjectId, expectedRevision, currentRevision);

        await EnsureFloorAsync(connection, (SqliteTransaction)transaction, observation, cancellationToken)
            .ConfigureAwait(false);
        await InsertObservationAsync(connection, (SqliteTransaction)transaction, observation, cancellationToken)
            .ConfigureAwait(false);
        await InsertLayerAsync(connection, (SqliteTransaction)transaction, layer, cancellationToken)
            .ConfigureAwait(false);
        var nextRevision = currentRevision + 1;
        await UpdateProjectAfterObservationAsync(
            connection,
            (SqliteTransaction)transaction,
            observation,
            layer,
            nextRevision,
            cancellationToken).ConfigureAwait(false);
        await InsertRevisionAsync(
            connection,
            (SqliteTransaction)transaction,
            observation.ProjectId,
            nextRevision,
            commandId,
            "AddObservation",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadRequiredAsync(connection, observation.ProjectId, cancellationToken).ConfigureAwait(false);
        return new SurveyObservationCommitResult(snapshot, observation, layer, false);
    }

    public async Task<SurveyProjectSnapshot> EditLayerAsync(
        SurveyLayerEditRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(request.ProjectId, false, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var currentRevision = await ReadRevisionAsync(
            connection,
            (SqliteTransaction)transaction,
            request.ProjectId,
            cancellationToken).ConfigureAwait(false);
        EnsureRevision(request.ProjectId, request.ExpectedRevision, currentRevision);
        var current = await ReadLayerAsync(
            connection,
            (SqliteTransaction)transaction,
            request,
            cancellationToken).ConfigureAwait(false);
        var updated = ApplyEdit(current, request, currentRevision + 1);
        await UpdateLayerAsync(connection, (SqliteTransaction)transaction, updated, cancellationToken)
            .ConfigureAwait(false);
        if (current.IsDeleted != updated.IsDeleted)
        {
            var root = connection.CreateCommand();
            root.Transaction = (SqliteTransaction)transaction;
            root.CommandText = updated.IsDeleted
                ? """
                  UPDATE floors SET root_layer_id = (
                      SELECT layer_id FROM layers
                      WHERE project_id = $project_id AND floor_id = $floor_id
                          AND is_deleted = 0 AND layer_id <> $layer_id
                      ORDER BY z_order ASC, layer_id ASC LIMIT 1)
                  WHERE project_id = $project_id AND floor_id = $floor_id
                      AND root_layer_id = $layer_id;
                  """
                : """
                  UPDATE floors SET root_layer_id = $layer_id
                  WHERE project_id = $project_id AND floor_id = $floor_id
                      AND root_layer_id IS NULL;
                  """;
            root.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
            root.Parameters.AddWithValue("$floor_id", current.FloorId.ToString("N"));
            root.Parameters.AddWithValue("$layer_id", current.LayerId.ToString("N"));
            await root.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (request.SetAsFloorRoot)
        {
            if (updated.IsDeleted)
                throw new InvalidOperationException("A deleted layer cannot be the floor root.");
            var setRoot = connection.CreateCommand();
            setRoot.Transaction = (SqliteTransaction)transaction;
            setRoot.CommandText = """
                UPDATE floors SET root_layer_id = $layer_id
                WHERE project_id = $project_id AND floor_id = $floor_id;
                """;
            setRoot.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
            setRoot.Parameters.AddWithValue("$floor_id", updated.FloorId.ToString("N"));
            setRoot.Parameters.AddWithValue("$layer_id", updated.LayerId.ToString("N"));
            await setRoot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var nextRevision = currentRevision + 1;
        await TouchProjectAsync(
            connection,
            (SqliteTransaction)transaction,
            request.ProjectId,
            nextRevision,
            keepPublished: false,
            cancellationToken).ConfigureAwait(false);
        await InsertRevisionAsync(
            connection,
            (SqliteTransaction)transaction,
            request.ProjectId,
            nextRevision,
            request.CommandId,
            "EditLayer",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(connection, request.ProjectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SurveyProjectSnapshot> SetProjectStateAsync(
        SurveyProjectStateRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(request.ProjectId, false, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var currentRevision = await ReadRevisionAsync(
            connection,
            (SqliteTransaction)transaction,
            request.ProjectId,
            cancellationToken).ConfigureAwait(false);
        EnsureRevision(request.ProjectId, request.ExpectedRevision, currentRevision);
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE projects SET state = $state, revision = $revision, updated_utc = $updated_utc,
                published_revision = CASE
                    WHEN $state = $published THEN $published_revision
                    ELSE published_revision
                END
            WHERE project_id = $project_id;
            """;
        command.Parameters.AddWithValue("$state", (int)request.State);
        command.Parameters.AddWithValue("$published", (int)SurveyProjectState.Published);
        command.Parameters.AddWithValue("$published_revision", currentRevision);
        command.Parameters.AddWithValue("$revision", currentRevision + 1);
        command.Parameters.AddWithValue("$updated_utc", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertRevisionAsync(
            connection,
            (SqliteTransaction)transaction,
            request.ProjectId,
            currentRevision + 1,
            request.CommandId,
            "SetProjectState",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(connection, request.ProjectId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(
        Guid projectId,
        bool create,
        CancellationToken cancellationToken)
    {
        var gate = _projectOpenGates.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenCoreAsync(projectId, create, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenCoreAsync(
        Guid projectId,
        bool create,
        CancellationToken cancellationToken)
    {
        var databasePath = _paths.DatabasePath(projectId);
        if (!create && !File.Exists(databasePath))
            throw new SurveyProjectNotFoundException(projectId);
        if (!create && File.Exists(databasePath))
        {
            var storedVersion = await SurveyDatabaseSchema.ReadStoredVersionAsync(
                databasePath,
                cancellationToken).ConfigureAwait(false);
            if (storedVersion > SurveyDatabaseSchema.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Survey schema {storedVersion} is newer than this application supports.");
            }
            if (storedVersion is > 0 and < SurveyDatabaseSchema.CurrentVersion)
            {
                var backupPath = Path.Combine(
                    _paths.ProjectDirectory(projectId),
                    $"project.schema-v{storedVersion}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak");
                await SurveyDatabaseSchema.CreateBackupAsync(
                    databasePath,
                    backupPath,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            // One database exists per survey project. Disabling provider-level
            // pooling prevents archived/deleted project files from remaining
            // locked on Windows while WAL still provides efficient reuse.
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SurveyDatabaseSchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsCatalogReadFailure(Exception exception) => exception is
        SqliteException
        or InvalidDataException
        or InvalidOperationException
        or IOException
        or UnauthorizedAccessException
        or FormatException;

    private static async Task<SurveyProjectSnapshot> ReadRequiredAsync(
        SqliteConnection connection,
        Guid projectId,
        CancellationToken cancellationToken) =>
        await SqliteSurveyProjectReader.ReadSnapshotAsync(connection, projectId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new SurveyProjectNotFoundException(projectId);

    private static SurveyProjectSummary ToSummary(SurveyProjectSnapshot snapshot) => new(
        snapshot.Project.ProjectId,
        snapshot.Project.Name,
        snapshot.Project.MapClass,
        snapshot.Project.State,
        snapshot.Project.UpdatedAt,
        snapshot.Project.Revision,
        snapshot.Observations.Count,
        snapshot.Layers.Count(item => !item.IsDeleted),
        snapshot.Observations.Count(item => item.State == SurveyObservationState.Unregistered));

    private static string CreateProjectName(string? requested, DateTimeOffset now) =>
        string.IsNullOrWhiteSpace(requested)
            ? $"未命名测绘 {now.ToLocalTime():yyyy-MM-dd HH:mm}"
            : requested.Trim();

    private static string NormalizeFloorKey(string value) => value.Trim().ToLowerInvariant();
    private static string FormatDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateStartRequest(SurveyStartRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MapClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FloorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConfigDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AlgorithmVersion);
    }

    private static void ValidateObservation(SurveyObservation observation, SurveyMapLayer layer)
    {
        if (observation.ProjectId != layer.ProjectId
            || observation.FloorId != layer.FloorId
            || observation.ObservationId != layer.ObservationId)
            throw new ArgumentException("Survey observation and layer identity do not match.");
        if (!observation.SourceAsset.IsValid || !layer.AutomaticTransform.IsValid)
            throw new ArgumentException("Survey observation or layer is invalid.");
    }

    private static void EnsureRevision(Guid projectId, long expected, long actual)
    {
        if (expected != actual)
            throw new SurveyRevisionConflictException(projectId, expected, actual);
    }
}
