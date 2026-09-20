using IDVBuff.Survey.Contracts;
using Microsoft.Data.Sqlite;
using static IDVBuff.Survey.Persistence.Sqlite.SqliteSurveyCommands;

namespace IDVBuff.Survey.Persistence.Sqlite;

public sealed partial class SqliteSurveyProjectRepository
{
    public async Task<IDVBuff.Survey.Domain.SurveyProjectSnapshot> RecordCaptureFailureAsync(
        SurveyCaptureFailureRequest request,
        CancellationToken cancellationToken = default)
    {
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

        var command = connection.CreateCommand();
        command.Transaction = sqlite;
        command.CommandText = """
            INSERT INTO capture_attempts(
                attempt_id, project_id, match_id, operation_epoch,
                map_toggle_version, floor_key, occurred_utc, error_code, message)
            VALUES($id, $project, $match, $epoch, $toggle, $floor, $time, $error, $message);
            """;
        command.Parameters.AddWithValue("$id", request.CommandId.ToString("N"));
        command.Parameters.AddWithValue("$project", request.ProjectId.ToString("N"));
        command.Parameters.AddWithValue("$match", request.MatchId.ToString("N"));
        command.Parameters.AddWithValue("$epoch", request.OperationEpoch);
        command.Parameters.AddWithValue("$toggle", request.MapToggleVersion);
        command.Parameters.AddWithValue("$floor", request.FloorKey.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$time", FormatDate(request.OccurredAt));
        command.Parameters.AddWithValue("$error", (int)request.ErrorCode);
        command.Parameters.AddWithValue("$message", request.Message);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var nextRevision = revision + 1;
        await TouchProjectAsync(
            connection,
            sqlite,
            request.ProjectId,
            nextRevision,
            keepPublished: true,
            cancellationToken).ConfigureAwait(false);
        await InsertRevisionAsync(
            connection,
            sqlite,
            request.ProjectId,
            nextRevision,
            request.CommandId,
            "CaptureFailed",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(connection, request.ProjectId, cancellationToken)
            .ConfigureAwait(false);
    }
}
