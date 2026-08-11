using IDVBuff.Survey.Contracts;
using Microsoft.Data.Sqlite;
using static IDVBuff.Survey.Persistence.Sqlite.SqliteSurveyCommands;

namespace IDVBuff.Survey.Persistence.Sqlite;

public sealed partial class SqliteSurveyProjectRepository
{
    public async Task<IDVBuff.Survey.Domain.SurveyProjectSnapshot> CommitProcessingAsync(
        SurveyProcessingCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.AutomaticTransform.IsValid
            || !double.IsFinite(request.Quality)
            || request.Quality < 0d
            || request.Quality > 1d)
            throw new ArgumentException("Survey processing result is invalid.", nameof(request));
        await using var connection = await OpenAsync(request.ProjectId, false, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var revision = await ReadRevisionAsync(
            connection,
            sqliteTransaction,
            request.ProjectId,
            cancellationToken).ConfigureAwait(false);
        EnsureRevision(request.ProjectId, request.ExpectedRevision, revision);
        var nextRevision = revision + 1;

        var observation = connection.CreateCommand();
        observation.Transaction = sqliteTransaction;
        observation.CommandText = """
            UPDATE observations
            SET state = $state, quality = $quality, error_code = $error_code,
                error_message = $error_message
            WHERE project_id = $project_id AND observation_id = $observation_id;
            """;
        observation.Parameters.AddWithValue("$state", (int)request.ObservationState);
        observation.Parameters.AddWithValue("$quality", request.Quality);
        observation.Parameters.AddWithValue("$error_code", (int)request.ErrorCode);
        observation.Parameters.AddWithValue("$error_message", (object?)request.ErrorMessage ?? DBNull.Value);
        observation.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
        observation.Parameters.AddWithValue("$observation_id", request.ObservationId.ToString("N"));
        if (await observation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Survey observation was not found.");

        var layer = connection.CreateCommand();
        layer.Transaction = sqliteTransaction;
        layer.CommandText = """
            UPDATE layers
            SET auto_tx = $tx, auto_ty = $ty, auto_rotation = $rotation,
                auto_sx = $sx, auto_sy = $sy, auto_revision = $revision
            WHERE project_id = $project_id AND layer_id = $layer_id;
            """;
        layer.Parameters.AddWithValue("$tx", request.AutomaticTransform.TranslationX);
        layer.Parameters.AddWithValue("$ty", request.AutomaticTransform.TranslationY);
        layer.Parameters.AddWithValue("$rotation", request.AutomaticTransform.RotationDegrees);
        layer.Parameters.AddWithValue("$sx", request.AutomaticTransform.ScaleX);
        layer.Parameters.AddWithValue("$sy", request.AutomaticTransform.ScaleY);
        layer.Parameters.AddWithValue("$revision", nextRevision);
        layer.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
        layer.Parameters.AddWithValue("$layer_id", request.LayerId.ToString("N"));
        if (await layer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Survey layer was not found.");

        if (request.Constraint is { } constraint)
            await InsertConstraintAsync(connection, sqliteTransaction, constraint, cancellationToken)
                .ConfigureAwait(false);
        await UpsertObservationAssetAsync(
            connection,
            sqliteTransaction,
            request.ProjectId,
            request.ObservationId,
            "structure",
            request.StructureAsset,
            cancellationToken).ConfigureAwait(false);
        await UpsertObservationAssetAsync(
            connection,
            sqliteTransaction,
            request.ProjectId,
            request.ObservationId,
            "features",
            request.FeatureAsset,
            cancellationToken).ConfigureAwait(false);
        await UpsertObservationAssetAsync(
            connection,
            sqliteTransaction,
            request.ProjectId,
            request.ObservationId,
            "display",
            request.DisplayAsset,
            cancellationToken).ConfigureAwait(false);
        await UpsertObservationAssetAsync(
            connection,
            sqliteTransaction,
            request.ProjectId,
            request.ObservationId,
            "visible-mask",
            request.VisibleMaskAsset,
            cancellationToken).ConfigureAwait(false);
        if (request.UsesCleanedDisplay is { } usesCleanedDisplay)
        {
            var displayState = connection.CreateCommand();
            displayState.Transaction = sqliteTransaction;
            displayState.CommandText = """
                UPDATE layer_edit_state SET uses_cleaned_display = $cleaned
                WHERE project_id = $project AND layer_id = $layer;
                """;
            displayState.Parameters.AddWithValue("$cleaned", usesCleanedDisplay);
            displayState.Parameters.AddWithValue("$project", request.ProjectId.ToString("N"));
            displayState.Parameters.AddWithValue("$layer", request.LayerId.ToString("N"));
            if (await displayState.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Survey layer edit state was not found.");
        }
        await TouchProjectAsync(
            connection,
            sqliteTransaction,
            request.ProjectId,
            nextRevision,
            keepPublished: false,
            cancellationToken).ConfigureAwait(false);
        await InsertRevisionAsync(
            connection,
            sqliteTransaction,
            request.ProjectId,
            nextRevision,
            request.CommandId,
            "CommitProcessing",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(connection, request.ProjectId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertConstraintAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IDVBuff.Survey.Domain.SurveyConstraint constraint,
        CancellationToken cancellationToken)
    {
        var transform = constraint.RelativeTransform;
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO constraints(
                constraint_id, project_id, floor_id, source_layer_id, target_layer_id,
                tx, ty, rotation, sx, sy, confidence, residual, inlier_count,
                algorithm_id, algorithm_version, is_accepted, rejection_reason)
            VALUES(
                $id, $project, $floor, $source, $target, $tx, $ty, $rotation,
                $sx, $sy, $confidence, $residual, $inliers, $algorithm,
                $version, $accepted, $reason);
            """;
        command.Parameters.AddWithValue("$id", constraint.ConstraintId.ToString("N"));
        command.Parameters.AddWithValue("$project", constraint.ProjectId.ToString("N"));
        command.Parameters.AddWithValue("$floor", constraint.FloorId.ToString("N"));
        command.Parameters.AddWithValue("$source", constraint.SourceLayerId.ToString("N"));
        command.Parameters.AddWithValue("$target", constraint.TargetLayerId.ToString("N"));
        command.Parameters.AddWithValue("$tx", transform.TranslationX);
        command.Parameters.AddWithValue("$ty", transform.TranslationY);
        command.Parameters.AddWithValue("$rotation", transform.RotationDegrees);
        command.Parameters.AddWithValue("$sx", transform.ScaleX);
        command.Parameters.AddWithValue("$sy", transform.ScaleY);
        command.Parameters.AddWithValue("$confidence", constraint.Confidence);
        command.Parameters.AddWithValue("$residual", constraint.Residual);
        command.Parameters.AddWithValue("$inliers", constraint.InlierCount);
        command.Parameters.AddWithValue("$algorithm", constraint.AlgorithmId);
        command.Parameters.AddWithValue("$version", constraint.AlgorithmVersion);
        command.Parameters.AddWithValue("$accepted", constraint.IsAccepted);
        command.Parameters.AddWithValue("$reason", (object?)constraint.RejectionReason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertObservationAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        Guid observationId,
        string kind,
        IDVBuff.Survey.Domain.SurveyAssetReference? asset,
        CancellationToken cancellationToken)
    {
        if (asset is null)
            return;
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO observation_assets(
                observation_id, project_id, asset_kind, sha256, relative_path,
                media_type, byte_length, pixel_width, pixel_height)
            VALUES($observation, $project, $kind, $sha, $path, $media, $bytes, $width, $height)
            ON CONFLICT(observation_id, asset_kind) DO UPDATE SET
                sha256 = excluded.sha256,
                relative_path = excluded.relative_path,
                media_type = excluded.media_type,
                byte_length = excluded.byte_length,
                pixel_width = excluded.pixel_width,
                pixel_height = excluded.pixel_height;
            """;
        command.Parameters.AddWithValue("$observation", observationId.ToString("N"));
        command.Parameters.AddWithValue("$project", projectId.ToString("N"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$sha", asset.Sha256);
        command.Parameters.AddWithValue("$path", asset.RelativePath);
        command.Parameters.AddWithValue("$media", asset.MediaType);
        command.Parameters.AddWithValue("$bytes", asset.ByteLength);
        command.Parameters.AddWithValue("$width", asset.PixelWidth);
        command.Parameters.AddWithValue("$height", asset.PixelHeight);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
