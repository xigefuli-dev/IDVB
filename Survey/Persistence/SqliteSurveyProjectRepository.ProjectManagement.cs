using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using Microsoft.Data.Sqlite;
using static IDVBuff.Survey.Persistence.Sqlite.SqliteSurveyCommands;

namespace IDVBuff.Survey.Persistence.Sqlite;

public sealed partial class SqliteSurveyProjectRepository
{
    public async Task<SurveyProjectSnapshot> RenameAsync(
        SurveyProjectRenameRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
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

        var rename = connection.CreateCommand();
        rename.Transaction = sqlite;
        rename.CommandText = "UPDATE projects SET name = $name WHERE project_id = $project_id;";
        rename.Parameters.AddWithValue("$name", request.Name.Trim());
        rename.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
        if (await rename.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new SurveyProjectNotFoundException(request.ProjectId);

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
            "RenameProject",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(connection, request.ProjectId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        SurveyProjectDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var gate = _projectOpenGates.GetOrAdd(
            request.ProjectId,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var projectDirectory = Path.GetFullPath(_paths.ProjectDirectory(request.ProjectId));
            var root = Path.GetFullPath(_paths.RootDirectory);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!projectDirectory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Survey project path escapes the storage root.");

            await using (var connection = await OpenCoreAsync(
                request.ProjectId,
                create: false,
                cancellationToken).ConfigureAwait(false))
            {
                var revision = connection.CreateCommand();
                revision.CommandText = "SELECT revision FROM projects WHERE project_id = $project_id;";
                revision.Parameters.AddWithValue("$project_id", request.ProjectId.ToString("N"));
                var value = await revision.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is null)
                    throw new SurveyProjectNotFoundException(request.ProjectId);
                EnsureRevision(request.ProjectId, request.ExpectedRevision, Convert.ToInt64(value));
            }

            Directory.Delete(projectDirectory, recursive: true);
        }
        finally
        {
            gate.Release();
        }
    }
}
