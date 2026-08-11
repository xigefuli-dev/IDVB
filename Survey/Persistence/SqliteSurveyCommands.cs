using System.Globalization;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using Microsoft.Data.Sqlite;

namespace IDVBuff.Survey.Persistence.Sqlite;

internal static class SqliteSurveyCommands
{
    public static async Task<long> ReadRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM projects WHERE project_id = $project_id;";
        command.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long revision
            ? revision
            : throw new SurveyProjectNotFoundException(projectId);
    }

    public static async Task InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        long revision,
        Guid commandId,
        string commandType,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO revisions(
                revision, project_id, command_id, command_type, created_utc, payload_json)
            VALUES($revision, $project_id, $command_id, $command_type, $created_utc, NULL);
            """;
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        command.Parameters.AddWithValue("$command_id", commandId.ToString("N"));
        command.Parameters.AddWithValue("$command_type", commandType);
        command.Parameters.AddWithValue("$created_utc", FormatDate(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Guid?> FindLayerByIdempotencyKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT l.layer_id
            FROM layers l
            JOIN observations o ON o.observation_id = l.observation_id
            WHERE o.idempotency_key = $idempotency_key;
            """;
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string value ? Guid.ParseExact(value, "N") : null;
    }

    public static async Task EnsureFloorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SurveyObservation observation,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO floors(
                floor_id, project_id, floor_key, display_name, sort_order, root_layer_id)
            VALUES(
                $floor_id, $project_id, $floor_key, $display_name,
                (SELECT COUNT(*) FROM floors WHERE project_id = $project_id), NULL)
            ON CONFLICT(project_id, floor_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$floor_id", observation.FloorId.ToString("N"));
        command.Parameters.AddWithValue("$project_id", observation.ProjectId.ToString("N"));
        command.Parameters.AddWithValue("$floor_key", observation.Capture.FloorKey);
        command.Parameters.AddWithValue("$display_name", observation.Capture.FloorKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task InsertObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SurveyObservation observation,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO observations(
                observation_id, project_id, floor_id, idempotency_key, match_id,
                operation_epoch, map_toggle_version, captured_utc, client_width,
                client_height, dpi, viewport_x, viewport_y, viewport_width,
                viewport_height, floor_key, config_digest, algorithm_version,
                source_sha256, source_path, source_media_type, source_byte_length,
                source_pixel_width, source_pixel_height, state, quality, error_code,
                error_message)
            VALUES(
                $observation_id, $project_id, $floor_id, $idempotency_key, $match_id,
                $operation_epoch, $map_toggle_version, $captured_utc, $client_width,
                $client_height, $dpi, $viewport_x, $viewport_y, $viewport_width,
                $viewport_height, $floor_key, $config_digest, $algorithm_version,
                $source_sha256, $source_path, $source_media_type, $source_byte_length,
                $source_pixel_width, $source_pixel_height, $state, $quality, $error_code,
                $error_message);
            """;
        AddObservationParameters(command, observation);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task InsertLayerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SurveyMapLayer layer,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO layers(
                layer_id, project_id, floor_id, observation_id, name, z_order,
                is_visible, is_locked, is_deleted, opacity, blend_mode,
                auto_tx, auto_ty, auto_rotation, auto_sx, auto_sy,
                manual_tx, manual_ty, manual_rotation, manual_sx, manual_sy,
                auto_revision, manual_revision)
            VALUES(
                $layer_id, $project_id, $floor_id, $observation_id, $name, $z_order,
                $is_visible, $is_locked, $is_deleted, $opacity, $blend_mode,
                $auto_tx, $auto_ty, $auto_rotation, $auto_sx, $auto_sy,
                $manual_tx, $manual_ty, $manual_rotation, $manual_sx, $manual_sy,
                $auto_revision, $manual_revision);
            """;
        AddLayerParameters(command, layer);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var editorState = connection.CreateCommand();
        editorState.Transaction = transaction;
        editorState.CommandText = """
            INSERT INTO layer_edit_state(
                layer_id, project_id, uses_cleaned_display, hidden_sha256,
                hidden_path, hidden_media_type, hidden_byte_length,
                hidden_pixel_width, hidden_pixel_height)
            VALUES($layer, $project, $cleaned, $sha, $path, $media, $bytes, $width, $height);
            """;
        editorState.Parameters.AddWithValue("$layer", layer.LayerId.ToString("N"));
        editorState.Parameters.AddWithValue("$project", layer.ProjectId.ToString("N"));
        editorState.Parameters.AddWithValue("$cleaned", layer.UsesCleanedDisplay);
        AddAssetParameters(editorState, layer.HiddenMaskAsset);
        await editorState.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task UpdateProjectAfterObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SurveyObservation observation,
        SurveyMapLayer layer,
        long revision,
        CancellationToken cancellationToken)
    {
        var project = connection.CreateCommand();
        project.Transaction = transaction;
        project.CommandText = """
            UPDATE projects SET state = $state, active_floor_key = $floor_key,
                revision = $revision, updated_utc = $updated_utc
            WHERE project_id = $project_id;
            """;
        project.Parameters.AddWithValue("$state", (int)SurveyProjectState.Collecting);
        project.Parameters.AddWithValue("$floor_key", observation.Capture.FloorKey);
        project.Parameters.AddWithValue("$revision", revision);
        project.Parameters.AddWithValue("$updated_utc", FormatDate(DateTimeOffset.UtcNow));
        project.Parameters.AddWithValue("$project_id", observation.ProjectId.ToString("N"));
        await project.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var root = connection.CreateCommand();
        root.Transaction = transaction;
        root.CommandText = """
            UPDATE floors SET root_layer_id = $layer_id
            WHERE floor_id = $floor_id AND root_layer_id IS NULL;
            """;
        root.Parameters.AddWithValue("$layer_id", layer.LayerId.ToString("N"));
        root.Parameters.AddWithValue("$floor_id", layer.FloorId.ToString("N"));
        await root.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SurveyMapLayer> ReadLayerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SurveyLayerEditRequest request,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT floor_id, observation_id, name, z_order, is_visible, is_locked,
                   is_deleted, opacity, blend_mode, auto_tx, auto_ty, auto_rotation,
                   auto_sx, auto_sy, manual_tx, manual_ty, manual_rotation, manual_sx,
                   manual_sy, auto_revision, manual_revision
            FROM layers WHERE project_id = $project_id AND layer_id = $layer_id;
            """;
        command.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
        command.Parameters.AddWithValue("$layer_id", request.LayerId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Survey layer was not found.");
        return new SurveyMapLayer(
            request.LayerId,
            request.ProjectId,
            Guid.ParseExact(reader.GetString(0), "N"),
            Guid.ParseExact(reader.GetString(1), "N"),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6),
            reader.GetDouble(7),
            (SurveyBlendMode)reader.GetInt32(8),
            ReadTransform(reader, 9),
            reader.IsDBNull(14) ? null : ReadTransform(reader, 14),
            reader.GetInt64(19),
            reader.GetInt64(20));
    }

    public static SurveyMapLayer ApplyEdit(
        SurveyMapLayer current,
        SurveyLayerEditRequest request,
        long revision)
    {
        if (request.Opacity is { } opacity && (opacity < 0d || opacity > 1d || !double.IsFinite(opacity)))
            throw new ArgumentOutOfRangeException(nameof(request), "Layer opacity must be between 0 and 1.");
        if (request.ManualTransformOverride is { IsValid: false })
            throw new ArgumentException("Manual layer transform is invalid.", nameof(request));

        var manual = request.ClearManualTransform
            ? null
            : request.ManualTransformOverride ?? current.ManualTransformOverride;
        var manualChanged = request.ClearManualTransform || request.ManualTransformOverride is not null;
        return current with
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? current.Name : request.Name.Trim(),
            ZOrder = request.ZOrder ?? current.ZOrder,
            IsVisible = request.IsVisible ?? current.IsVisible,
            IsLocked = request.IsLocked ?? current.IsLocked,
            IsDeleted = request.IsDeleted ?? current.IsDeleted,
            Opacity = request.Opacity ?? current.Opacity,
            ManualTransformOverride = manual,
            ManualTransformRevision = manualChanged ? revision : current.ManualTransformRevision
        };
    }

    public static async Task UpdateLayerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SurveyMapLayer layer,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE layers SET name = $name, z_order = $z_order,
                is_visible = $is_visible, is_locked = $is_locked,
                is_deleted = $is_deleted, opacity = $opacity,
                manual_tx = $manual_tx, manual_ty = $manual_ty,
                manual_rotation = $manual_rotation, manual_sx = $manual_sx,
                manual_sy = $manual_sy, manual_revision = $manual_revision
            WHERE project_id = $project_id AND layer_id = $layer_id;
            """;
        AddLayerParameters(command, layer);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task TouchProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        long revision,
        bool keepPublished,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = keepPublished
            ? "UPDATE projects SET revision = $revision, updated_utc = $updated_utc WHERE project_id = $project_id;"
            : """
              UPDATE projects SET revision = $revision, updated_utc = $updated_utc,
                  state = CASE WHEN state = $published THEN $needs_review ELSE state END
              WHERE project_id = $project_id;
              """;
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updated_utc", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        command.Parameters.AddWithValue("$published", (int)SurveyProjectState.Published);
        command.Parameters.AddWithValue("$needs_review", (int)SurveyProjectState.NeedsReview);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddObservationParameters(SqliteCommand command, SurveyObservation observation)
    {
        var capture = observation.Capture;
        var asset = observation.SourceAsset;
        command.Parameters.AddWithValue("$observation_id", observation.ObservationId.ToString("N"));
        command.Parameters.AddWithValue("$project_id", observation.ProjectId.ToString("N"));
        command.Parameters.AddWithValue("$floor_id", observation.FloorId.ToString("N"));
        command.Parameters.AddWithValue("$idempotency_key", observation.IdempotencyKey);
        command.Parameters.AddWithValue("$match_id", capture.MatchId.ToString("N"));
        command.Parameters.AddWithValue("$operation_epoch", capture.OperationEpoch);
        command.Parameters.AddWithValue("$map_toggle_version", capture.MapToggleVersion);
        command.Parameters.AddWithValue("$captured_utc", FormatDate(capture.CapturedAt));
        command.Parameters.AddWithValue("$client_width", capture.ClientWidth);
        command.Parameters.AddWithValue("$client_height", capture.ClientHeight);
        command.Parameters.AddWithValue("$dpi", capture.Dpi);
        command.Parameters.AddWithValue("$viewport_x", capture.ViewportBounds.X);
        command.Parameters.AddWithValue("$viewport_y", capture.ViewportBounds.Y);
        command.Parameters.AddWithValue("$viewport_width", capture.ViewportBounds.Width);
        command.Parameters.AddWithValue("$viewport_height", capture.ViewportBounds.Height);
        command.Parameters.AddWithValue("$floor_key", capture.FloorKey);
        command.Parameters.AddWithValue("$config_digest", capture.ConfigDigest);
        command.Parameters.AddWithValue("$algorithm_version", capture.AlgorithmVersion);
        command.Parameters.AddWithValue("$source_sha256", asset.Sha256);
        command.Parameters.AddWithValue("$source_path", asset.RelativePath);
        command.Parameters.AddWithValue("$source_media_type", asset.MediaType);
        command.Parameters.AddWithValue("$source_byte_length", asset.ByteLength);
        command.Parameters.AddWithValue("$source_pixel_width", asset.PixelWidth);
        command.Parameters.AddWithValue("$source_pixel_height", asset.PixelHeight);
        command.Parameters.AddWithValue("$state", (int)observation.State);
        command.Parameters.AddWithValue("$quality", observation.Quality);
        command.Parameters.AddWithValue("$error_code", (int)observation.ErrorCode);
        command.Parameters.AddWithValue("$error_message", (object?)observation.ErrorMessage ?? DBNull.Value);
    }

    private static void AddLayerParameters(SqliteCommand command, SurveyMapLayer layer)
    {
        command.Parameters.AddWithValue("$layer_id", layer.LayerId.ToString("N"));
        command.Parameters.AddWithValue("$project_id", layer.ProjectId.ToString("N"));
        command.Parameters.AddWithValue("$floor_id", layer.FloorId.ToString("N"));
        command.Parameters.AddWithValue("$observation_id", layer.ObservationId.ToString("N"));
        command.Parameters.AddWithValue("$name", layer.Name);
        command.Parameters.AddWithValue("$z_order", layer.ZOrder);
        command.Parameters.AddWithValue("$is_visible", layer.IsVisible);
        command.Parameters.AddWithValue("$is_locked", layer.IsLocked);
        command.Parameters.AddWithValue("$is_deleted", layer.IsDeleted);
        command.Parameters.AddWithValue("$opacity", layer.Opacity);
        command.Parameters.AddWithValue("$blend_mode", (int)layer.BlendMode);
        AddTransform(command, "auto", layer.AutomaticTransform);
        AddNullableTransform(command, "manual", layer.ManualTransformOverride);
        command.Parameters.AddWithValue("$auto_revision", layer.AutomaticTransformRevision);
        command.Parameters.AddWithValue("$manual_revision", layer.ManualTransformRevision);
    }

    private static void AddTransform(SqliteCommand command, string prefix, SurveyLayerTransform transform)
    {
        command.Parameters.AddWithValue($"${prefix}_tx", transform.TranslationX);
        command.Parameters.AddWithValue($"${prefix}_ty", transform.TranslationY);
        command.Parameters.AddWithValue($"${prefix}_rotation", transform.RotationDegrees);
        command.Parameters.AddWithValue($"${prefix}_sx", transform.ScaleX);
        command.Parameters.AddWithValue($"${prefix}_sy", transform.ScaleY);
    }

    private static void AddNullableTransform(
        SqliteCommand command,
        string prefix,
        SurveyLayerTransform? transform)
    {
        command.Parameters.AddWithValue($"${prefix}_tx", (object?)transform?.TranslationX ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}_ty", (object?)transform?.TranslationY ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}_rotation", (object?)transform?.RotationDegrees ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}_sx", (object?)transform?.ScaleX ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}_sy", (object?)transform?.ScaleY ?? DBNull.Value);
    }

    private static void AddAssetParameters(SqliteCommand command, SurveyAssetReference? asset)
    {
        command.Parameters.AddWithValue("$sha", (object?)asset?.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)asset?.RelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$media", (object?)asset?.MediaType ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", (object?)asset?.ByteLength ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", (object?)asset?.PixelWidth ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)asset?.PixelHeight ?? DBNull.Value);
    }

    private static SurveyLayerTransform ReadTransform(SqliteDataReader reader, int start) => new(
        reader.GetDouble(start),
        reader.GetDouble(start + 1),
        reader.GetDouble(start + 2),
        reader.GetDouble(start + 3),
        reader.GetDouble(start + 4));

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
