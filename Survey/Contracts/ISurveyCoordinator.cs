using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Contracts;

public interface ISurveyStatusSource
{
    SurveyStatusSnapshot Status { get; }
    event EventHandler<SurveyStatusSnapshot>? StatusChanged;
}

public interface ISurveyCoordinator : ISurveyStatusSource, IAsyncDisposable
{
    Guid? ArmedResumeProjectId { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task ArmResumeAsync(
        Guid? projectId,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> StartAsync(
        SurveyStartRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyObservationCommitResult>> AddObservationAsync(
        SurveyObservationRequest request,
        CancellationToken cancellationToken = default);

    Task SetRuntimeStateAsync(
        Guid projectId,
        SurveyRuntimeState state,
        string? message,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> EditLayerAsync(
        SurveyLayerEditRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> ApplyLayerBatchAsync(
        SurveyLayerBatchEditRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> ToggleLayerDecontaminationAsync(
        SurveyLayerDecontaminationRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyLayerOperationResult>> AlignLayersAsync(
        SurveyLayerAlignmentRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyLayerOperationResult>> NormalizeLayerColorsAsync(
        SurveyLayerColorNormalizationRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyLayerOperationResult>> ApplyMaskStrokeAsync(
        SurveyMaskStrokeRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> SetProjectStateAsync(
        SurveyProjectStateRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> UpdateMetadataAsync(
        SurveyProjectMetadataRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> RenameProjectAsync(
        SurveyProjectRenameRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<bool>> DeleteProjectAsync(
        SurveyProjectDeleteRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> RecordCaptureFailureAsync(
        SurveyCaptureFailureRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> ReorderLayersAsync(
        SurveyLayerOrderRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> EndAsync(
        SurveyEndRequest request,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot?> GetProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenAssetAsync(
        Guid projectId,
        SurveyAssetReference asset,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenRenderedLayerAsync(
        Guid projectId,
        Guid layerId,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenRenderedLayerAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SurveyProjectSummary>> ListProjectsAsync(
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyProjectSnapshot>> DuplicateProjectAsync(
        Guid projectId,
        string? name = null,
        CancellationToken cancellationToken = default);

    Task<SurveyOperationResult<SurveyDualOutput>> RenderOutputsAsync(
        Guid projectId,
        string floorKey,
        CancellationToken cancellationToken = default);
}
