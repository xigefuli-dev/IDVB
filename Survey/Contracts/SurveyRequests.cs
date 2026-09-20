using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Contracts;

public sealed record SurveyStartRequest(
    Guid CommandId,
    Guid MatchId,
    long OperationEpoch,
    string MapClass,
    string FloorKey,
    string? Name,
    string ConfigDigest,
    string AlgorithmVersion,
    Guid? ResumeProjectId = null);

public sealed record SurveyEncodedFrame(
    ReadOnlyMemory<byte> Bytes,
    string FileExtension,
    string MediaType,
    int PixelWidth,
    int PixelHeight,
    SurveyCaptureContext Capture);

public sealed record SurveyObservationRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    SurveyEncodedFrame Frame);

public sealed record SurveyObservationImportRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    SurveyEncodedFrame Frame,
    string? LayerName = null);

public sealed record SurveyLayerEditRequest(
    Guid CommandId,
    Guid ProjectId,
    Guid LayerId,
    long ExpectedRevision,
    SurveyLayerTransform? ManualTransformOverride = null,
    bool ClearManualTransform = false,
    double? Opacity = null,
    int? ZOrder = null,
    bool? IsVisible = null,
    bool? IsLocked = null,
    bool? IsDeleted = null,
    string? Name = null,
    bool SetAsFloorRoot = false,
    double? Brightness = null);

public sealed record SurveyProcessingCommitRequest(
    Guid CommandId,
    Guid ProjectId,
    Guid ObservationId,
    Guid LayerId,
    long ExpectedRevision,
    SurveyObservationState ObservationState,
    double Quality,
    SurveyErrorCode ErrorCode,
    string? ErrorMessage,
    SurveyLayerTransform AutomaticTransform,
    SurveyConstraint? Constraint,
    SurveyAssetReference? StructureAsset = null,
    SurveyAssetReference? FeatureAsset = null,
    SurveyAssetReference? DisplayAsset = null,
    SurveyAssetReference? VisibleMaskAsset = null,
    bool? UsesCleanedDisplay = null);

public sealed record SurveyEndRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    Guid MatchId,
    long OperationEpoch);

public sealed record SurveyProjectStateRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    SurveyProjectState State);

public sealed record SurveyProjectMetadataRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    string Name,
    string MapClass,
    Guid? FloorId = null,
    string? FloorDisplayName = null);

public sealed record SurveyProjectRenameRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    string Name);

public sealed record SurveyProjectDeleteRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision);

public sealed record SurveyCaptureFailureRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    Guid MatchId,
    long OperationEpoch,
    long MapToggleVersion,
    string FloorKey,
    DateTimeOffset OccurredAt,
    SurveyErrorCode ErrorCode,
    string Message);

public sealed record SurveyLayerOrderRequest(
    Guid CommandId,
    Guid ProjectId,
    Guid FloorId,
    long ExpectedRevision,
    IReadOnlyList<Guid> OrderedLayerIds);

public sealed record SurveyLayerMutation(
    Guid LayerId,
    bool? UsesCleanedDisplay = null,
    SurveyAssetReference? HiddenMaskAsset = null,
    bool ReplaceHiddenMask = false,
    SurveyAssetReference? ColorFilterAsset = null,
    bool ReplaceColorFilter = false,
    SurveyLayerTransform? ManualTransformOverride = null,
    bool ReplaceManualTransform = false,
    SurveyObservationState? ObservationState = null,
    SurveyErrorCode? ObservationErrorCode = null,
    string? ObservationErrorMessage = null,
    bool ReplaceObservationStatus = false);

public sealed record SurveyLayerBatchEditRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    IReadOnlyList<SurveyLayerMutation> Mutations);

public sealed record SurveyLayerDecontaminationRequest(
    Guid CommandId,
    Guid ProjectId,
    Guid LayerId,
    long ExpectedRevision);

public sealed record SurveyLayerAlignmentRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    Guid AnchorLayerId,
    IReadOnlyList<Guid> LayerIds);

public sealed record SurveyLayerColorNormalizationRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    Guid AnchorLayerId,
    IReadOnlyList<Guid> LayerIds);

public sealed record SurveyLayerVignetteCorrectionRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    IReadOnlyList<Guid> LayerIds,
    double CompensationStart,
    double CompensationStrength);

public sealed record SurveyLayerColorTemplateRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    IReadOnlyList<Guid> LayerIds,
    IReadOnlyList<SurveyColorTemplateEntry> Entries);

public sealed record SurveyMaskStrokeRequest(
    Guid CommandId,
    Guid ProjectId,
    long ExpectedRevision,
    Guid FloorId,
    IReadOnlyList<Guid> LayerIds,
    IReadOnlyList<SurveyWorldPoint> Points,
    double Size,
    SurveyBrushShape Shape);

public sealed record SurveyColorBrushRequest(
    Guid CommandId,
    Guid ProjectId,
    Guid LayerId,
    long ExpectedRevision,
    IReadOnlyList<SurveyWorldPoint> Points,
    double Size,
    SurveyBrushShape Shape,
    SurveyColor Color);

public sealed record SurveyColorFillRequest(
    Guid CommandId,
    Guid ProjectId,
    Guid LayerId,
    long ExpectedRevision,
    int PixelX,
    int PixelY,
    byte Tolerance,
    SurveyColor Color);
