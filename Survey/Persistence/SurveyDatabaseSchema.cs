using Microsoft.Data.Sqlite;

namespace IDVBuff.Survey.Persistence.Sqlite;

internal static class SurveyDatabaseSchema
{
    public const int CurrentVersion = 5;

    public static async Task<int> ReadStoredVersionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
            return 0;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta';";
        if (Convert.ToInt64(await table.ExecuteScalarAsync(cancellationToken)) == 0)
            return 0;
        var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM meta WHERE key = 'survey_schema_version';";
        var value = Convert.ToString(await version.ExecuteScalarAsync(cancellationToken));
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    public static async Task CreateBackupAsync(
        string databasePath,
        string backupPath,
        CancellationToken cancellationToken)
    {
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    public static async Task EnsureAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var storedVersion = await ReadVersionFromOpenConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (storedVersion > CurrentVersion)
        {
            throw new InvalidDataException(
                $"Survey schema {storedVersion} requires a newer version of Identity Vision Bridge.");
        }
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS projects (
                project_id TEXT PRIMARY KEY,
                schema_version INTEGER NOT NULL,
                name TEXT NOT NULL,
                map_class TEXT NOT NULL,
                state INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                revision INTEGER NOT NULL,
                config_digest TEXT NOT NULL,
                algorithm_version TEXT NOT NULL,
                active_floor_key TEXT NOT NULL,
                published_revision INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS floors (
                floor_id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(project_id),
                floor_key TEXT NOT NULL,
                display_name TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                root_layer_id TEXT NULL,
                world_x REAL NULL,
                world_y REAL NULL,
                world_width REAL NULL,
                world_height REAL NULL,
                UNIQUE(project_id, floor_key)
            );

            CREATE TABLE IF NOT EXISTS observations (
                observation_id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(project_id),
                floor_id TEXT NOT NULL REFERENCES floors(floor_id),
                idempotency_key TEXT NOT NULL UNIQUE,
                match_id TEXT NOT NULL,
                operation_epoch INTEGER NOT NULL,
                map_toggle_version INTEGER NOT NULL,
                captured_utc TEXT NOT NULL,
                client_width INTEGER NOT NULL,
                client_height INTEGER NOT NULL,
                dpi REAL NOT NULL,
                viewport_x REAL NOT NULL,
                viewport_y REAL NOT NULL,
                viewport_width REAL NOT NULL,
                viewport_height REAL NOT NULL,
                floor_key TEXT NOT NULL,
                config_digest TEXT NOT NULL,
                algorithm_version TEXT NOT NULL,
                source_sha256 TEXT NOT NULL,
                source_path TEXT NOT NULL,
                source_media_type TEXT NOT NULL,
                source_byte_length INTEGER NOT NULL,
                source_pixel_width INTEGER NOT NULL,
                source_pixel_height INTEGER NOT NULL,
                state INTEGER NOT NULL,
                quality REAL NOT NULL,
                error_code INTEGER NOT NULL,
                error_message TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS layers (
                layer_id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(project_id),
                floor_id TEXT NOT NULL REFERENCES floors(floor_id),
                observation_id TEXT NOT NULL REFERENCES observations(observation_id),
                name TEXT NOT NULL,
                z_order INTEGER NOT NULL,
                is_visible INTEGER NOT NULL,
                is_locked INTEGER NOT NULL,
                is_deleted INTEGER NOT NULL,
                opacity REAL NOT NULL,
                blend_mode INTEGER NOT NULL,
                auto_tx REAL NOT NULL,
                auto_ty REAL NOT NULL,
                auto_rotation REAL NOT NULL,
                auto_sx REAL NOT NULL,
                auto_sy REAL NOT NULL,
                manual_tx REAL NULL,
                manual_ty REAL NULL,
                manual_rotation REAL NULL,
                manual_sx REAL NULL,
                manual_sy REAL NULL,
                auto_revision INTEGER NOT NULL,
                manual_revision INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS layer_edit_state (
                layer_id TEXT PRIMARY KEY REFERENCES layers(layer_id),
                project_id TEXT NOT NULL REFERENCES projects(project_id),
                uses_cleaned_display INTEGER NOT NULL DEFAULT 0,
                hidden_sha256 TEXT NULL,
                hidden_path TEXT NULL,
                hidden_media_type TEXT NULL,
                hidden_byte_length INTEGER NULL,
                hidden_pixel_width INTEGER NULL,
                hidden_pixel_height INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS constraints (
                constraint_id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(project_id),
                floor_id TEXT NOT NULL REFERENCES floors(floor_id),
                source_layer_id TEXT NOT NULL REFERENCES layers(layer_id),
                target_layer_id TEXT NOT NULL REFERENCES layers(layer_id),
                tx REAL NOT NULL,
                ty REAL NOT NULL,
                rotation REAL NOT NULL,
                sx REAL NOT NULL,
                sy REAL NOT NULL,
                confidence REAL NOT NULL,
                residual REAL NOT NULL,
                inlier_count INTEGER NOT NULL,
                algorithm_id TEXT NOT NULL,
                algorithm_version TEXT NOT NULL,
                is_accepted INTEGER NOT NULL,
                rejection_reason TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS revisions (
                revision INTEGER PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(project_id),
                command_id TEXT NOT NULL,
                command_type TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                payload_json TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS capture_attempts (
                attempt_id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(project_id),
                match_id TEXT NOT NULL,
                operation_epoch INTEGER NOT NULL,
                map_toggle_version INTEGER NOT NULL,
                floor_key TEXT NOT NULL,
                occurred_utc TEXT NOT NULL,
                error_code INTEGER NOT NULL,
                message TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS observation_assets (
                observation_id TEXT NOT NULL REFERENCES observations(observation_id),
                project_id TEXT NOT NULL REFERENCES projects(project_id),
                asset_kind TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                media_type TEXT NOT NULL,
                byte_length INTEGER NOT NULL,
                pixel_width INTEGER NOT NULL,
                pixel_height INTEGER NOT NULL,
                PRIMARY KEY(observation_id, asset_kind)
            );

            CREATE INDEX IF NOT EXISTS ix_observations_project_floor
                ON observations(project_id, floor_id, captured_utc);
            CREATE INDEX IF NOT EXISTS ix_layers_project_floor_order
                ON layers(project_id, floor_id, z_order);
            CREATE INDEX IF NOT EXISTS ix_layer_edit_state_project
                ON layer_edit_state(project_id);
            CREATE INDEX IF NOT EXISTS ix_capture_attempts_project_time
                ON capture_attempts(project_id, occurred_utc);

            INSERT OR IGNORE INTO layer_edit_state(
                layer_id, project_id, uses_cleaned_display)
            SELECT l.layer_id, l.project_id,
                CASE WHEN EXISTS(
                    SELECT 1 FROM observation_assets a
                    WHERE a.observation_id = l.observation_id
                        AND a.asset_kind = 'display')
                THEN 1 ELSE 0 END
            FROM layers l;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = """
            INSERT INTO meta(key, value) VALUES('survey_schema_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        versionCommand.Parameters.AddWithValue("$version", CurrentVersion.ToString());
        await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadVersionFromOpenConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta';";
        if (Convert.ToInt64(await table.ExecuteScalarAsync(cancellationToken)) == 0)
            return 0;
        var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM meta WHERE key = 'survey_schema_version';";
        var value = Convert.ToString(await version.ExecuteScalarAsync(cancellationToken));
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }
}
