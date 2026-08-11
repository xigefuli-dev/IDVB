namespace IDVBuff.Survey.Domain;

public enum SurveyProjectState
{
    Draft,
    Collecting,
    NeedsReview,
    ReadyToPublish,
    Published,
    Archived
}

public enum SurveyRuntimeState
{
    Inactive,
    Activating,
    WaitingForMapOpen,
    WaitingForStableFrame,
    ProcessingObservation,
    Committing,
    WaitingForNextOpen,
    Paused,
    Ending,
    Faulted
}

public enum SurveyObservationState
{
    Captured,
    Preprocessed,
    Registered,
    Unregistered,
    Corrupt
}

public enum SurveyBlendMode
{
    Normal
}

public enum SurveyBrushShape
{
    Circle,
    Square
}

public enum SurveyErrorCode
{
    None,
    InvalidState,
    ProjectNotFound,
    RevisionConflict,
    CaptureFailed,
    FrameInvalid,
    AssetWriteFailed,
    StorageUnavailable,
    RegistrationRejected,
    ProjectArchived,
    UnsupportedSchema,
    Cancelled,
    PreprocessingFailed,
    Unknown
}
