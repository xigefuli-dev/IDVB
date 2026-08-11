using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using Microsoft.Data.Sqlite;
using static IDVBuff.Survey.Persistence.Sqlite.SqliteSurveyCommands;

namespace IDVBuff.Survey.Persistence.Sqlite;

public sealed partial class SqliteSurveyProjectRepository
{
    public async Task<SurveyProjectSnapshot> UpdateMetadataAsync(
        SurveyProjectMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.MapClass))
            throw new ArgumentException("Project name and class are required.", nameof(request));
        if (request.FloorId is not null && string.IsNullOrWhiteSpace(request.FloorDisplayName))
            throw new ArgumentException("Floor display name is required.", nameof(request));

        await using var connection = await OpenAsync(request.ProjectId, false, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqlite = (SqliteTransaction)transaction;
        var revision = await ReadRevisionAsync(
            connection,
            sqlite,
            request.ProjectId,
            cancellationToken).ConfigureAwait(false);
        EnsureRevision(request.ProjectId, request.ExpectedRevision, revision);
        var stateCommand = connection.CreateCommand();
        stateCommand.Transaction = sqlite;
        stateCommand.CommandText = "SELECT state FROM projects WHERE project_id = $project_id;";
        stateCommand.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
        var state = Convert.ToInt32(await stateCommand.ExecuteScalarAsync(cancellationToken));
        if ((SurveyProjectState)state == SurveyProjectState.Archived)
            throw new InvalidOperationException("Archived survey projects are read-only.");

        var project = connection.CreateCommand();
        project.Transaction = sqlite;
        project.CommandText = """
            UPDATE projects SET name = $name, map_class = $class
            WHERE project_id = $project_id;
            """;
        project.Parameters.AddWithValue("$name", request.Name.Trim());
        project.Parameters.AddWithValue("$class", request.MapClass.Trim());
        project.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
        await project.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (request.FloorId is { } floorId)
        {
            var floor = connection.CreateCommand();
            floor.Transaction = sqlite;
            floor.CommandText = """
                UPDATE floors SET display_name = $name
                WHERE project_id = $project_id AND floor_id = $floor_id;
                """;
            floor.Parameters.AddWithValue("$name", request.FloorDisplayName!.Trim());
            floor.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
            floor.Parameters.AddWithValue("$floor_id", floorId.ToString("N"));
            if (await floor.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Survey floor was not found.");
        }

        var nextRevision = revision + 1;
        await TouchProjectAsync(
            connection,
            sqlite,
            request.ProjectId,
            nextRevision,
            keepPublished: false,
            cancellationToken).ConfigureAwait(false);
        await InsertRevisionAsync(
            connection,
            sqlite,
            request.ProjectId,
            nextRevision,
            request.CommandId,
            "UpdateProjectMetadata",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(connection, request.ProjectId, cancellationToken)
            .ConfigureAwait(false);
    }
}
