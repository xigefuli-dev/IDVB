namespace IDVBuff.Survey.Contracts;

public sealed class SurveyRevisionConflictException(
    Guid projectId,
    long expectedRevision,
    long actualRevision)
    : InvalidOperationException(
        $"Survey project {projectId} revision conflict: expected {expectedRevision}, actual {actualRevision}.")
{
    public Guid ProjectId { get; } = projectId;
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}

public sealed class SurveyProjectNotFoundException(Guid projectId)
    : InvalidOperationException($"Survey project {projectId} was not found.")
{
    public Guid ProjectId { get; } = projectId;
}
