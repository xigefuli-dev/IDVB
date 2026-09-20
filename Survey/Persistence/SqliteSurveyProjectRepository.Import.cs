using IDVBuff.Survey.Domain;
using Microsoft.Data.Sqlite;
using static IDVBuff.Survey.Persistence.Sqlite.SqliteSurveyCommands;

namespace IDVBuff.Survey.Persistence.Sqlite;

public sealed partial class SqliteSurveyProjectRepository
{
    public async Task<SurveyProjectSnapshot> ImportSnapshotAsync(
        SurveyProjectSnapshot snapshot,
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        var projectId = snapshot.Project.ProjectId;
        if (File.Exists(_paths.DatabasePath(projectId)))
            throw new InvalidOperationException("A survey project with the same identity already exists.");
        ValidateImportedSnapshot(snapshot);
        Directory.CreateDirectory(_paths.ProjectDirectory(projectId));
        Directory.CreateDirectory(_paths.AssetsDirectory(projectId));
        Directory.CreateDirectory(_paths.TemporaryDirectory(projectId));
        await using var connection = await OpenAsync(projectId, create: true, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        await InsertImportedProjectAsync(connection, sqliteTransaction, snapshot.Project, cancellationToken)
            .ConfigureAwait(false);
        foreach (var floor in snapshot.Floors.OrderBy(item => item.Order))
            await InsertImportedFloorAsync(connection, sqliteTransaction, projectId, floor, cancellationToken)
                .ConfigureAwait(false);
        foreach (var observation in snapshot.Observations.OrderBy(item => item.Capture.CapturedAt))
        {
            await InsertObservationAsync(connection, sqliteTransaction, observation, cancellationToken)
                .ConfigureAwait(false);
            await UpsertObservationAssetAsync(
                connection,
                sqliteTransaction,
                projectId,
                observation.ObservationId,
                "structure",
                observation.StructureAsset,
                cancellationToken).ConfigureAwait(false);
            await UpsertObservationAssetAsync(
                connection,
                sqliteTransaction,
                projectId,
                observation.ObservationId,
                "features",
                observation.FeatureAsset,
                cancellationToken).ConfigureAwait(false);
            await UpsertObservationAssetAsync(
                connection,
                sqliteTransaction,
                projectId,
                observation.ObservationId,
                "display",
                observation.DisplayAsset,
                cancellationToken).ConfigureAwait(false);
            await UpsertObservationAssetAsync(
                connection,
                sqliteTransaction,
                projectId,
                observation.ObservationId,
                "visible-mask",
                observation.VisibleMaskAsset,
                cancellationToken).ConfigureAwait(false);
        }
        foreach (var layer in snapshot.Layers.OrderBy(item => item.ZOrder))
            await InsertLayerAsync(connection, sqliteTransaction, layer, cancellationToken)
                .ConfigureAwait(false);
        foreach (var constraint in snapshot.Constraints)
            await InsertConstraintAsync(connection, sqliteTransaction, constraint, cancellationToken)
                .ConfigureAwait(false);
        await InsertRevisionAsync(
            connection,
            sqliteTransaction,
            projectId,
            snapshot.Project.Revision,
            commandId,
            "ImportSurveyProject",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertImportedProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SurveyProject project,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO projects(
                project_id, schema_version, name, map_class, state, created_utc,
                updated_utc, revision, config_digest, algorithm_version,
                active_floor_key, published_revision)
            VALUES(
                $id, $schema, $name, $class, $state, $created, $updated,
                $revision, $config, $algorithm, $floor, $published);
            """;
        command.Parameters.AddWithValue("$id", project.ProjectId.ToString("N"));
        command.Parameters.AddWithValue("$schema", SurveyDatabaseSchema.CurrentVersion);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$class", project.MapClass);
        command.Parameters.AddWithValue("$state", (int)project.State);
        command.Parameters.AddWithValue("$created", FormatDate(project.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatDate(project.UpdatedAt));
        command.Parameters.AddWithValue("$revision", project.Revision);
        command.Parameters.AddWithValue("$config", project.ConfigDigest);
        command.Parameters.AddWithValue("$algorithm", project.AlgorithmVersion);
        command.Parameters.AddWithValue("$floor", project.ActiveFloorKey);
        command.Parameters.AddWithValue("$published", (object?)project.PublishedRevision ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertImportedFloorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        SurveyFloor floor,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO floors(
                floor_id, project_id, floor_key, display_name, sort_order,
                root_layer_id, world_x, world_y, world_width, world_height)
            VALUES($id, $project, $key, $name, $order, $root, $x, $y, $width, $height);
            """;
        command.Parameters.AddWithValue("$id", floor.FloorId.ToString("N"));
        command.Parameters.AddWithValue("$project", projectId.ToString("N"));
        command.Parameters.AddWithValue("$key", floor.FloorKey);
        command.Parameters.AddWithValue("$name", floor.DisplayName);
        command.Parameters.AddWithValue("$order", floor.Order);
        command.Parameters.AddWithValue("$root", (object?)floor.RootLayerId?.ToString("N") ?? DBNull.Value);
        command.Parameters.AddWithValue("$x", (object?)floor.WorldBounds?.X ?? DBNull.Value);
        command.Parameters.AddWithValue("$y", (object?)floor.WorldBounds?.Y ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", (object?)floor.WorldBounds?.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)floor.WorldBounds?.Height ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateImportedSnapshot(SurveyProjectSnapshot snapshot)
    {
        if (snapshot.Project.ProjectId == Guid.Empty || snapshot.Project.Revision < 1)
            throw new InvalidDataException("Imported survey project identity or revision is invalid.");
        var floorIds = snapshot.Floors.Select(item => item.FloorId).ToHashSet();
        var observationIds = snapshot.Observations.Select(item => item.ObservationId).ToHashSet();
        var layerIds = snapshot.Layers.Select(item => item.LayerId).ToHashSet();
        if (floorIds.Count != snapshot.Floors.Count
            || observationIds.Count != snapshot.Observations.Count
            || layerIds.Count != snapshot.Layers.Count
            || snapshot.Observations.Any(item =>
                item.ProjectId != snapshot.Project.ProjectId
                || !floorIds.Contains(item.FloorId)
                || !item.SourceAsset.IsValid)
            || snapshot.Layers.Any(item =>
                item.ProjectId != snapshot.Project.ProjectId
                || !floorIds.Contains(item.FloorId)
                || !observationIds.Contains(item.ObservationId)
                || !item.AutomaticTransform.IsValid)
            || snapshot.Constraints.Any(item =>
                item.ProjectId != snapshot.Project.ProjectId
                || !floorIds.Contains(item.FloorId)
                || !layerIds.Contains(item.SourceLayerId)
                || !layerIds.Contains(item.TargetLayerId)))
            throw new InvalidDataException("Imported survey project relationships are invalid.");
    }
}
