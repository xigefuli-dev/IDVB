namespace IDVBuff.Features.Maps;

public enum MapSessionState
{
    Closed,
    OpeningDetected,
    WaitingForStableFrames,
    IdentifyingMap,
    CoarseLocating,
    FineLocating,
    Confirming,
    Locked,
    LowConfidence,
    Lost,
    RecalibrationRequired
}

public enum MapRecalibrationReason
{
    None,
    MapReopened,
    WindowChanged,
    ResolutionChanged,
    DpiChanged,
    ViewportChanged,
    NativeScaleChanged,
    NativeRotationChanged,
    BackgroundMismatch,
    TransformError,
    MapIdentityChanged,
    FloorChanged,
    AlignmentLost
}

public enum MapLocationMethod
{
    None,
    DualAnchor,
    SingleAnchor,
    AuxiliaryAnchor,
    StructureTranslation,
    Manual
}
