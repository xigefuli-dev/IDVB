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

public readonly record struct SurveyColor(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParseHex(string? value, out SurveyColor color)
    {
        color = default;
        if (value is null) return false;
        var text = value.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var rgb)) return false;
        color = new SurveyColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
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
