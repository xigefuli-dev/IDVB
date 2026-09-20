using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Editor.WinUI;

internal sealed record SurveyEditorLayerState(
    SurveyLayerTransform? ManualTransform,
    double Opacity,
    double Brightness,
    int ZOrder,
    bool IsVisible,
    bool IsLocked,
    bool IsDeleted,
    string Name,
    bool UsesCleanedDisplay,
    SurveyAssetReference? HiddenMaskAsset,
    SurveyAssetReference? ColorFilterAsset,
    SurveyObservationState ObservationState,
    SurveyErrorCode ObservationErrorCode,
    string? ObservationErrorMessage)
{
    public static SurveyEditorLayerState FromLayer(
        SurveyMapLayer layer,
        SurveyObservation observation) => new(
        layer.ManualTransformOverride,
        layer.Opacity,
        layer.Brightness,
        layer.ZOrder,
        layer.IsVisible,
        layer.IsLocked,
        layer.IsDeleted,
        layer.Name,
        layer.UsesCleanedDisplay,
        layer.HiddenMaskAsset,
        layer.ColorFilterAsset,
        observation.State,
        observation.ErrorCode,
        observation.ErrorMessage);
}

internal abstract record SurveyEditorHistoryEntry;

internal sealed record SurveyEditorLayerHistoryEntry(
    Guid LayerId,
    SurveyEditorLayerState Before,
    SurveyEditorLayerState After) : SurveyEditorHistoryEntry;

internal sealed record SurveyEditorOrderHistoryEntry(
    Guid FloorId,
    IReadOnlyList<Guid> Before,
    IReadOnlyList<Guid> After) : SurveyEditorHistoryEntry;

internal sealed record SurveyEditorBatchHistoryEntry(
    IReadOnlyDictionary<Guid, SurveyEditorLayerState> Before,
    IReadOnlyDictionary<Guid, SurveyEditorLayerState> After) : SurveyEditorHistoryEntry;
