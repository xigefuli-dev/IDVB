using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Contracts;

public sealed record SurveyPreprocessRequest(
    Guid ProjectId,
    SurveyObservation Observation);

public sealed record SurveyPreprocessResult(
    SurveyAssetReference? StructureAsset,
    SurveyAssetReference? FeatureAsset,
    double Quality,
    bool IsUsable,
    string? RejectionReason,
    SurveyAssetReference? DisplayAsset = null,
    SurveyAssetReference? VisibleMaskAsset = null);

public sealed record SurveyRegistrationRequest(
    SurveyObservation SourceObservation,
    SurveyObservation TargetObservation,
    SurveyMapLayer TargetLayer,
    SurveyAssetReference? SourceImageAsset = null,
    SurveyAssetReference? TargetImageAsset = null);

public sealed record SurveyRegistrationResult(
    bool IsAccepted,
    SurveyLayerTransform RelativeTransform,
    double Confidence,
    double Residual,
    int InlierCount,
    string AlgorithmId,
    string AlgorithmVersion,
    string? RejectionReason);

public interface ISurveyPreprocessor
{
    Task<SurveyPreprocessResult> ProcessAsync(
        SurveyPreprocessRequest request,
        CancellationToken cancellationToken = default);
}

public interface ISurveyLayerRasterEditor
{
    Task<SurveyAssetReference> NormalizeColorsAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        SurveyMapLayer anchorLayer,
        SurveyObservation anchorObservation,
        CancellationToken cancellationToken = default);

    Task<SurveyAssetReference?> ApplyHiddenMaskAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        IReadOnlyList<SurveyWorldPoint> worldPoints,
        double size,
        SurveyBrushShape shape,
        CancellationToken cancellationToken = default);

    Task<ReadOnlyMemory<byte>> RenderLayerAsync(
        Guid projectId,
        SurveyMapLayer layer,
        SurveyObservation observation,
        CancellationToken cancellationToken = default);
}

public interface ISurveyPairRegistrar
{
    Task<SurveyRegistrationResult> RegisterAsync(
        SurveyRegistrationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPoseGraphOptimizer
{
    Task<IReadOnlyDictionary<Guid, SurveyLayerTransform>> OptimizeAsync(
        SurveyProjectSnapshot project,
        CancellationToken cancellationToken = default);
}
