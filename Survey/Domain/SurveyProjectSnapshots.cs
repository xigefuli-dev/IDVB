namespace IDVBuff.Survey.Domain;

public sealed record SurveyProject(
    Guid ProjectId,
    int SchemaVersion,
    string Name,
    string MapClass,
    SurveyProjectState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Revision,
    string ConfigDigest,
    string AlgorithmVersion,
    string ActiveFloorKey,
    long? PublishedRevision);

public sealed record SurveyProjectSnapshot(
    SurveyProject Project,
    IReadOnlyList<SurveyFloor> Floors,
    IReadOnlyList<SurveyObservation> Observations,
    IReadOnlyList<SurveyMapLayer> Layers,
    IReadOnlyList<SurveyConstraint> Constraints)
{
    public IEnumerable<SurveyMapLayer> ActiveLayers(string floorKey) =>
        from layer in Layers
        join floor in Floors on layer.FloorId equals floor.FloorId
        where !layer.IsDeleted
              && string.Equals(floor.FloorKey, floorKey, StringComparison.OrdinalIgnoreCase)
        orderby layer.ZOrder descending
        select layer;
}

public sealed record SurveyProjectSummary(
    Guid ProjectId,
    string Name,
    string MapClass,
    SurveyProjectState State,
    DateTimeOffset UpdatedAt,
    long Revision,
    int ObservationCount,
    int ActiveLayerCount,
    int UnregisteredCount);

public sealed record SurveyStatusSnapshot(
    Guid? ProjectId,
    string? ProjectName,
    SurveyProjectState? ProjectState,
    SurveyRuntimeState RuntimeState,
    string? FloorKey,
    int ObservationCount,
    int RegisteredCount,
    int UnregisteredCount,
    int DeletedCount,
    int PendingCount,
    DateTimeOffset? LastCaptureAt,
    long Revision,
    bool IsSaving,
    SurveyErrorCode LastErrorCode,
    string? LastMessage,
    string? DiagnosticId)
{
    public static SurveyStatusSnapshot Inactive { get; } = new(
        null,
        null,
        null,
        SurveyRuntimeState.Inactive,
        null,
        0,
        0,
        0,
        0,
        0,
        null,
        0,
        false,
        SurveyErrorCode.None,
        null,
        null);
}
