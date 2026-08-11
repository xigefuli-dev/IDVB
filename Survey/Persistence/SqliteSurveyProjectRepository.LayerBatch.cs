using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using Microsoft.Data.Sqlite;
using static IDVBuff.Survey.Persistence.Sqlite.SqliteSurveyCommands;

namespace IDVBuff.Survey.Persistence.Sqlite;

public sealed partial class SqliteSurveyProjectRepository
{
    public async Task<SurveyProjectSnapshot> ApplyLayerBatchAsync(
        SurveyLayerBatchEditRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Mutations.Count == 0
            || request.Mutations.Select(item => item.LayerId).Distinct().Count() != request.Mutations.Count)
            throw new ArgumentException("Layer mutations must be non-empty and unique.", nameof(request));
        if (request.Mutations.Any(item =>
            item.ReplaceManualTransform && item.ManualTransformOverride is { IsValid: false }))
            throw new ArgumentException("Layer mutation contains an invalid transform.", nameof(request));
        if (request.Mutations.Any(item => item.ReplaceObservationStatus
            && (item.ObservationState is null || item.ObservationErrorCode is null)))
            throw new ArgumentException("Observation status replacement is incomplete.", nameof(request));

        await using var connection = await OpenAsync(request.ProjectId, false, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqlite = (SqliteTransaction)transaction;
        var revision = await ReadRevisionAsync(connection, sqlite, request.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        EnsureRevision(request.ProjectId, request.ExpectedRevision, revision);
        var nextRevision = revision + 1;

        foreach (var mutation in request.Mutations)
        {
            if (mutation.ReplaceManualTransform)
            {
                var transform = connection.CreateCommand();
                transform.Transaction = sqlite;
                transform.CommandText = """
                    UPDATE layers SET manual_tx = $tx, manual_ty = $ty,
                        manual_rotation = $rotation, manual_sx = $sx,
                        manual_sy = $sy, manual_revision = $revision
                    WHERE project_id = $project AND layer_id = $layer;
                    """;
                transform.Parameters.AddWithValue("$tx", (object?)mutation.ManualTransformOverride?.TranslationX ?? DBNull.Value);
                transform.Parameters.AddWithValue("$ty", (object?)mutation.ManualTransformOverride?.TranslationY ?? DBNull.Value);
                transform.Parameters.AddWithValue("$rotation", (object?)mutation.ManualTransformOverride?.RotationDegrees ?? DBNull.Value);
                transform.Parameters.AddWithValue("$sx", (object?)mutation.ManualTransformOverride?.ScaleX ?? DBNull.Value);
                transform.Parameters.AddWithValue("$sy", (object?)mutation.ManualTransformOverride?.ScaleY ?? DBNull.Value);
                transform.Parameters.AddWithValue("$revision", nextRevision);
                transform.Parameters.AddWithValue("$project", request.ProjectId.ToString("N"));
                transform.Parameters.AddWithValue("$layer", mutation.LayerId.ToString("N"));
                if (await transform.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Survey layer was not found.");
            }

            var state = connection.CreateCommand();
            state.Transaction = sqlite;
            state.CommandText = """
                UPDATE layer_edit_state SET
                    uses_cleaned_display = COALESCE($cleaned, uses_cleaned_display),
                    hidden_sha256 = CASE WHEN $replace_hidden THEN $sha ELSE hidden_sha256 END,
                    hidden_path = CASE WHEN $replace_hidden THEN $path ELSE hidden_path END,
                    hidden_media_type = CASE WHEN $replace_hidden THEN $media ELSE hidden_media_type END,
                    hidden_byte_length = CASE WHEN $replace_hidden THEN $bytes ELSE hidden_byte_length END,
                    hidden_pixel_width = CASE WHEN $replace_hidden THEN $width ELSE hidden_pixel_width END,
                    hidden_pixel_height = CASE WHEN $replace_hidden THEN $height ELSE hidden_pixel_height END
                WHERE project_id = $project AND layer_id = $layer;
                """;
            state.Parameters.AddWithValue("$cleaned", (object?)mutation.UsesCleanedDisplay ?? DBNull.Value);
            state.Parameters.AddWithValue("$replace_hidden", mutation.ReplaceHiddenMask);
            state.Parameters.AddWithValue("$sha", (object?)mutation.HiddenMaskAsset?.Sha256 ?? DBNull.Value);
            state.Parameters.AddWithValue("$path", (object?)mutation.HiddenMaskAsset?.RelativePath ?? DBNull.Value);
            state.Parameters.AddWithValue("$media", (object?)mutation.HiddenMaskAsset?.MediaType ?? DBNull.Value);
            state.Parameters.AddWithValue("$bytes", (object?)mutation.HiddenMaskAsset?.ByteLength ?? DBNull.Value);
            state.Parameters.AddWithValue("$width", (object?)mutation.HiddenMaskAsset?.PixelWidth ?? DBNull.Value);
            state.Parameters.AddWithValue("$height", (object?)mutation.HiddenMaskAsset?.PixelHeight ?? DBNull.Value);
            state.Parameters.AddWithValue("$project", request.ProjectId.ToString("N"));
            state.Parameters.AddWithValue("$layer", mutation.LayerId.ToString("N"));
            if (await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Survey layer edit state was not found.");

            if (mutation.ReplaceObservationStatus)
            {
                var observation = connection.CreateCommand();
                observation.Transaction = sqlite;
                observation.CommandText = """
                    UPDATE observations SET state = $state, error_code = $error,
                        error_message = $message
                    WHERE project_id = $project AND observation_id = (
                        SELECT observation_id FROM layers
                        WHERE project_id = $project AND layer_id = $layer);
                    """;
                observation.Parameters.AddWithValue("$state", (int)mutation.ObservationState!.Value);
                observation.Parameters.AddWithValue("$error", (int)mutation.ObservationErrorCode!.Value);
                observation.Parameters.AddWithValue(
                    "$message", (object?)mutation.ObservationErrorMessage ?? DBNull.Value);
                observation.Parameters.AddWithValue("$project", request.ProjectId.ToString("N"));
                observation.Parameters.AddWithValue("$layer", mutation.LayerId.ToString("N"));
                if (await observation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Survey layer observation was not found.");
            }
        }

        await TouchProjectAsync(
            connection, sqlite, request.ProjectId, nextRevision,
            keepPublished: false, cancellationToken).ConfigureAwait(false);
        await InsertRevisionAsync(
            connection, sqlite, request.ProjectId, nextRevision,
            request.CommandId, "EditLayerBatch", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(connection, request.ProjectId, cancellationToken).ConfigureAwait(false);
    }
}
