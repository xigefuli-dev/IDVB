using IDVBuff.Survey.Contracts;
using Microsoft.Data.Sqlite;
using static IDVBuff.Survey.Persistence.Sqlite.SqliteSurveyCommands;

namespace IDVBuff.Survey.Persistence.Sqlite;

public sealed partial class SqliteSurveyProjectRepository
{
    public async Task<IDVBuff.Survey.Domain.SurveyProjectSnapshot> ReorderLayersAsync(
        SurveyLayerOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.OrderedLayerIds.Count == 0
            || request.OrderedLayerIds.Count != request.OrderedLayerIds.Distinct().Count())
            throw new ArgumentException("Layer order must contain unique active layers.", nameof(request));
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

        var query = connection.CreateCommand();
        query.Transaction = sqlite;
        query.CommandText = """
            SELECT layer_id FROM layers
            WHERE project_id = $project_id AND floor_id = $floor_id AND is_deleted = 0;
            """;
        query.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
        query.Parameters.AddWithValue("$floor_id", request.FloorId.ToString("N"));
        var actual = new HashSet<Guid>();
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                actual.Add(Guid.ParseExact(reader.GetString(0), "N"));
        }
        if (!actual.SetEquals(request.OrderedLayerIds))
            throw new InvalidOperationException("Layer order is stale or crosses floor boundaries.");

        for (var index = 0; index < request.OrderedLayerIds.Count; index++)
        {
            var update = connection.CreateCommand();
            update.Transaction = sqlite;
            update.CommandText = """
                UPDATE layers SET z_order = $order
                WHERE project_id = $project_id AND layer_id = $layer_id;
                """;
            update.Parameters.AddWithValue("$order", request.OrderedLayerIds.Count - index - 1);
            update.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
            update.Parameters.AddWithValue("$layer_id", request.OrderedLayerIds[index].ToString("N"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            "ReorderLayers",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(connection, request.ProjectId, cancellationToken)
            .ConfigureAwait(false);
    }
}
