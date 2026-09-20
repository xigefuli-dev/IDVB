using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Contracts;

public interface ISurveyProjectRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> CreateAsync(
        SurveyStartRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot?> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SurveyProjectSummary>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<SurveyObservationCommitResult> CommitObservationAsync(
        SurveyObservation observation,
        SurveyMapLayer layer,
        long expectedRevision,
        Guid commandId,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> EditLayerAsync(
        SurveyLayerEditRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> ApplyLayerBatchAsync(
        SurveyLayerBatchEditRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> CommitProcessingAsync(
        SurveyProcessingCommitRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> SetProjectStateAsync(
        SurveyProjectStateRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> UpdateMetadataAsync(
        SurveyProjectMetadataRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> RenameAsync(
        SurveyProjectRenameRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        SurveyProjectDeleteRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> RecordCaptureFailureAsync(
        SurveyCaptureFailureRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> ReorderLayersAsync(
        SurveyLayerOrderRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> ImportSnapshotAsync(
        SurveyProjectSnapshot snapshot,
        Guid commandId,
        CancellationToken cancellationToken = default);
}
