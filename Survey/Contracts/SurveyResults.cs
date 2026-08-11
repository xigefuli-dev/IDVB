using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Contracts;

public sealed record SurveyOperationResult<T>(
    bool Succeeded,
    T? Value,
    SurveyErrorCode ErrorCode,
    string? Message,
    string? DiagnosticId = null)
{
    public static SurveyOperationResult<T> Success(T value, string? message = null) =>
        new(true, value, SurveyErrorCode.None, message);

    public static SurveyOperationResult<T> Failure(
        SurveyErrorCode errorCode,
        string message,
        string? diagnosticId = null) =>
        new(false, default, errorCode, message, diagnosticId);
}

public sealed record SurveyObservationCommitResult(
    SurveyProjectSnapshot Snapshot,
    SurveyObservation Observation,
    SurveyMapLayer Layer,
    bool WasAlreadyCommitted);

public sealed record SurveyLayerOperationItem(
    Guid LayerId,
    bool Succeeded,
    string? Message = null,
    SurveyLayerTransform? Transform = null);

public sealed record SurveyLayerOperationResult(
    SurveyProjectSnapshot Snapshot,
    IReadOnlyList<SurveyLayerOperationItem> Items);
