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

public enum MapAlignmentFloorSource
{
    ManualOverride,
    DisplayedMiniMap
}

public readonly record struct MapAlignmentFloorSelection(
    string FloorKey,
    MapAlignmentFloorSource Source);

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
        MapFloorRecognitionIntent intent) => intent switch
        {
            MapFloorRecognitionIntent.AutomaticMapOpen => true,
            MapFloorRecognitionIntent.QuickScan => true,
            MapFloorRecognitionIntent.ManualRecognition => true,
            _ => true
        };

    public static MapAlignmentFloorSelection? ResolvePreferredAlignmentFloor(
        MapRecord map,
        string? manualFloorKey,
        string? displayedFloorKey)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (IsKnownFloor(map, manualFloorKey))
        {
            return new MapAlignmentFloorSelection(
                manualFloorKey!,
                MapAlignmentFloorSource.ManualOverride);
        }
        if (IsKnownFloor(map, displayedFloorKey))
        {
            return new MapAlignmentFloorSelection(
                displayedFloorKey!,
                MapAlignmentFloorSource.DisplayedMiniMap);
        }
        return null;
    }

    public static bool MayFallbackToAutomaticFloor(
        MapAlignmentFloorSource source) =>
        source == MapAlignmentFloorSource.DisplayedMiniMap;

    public static int GetOperationPriority(
        MapFloorRecognitionIntent intent) =>
        intent switch
        {
            MapFloorRecognitionIntent.ManualRecognition => 2,
            MapFloorRecognitionIntent.QuickScan => 1,
            _ => 0
        };

    private static bool IsKnownFloor(MapRecord map, string? floorKey) =>
        !string.IsNullOrWhiteSpace(floorKey)
        && MapFloorRules.GetFloorProfile(map, floorKey) is not null;
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
/*
 * 文件职责：MapFloorRecognitionModels。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
