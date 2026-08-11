using System.Globalization;
using IDVBuff.Survey.Domain;
using Microsoft.Data.Sqlite;

namespace IDVBuff.Survey.Persistence.Sqlite;

internal static class SqliteSurveyProjectReader
{
    public static async Task<SurveyProjectSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await ReadProjectAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
            return null;
        var floors = await ReadFloorsAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        var observations = await ReadObservationsAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        var layers = await ReadLayersAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        var constraints = await ReadConstraintsAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        return new SurveyProjectSnapshot(project, floors, observations, layers, constraints);
    }

    private static async Task<SurveyProject?> ReadProjectAsync(
        SqliteConnection connection,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, name, map_class, state, created_utc, updated_utc,
                   revision, config_digest, algorithm_version, active_floor_key,
                   published_revision
            FROM projects WHERE project_id = $project_id;
            """;
        command.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new SurveyProject(
            projectId,
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            (SurveyProjectState)reader.GetInt32(3),
            ParseDate(reader.GetString(4)),
            ParseDate(reader.GetString(5)),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10));
    }

    private static async Task<IReadOnlyList<SurveyFloor>> ReadFloorsAsync(
        SqliteConnection connection,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var result = new List<SurveyFloor>();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT floor_id, floor_key, display_name, sort_order, root_layer_id,
                   world_x, world_y, world_width, world_height
            FROM floors WHERE project_id = $project_id ORDER BY sort_order, floor_key;
            """;
        command.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            SurveyWorldRect? bounds = reader.IsDBNull(5)
                ? null
                : new SurveyWorldRect(
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7),
                    reader.GetDouble(8));
            result.Add(new SurveyFloor(
                ParseGuid(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : ParseGuid(reader.GetString(4)),
                bounds));
        }
        return result;
    }

    private static async Task<IReadOnlyList<SurveyObservation>> ReadObservationsAsync(
        SqliteConnection connection,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var result = new List<SurveyObservation>();
        var derivedAssets = await ReadObservationAssetsAsync(
            connection,
            projectId,
            cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT observation_id, floor_id, idempotency_key, match_id, operation_epoch,
                   map_toggle_version, captured_utc, client_width, client_height, dpi,
                   viewport_x, viewport_y, viewport_width, viewport_height, floor_key,
                   config_digest, algorithm_version, source_sha256, source_path,
                   source_media_type, source_byte_length, source_pixel_width,
                   source_pixel_height, state, quality, error_code, error_message
            FROM observations WHERE project_id = $project_id ORDER BY captured_utc, observation_id;
            """;
        command.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var observationId = ParseGuid(reader.GetString(0));
            var capture = new SurveyCaptureContext(
                ParseGuid(reader.GetString(3)),
                reader.GetInt64(4),
                reader.GetInt64(5),
                ParseDate(reader.GetString(6)),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetDouble(9),
                new SurveyPixelRect(
                    reader.GetDouble(10),
                    reader.GetDouble(11),
                    reader.GetDouble(12),
                    reader.GetDouble(13)),
                reader.GetString(14),
                reader.GetString(15),
                reader.GetString(16));
            var asset = new SurveyAssetReference(
                reader.GetString(17),
                reader.GetString(18),
                reader.GetString(19),
                reader.GetInt64(20),
                reader.GetInt32(21),
                reader.GetInt32(22));
            result.Add(new SurveyObservation(
                observationId,
                projectId,
                ParseGuid(reader.GetString(1)),
                reader.GetString(2),
                capture,
                asset,
                (SurveyObservationState)reader.GetInt32(23),
                reader.GetDouble(24),
                (SurveyErrorCode)reader.GetInt32(25),
                reader.IsDBNull(26) ? null : reader.GetString(26),
                derivedAssets.GetValueOrDefault((observationId, "structure")),
                derivedAssets.GetValueOrDefault((observationId, "features")),
                derivedAssets.GetValueOrDefault((observationId, "display")),
                derivedAssets.GetValueOrDefault((observationId, "visible-mask"))));
        }
        return result;
    }

    private static async Task<Dictionary<(Guid ObservationId, string Kind), SurveyAssetReference>>
        ReadObservationAssetsAsync(
            SqliteConnection connection,
            Guid projectId,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<(Guid, string), SurveyAssetReference>();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT observation_id, asset_kind, sha256, relative_path, media_type,
                   byte_length, pixel_width, pixel_height
            FROM observation_assets WHERE project_id = $project_id;
            """;
        command.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[(ParseGuid(reader.GetString(0)), reader.GetString(1))] = new SurveyAssetReference(
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt32(6),
                reader.GetInt32(7));
        }
        return result;
    }

    private static async Task<IReadOnlyList<SurveyMapLayer>> ReadLayersAsync(
        SqliteConnection connection,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var result = new List<SurveyMapLayer>();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.layer_id, l.floor_id, l.observation_id, l.name, l.z_order, l.is_visible,
                   l.is_locked, l.is_deleted, l.opacity, l.blend_mode, l.auto_tx, l.auto_ty,
                   l.auto_rotation, l.auto_sx, l.auto_sy, l.manual_tx, l.manual_ty,
                   l.manual_rotation, l.manual_sx, l.manual_sy, l.auto_revision, l.manual_revision,
                   COALESCE(e.uses_cleaned_display, 0), e.hidden_sha256,
                   e.hidden_path, e.hidden_media_type, e.hidden_byte_length,
                   e.hidden_pixel_width, e.hidden_pixel_height
            FROM layers l
            LEFT JOIN layer_edit_state e ON e.layer_id = l.layer_id
            WHERE l.project_id = $project_id ORDER BY z_order DESC, l.layer_id;
            """;
        command.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var automatic = ReadTransform(reader, 10);
            SurveyLayerTransform? manual = reader.IsDBNull(15)
                ? null
                : ReadTransform(reader, 15);
            SurveyAssetReference? hiddenMask = reader.IsDBNull(23)
                ? null
                : new SurveyAssetReference(
                    reader.GetString(23),
                    reader.GetString(24),
                    reader.GetString(25),
                    reader.GetInt64(26),
                    reader.GetInt32(27),
                    reader.GetInt32(28));
            result.Add(new SurveyMapLayer(
                ParseGuid(reader.GetString(0)),
                projectId,
                ParseGuid(reader.GetString(1)),
                ParseGuid(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetDouble(8),
                (SurveyBlendMode)reader.GetInt32(9),
                automatic,
                manual,
                reader.GetInt64(20),
                reader.GetInt64(21),
                reader.GetBoolean(22),
                hiddenMask));
        }
        return result;
    }

    private static async Task<IReadOnlyList<SurveyConstraint>> ReadConstraintsAsync(
        SqliteConnection connection,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var result = new List<SurveyConstraint>();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT constraint_id, floor_id, source_layer_id, target_layer_id,
                   tx, ty, rotation, sx, sy, confidence, residual, inlier_count,
                   algorithm_id, algorithm_version, is_accepted, rejection_reason
            FROM constraints WHERE project_id = $project_id;
            """;
        command.Parameters.AddWithValue("$project_id", projectId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SurveyConstraint(
                ParseGuid(reader.GetString(0)),
                projectId,
                ParseGuid(reader.GetString(1)),
                ParseGuid(reader.GetString(2)),
                ParseGuid(reader.GetString(3)),
                ReadTransform(reader, 4),
                reader.GetDouble(9),
                reader.GetDouble(10),
                reader.GetInt32(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetBoolean(14),
                reader.IsDBNull(15) ? null : reader.GetString(15)));
        }
        return result;
    }

    private static SurveyLayerTransform ReadTransform(SqliteDataReader reader, int start) => new(
        reader.GetDouble(start),
        reader.GetDouble(start + 1),
        reader.GetDouble(start + 2),
        reader.GetDouble(start + 3),
        reader.GetDouble(start + 4));

    private static Guid ParseGuid(string value) => Guid.ParseExact(value, "N");

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
