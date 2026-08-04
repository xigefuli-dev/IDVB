namespace IDVBuff.Features.Maps;

public sealed class MapFloorRecognitionResult
{
    public bool Succeeded { get; init; }
    public string? Floor { get; init; }
    public double Confidence { get; init; }
    public double LocalizationConfidence { get; init; }
    public NormalizedRectangle? LocalizedRegion { get; init; }
    public double CaptureMilliseconds { get; init; }
    public double AnalysisMilliseconds { get; init; }
    /// <summary>Wall-clock from original input timestamp to result produced.
    /// Legacy — includes animation delay and stability wait overhead.</summary>
    public double EndToEndMilliseconds { get; init; }
    public int AttemptCount { get; init; }
    public string FailureReason { get; init; } = string.Empty;

    // Phase 0: mutually-exclusive floor timing
    public double QueueMilliseconds { get; init; }
    public double WorkerMilliseconds { get; init; }
    public double RequestMilliseconds { get; init; }
    public double InputToResultMilliseconds { get; init; }
    public double RetryWaitMilliseconds { get; init; }
    public double WorkerOverheadMilliseconds { get; init; }
}

public enum MapFloorRoute
{
    Reject,
    FirstFloorAlignment,
    SecondFloorAlignment
}

public enum MapFloorRecognitionIntent
{
    AutomaticMapOpen,
    QuickScan,
    ManualRecognition
}

public static class MapFloorRecognitionRules
{
    public const double PerformanceBudgetMilliseconds = 100d;
    public const double ConfirmationSampleIntervalMilliseconds = 16d;

    public static bool IsPublishableSuccess(MapFloorRecognitionResult result) =>
        result.Succeeded
        && result.Floor is not null
        && double.IsFinite(result.EndToEndMilliseconds)
        && result.EndToEndMilliseconds >= 0d;

    public static bool IsWithinPerformanceBudget(
        MapFloorRecognitionResult result) =>
        IsPublishableSuccess(result)
        && result.EndToEndMilliseconds <= PerformanceBudgetMilliseconds;

    public static MapFloorRoute GetRoute(MapFloorRecognitionResult result)
    {
        if (!IsPublishableSuccess(result))
            return MapFloorRoute.Reject;
        return result.Floor switch
        {
            "1f" => MapFloorRoute.FirstFloorAlignment,
            "2f" => MapFloorRoute.SecondFloorAlignment,
            _ => MapFloorRoute.Reject
        };
    }

    public static bool RequiresConfirmedFirstFloor(
        MapFloorRecognitionIntent intent) =>
        intent == MapFloorRecognitionIntent.AutomaticMapOpen;

    public static int GetOperationPriority(
        MapFloorRecognitionIntent intent) =>
        intent switch
        {
            MapFloorRecognitionIntent.ManualRecognition => 2,
            MapFloorRecognitionIntent.QuickScan => 1,
            _ => 0
        };
}

/// <summary>
/// Rejects a single transient frame before a floor result is published.
/// 2F is deliberately more conservative because it disables 1F rendering.
/// </summary>
public sealed class MapFloorStabilityTracker
{
    public const int FirstFloorConfirmationFrames = 2;
    public const int SecondFloorConfirmationFrames = 3;

    private string? _candidate;
    private int _consecutiveMatches;
    private long _lastObservationTimestamp;

    public bool Observe(string floor) =>
        Observe(floor, _lastObservationTimestamp + 1L, 0L);

    public bool Observe(
        string floor,
        long observationTimestamp,
        long minimumIntervalTicks,
        int firstFloorConfirmationFrames = FirstFloorConfirmationFrames,
        int secondFloorConfirmationFrames = SecondFloorConfirmationFrames)
    {
        minimumIntervalTicks = Math.Max(0L, minimumIntervalTicks);
        if (_candidate != floor
            || observationTimestamp < _lastObservationTimestamp)
        {
            _candidate = floor;
            _consecutiveMatches = 0;
            _lastObservationTimestamp = 0L;
        }
        if (_consecutiveMatches > 0
            && observationTimestamp - _lastObservationTimestamp
                < minimumIntervalTicks)
        {
            return false;
        }

        _consecutiveMatches++;
        _lastObservationTimestamp = observationTimestamp;
        var requiredFrames = floor == "2f"
            ? Math.Max(1, secondFloorConfirmationFrames)
            : Math.Max(1, firstFloorConfirmationFrames);
        return _consecutiveMatches >= requiredFrames;
    }

    public void Reset()
    {
        _candidate = null;
        _consecutiveMatches = 0;
        _lastObservationTimestamp = 0L;
    }
}
