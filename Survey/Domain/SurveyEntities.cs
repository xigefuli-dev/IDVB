namespace IDVBuff.Survey.Domain;

public sealed record SurveyFloor(
    Guid FloorId,
    string FloorKey,
    string DisplayName,
    int Order,
    Guid? RootLayerId,
    SurveyWorldRect? WorldBounds);

public sealed record SurveyObservation(
    Guid ObservationId,
    Guid ProjectId,
    Guid FloorId,
    string IdempotencyKey,
    SurveyCaptureContext Capture,
    SurveyAssetReference SourceAsset,
    SurveyObservationState State,
    double Quality,
    SurveyErrorCode ErrorCode,
    string? ErrorMessage,
    SurveyAssetReference? StructureAsset,
    SurveyAssetReference? FeatureAsset,
    SurveyAssetReference? DisplayAsset = null,
    SurveyAssetReference? VisibleMaskAsset = null);

public sealed record SurveyMapLayer(
    Guid LayerId,
    Guid ProjectId,
    Guid FloorId,
    Guid ObservationId,
    string Name,
    int ZOrder,
    bool IsVisible,
    bool IsLocked,
    bool IsDeleted,
    double Opacity,
    SurveyBlendMode BlendMode,
    SurveyLayerTransform AutomaticTransform,
    SurveyLayerTransform? ManualTransformOverride,
    long AutomaticTransformRevision,
    long ManualTransformRevision,
    bool UsesCleanedDisplay = false,
    SurveyAssetReference? HiddenMaskAsset = null,
    SurveyAssetReference? ColorFilterAsset = null,
    double Brightness = 1d)
{
    public SurveyLayerTransform EffectiveTransform =>
        ManualTransformOverride ?? AutomaticTransform;
}

public sealed record SurveyConstraint(
    Guid ConstraintId,
    Guid ProjectId,
    Guid FloorId,
    Guid SourceLayerId,
    Guid TargetLayerId,
    SurveyLayerTransform RelativeTransform,
    double Confidence,
    double Residual,
    int InlierCount,
    string AlgorithmId,
    string AlgorithmVersion,
    bool IsAccepted,
    string? RejectionReason);

public sealed record SurveyRevision(
    long Revision,
    Guid ProjectId,
    Guid CommandId,
    string CommandType,
    DateTimeOffset CreatedAt,
    string? PayloadJson);
