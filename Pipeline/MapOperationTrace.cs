using System.Diagnostics;

namespace IDVBuff.Pipeline;

/// <summary>Operation kinds which receive a closed wall-clock trace.</summary>
public static class MapOperationTypes
{
    public const string QuickScan = "quick-scan";
    public const string BackgroundScan = "background-scan";
    public const string MapOpenAlignment = "map-open-alignment";
    public const string CandidateConfirmation = "candidate-confirmation";
    public const string FloorAlignment = "floor-alignment";
}

public enum MapOperationSpanStatus
{
    Completed,
    Failed,
    Cancelled,
    Superseded,
    Skipped
}

public enum MapOperationWaitKind
{
    Timer,
    Capture,
    Queue,
    User,
    Io,
    Compute
}

/// <summary>
/// A monotonic clock seam keeps the trace deterministic in unit tests without
/// changing the production timing source.
/// </summary>
public interface IMapOperationClock
{
    long Timestamp { get; }
    long Frequency { get; }
}

public sealed class StopwatchMapOperationClock : IMapOperationClock
{
    public long Timestamp => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
}

/// <summary>
/// Ambient operation trace used by lower-level map services. AsyncLocal keeps
/// the trace flowing through Task.Run without adding a trace parameter to
/// every legacy service contract.
/// </summary>
public static class MapOperationTraceAmbient
{
    private static readonly AsyncLocal<MapOperationTrace?> CurrentTrace = new();

    public static MapOperationTrace? Current => CurrentTrace.Value;

    public static MapOperationTrace.MapOperationSpanScope StartChild(
        string name,
        MapOperationWaitKind waitKind = MapOperationWaitKind.Compute,
        string? route = null,
        string? mapId = null,
        string? floorKey = null,
        int? attemptIndex = null)
    {
        return CurrentTrace.Value?.StartChild(
            name,
            waitKind,
            route: route,
            mapId: mapId,
            floorKey: floorKey,
            attemptIndex: attemptIndex)
            ?? MapOperationTrace.MapOperationSpanScope.Noop;
    }

    public static MapOperationTrace.MapOperationSpanScope StartTopLevel(
        string name,
        MapOperationWaitKind waitKind = MapOperationWaitKind.Compute,
        string? route = null,
        string? mapId = null,
        string? floorKey = null,
        int? attemptIndex = null)
    {
        return CurrentTrace.Value?.StartTopLevel(
            name,
            waitKind,
            route: route,
            mapId: mapId,
            floorKey: floorKey,
            attemptIndex: attemptIndex)
            ?? MapOperationTrace.MapOperationSpanScope.Noop;
    }

    public static void SetCurrent(MapOperationTrace? trace) =>
        CurrentTrace.Value = trace;
}

/// <summary>
/// Lightweight operation trace. Top-level spans are explicitly non-overlapping;
/// child spans can nest and are explanatory only.
/// </summary>
public sealed partial class MapOperationTrace
{
    private readonly object _gate = new();
    private readonly IMapOperationClock _clock;
    private readonly long _startTimestamp;
    private readonly long _frequency;
    private readonly IReadOnlyList<string> _declaredTopLevelPhases;
    private readonly Dictionary<string, ActiveSpan> _active = new(StringComparer.Ordinal);
    private readonly List<MapOperationSpan> _spans = [];
    private readonly AsyncLocal<string?> _currentSpanId = new();
    private bool _finalized;
    private bool _overlapDetected;
    private MapOperationTraceSummary? _summary;
    private string? _terminalOutcome;
    private string? _terminalReason;
    private string? _route;
    private string? _mapId;
    private string? _floorKey;
    private int? _attemptIndex;

    public MapOperationTrace(
        string operationType,
        IEnumerable<string>? topLevelPhases = null,
        IMapOperationClock? clock = null,
        string? route = null,
        string? mapId = null,
        string? floorKey = null,
        int? attemptIndex = null)
    {
        if (string.IsNullOrWhiteSpace(operationType))
            throw new ArgumentException("An operation type is required.", nameof(operationType));

        OperationType = operationType;
        OperationId = Guid.NewGuid().ToString("N");
        _clock = clock ?? new StopwatchMapOperationClock();
        _startTimestamp = _clock.Timestamp;
        _frequency = Math.Max(1L, _clock.Frequency);
        _declaredTopLevelPhases = (topLevelPhases ?? Array.Empty<string>())
            .Where(static phase => !string.IsNullOrWhiteSpace(phase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _route = route;
        _mapId = mapId;
        _floorKey = floorKey;
        _attemptIndex = attemptIndex;
    }

    public string OperationId { get; }
    public string OperationType { get; }
    public bool IsFinalized => Volatile.Read(ref _finalized);
    public MapOperationTraceSummary? Summary => Volatile.Read(ref _summary);

    public void SetTerminal(string outcome, string terminalReason)
    {
        lock (_gate)
        {
            if (_finalized)
                return;
            _terminalOutcome = outcome;
            _terminalReason = terminalReason;
        }
    }

    public void SetContext(
        string? route = null,
        string? mapId = null,
        string? floorKey = null,
        int? attemptIndex = null)
    {
        lock (_gate)
        {
            if (_finalized)
                return;
            if (route is not null)
                _route = route;
            if (mapId is not null)
                _mapId = mapId;
            if (floorKey is not null)
                _floorKey = floorKey;
            if (attemptIndex is not null)
                _attemptIndex = attemptIndex;
        }
    }

    public MapOperationSpanScope StartTopLevel(
        string name,
        MapOperationWaitKind waitKind = MapOperationWaitKind.Compute,
        string? route = null,
        string? mapId = null,
        string? floorKey = null,
        int? attemptIndex = null)
    {
        return StartSpan(
            name,
            isTopLevel: true,
            waitKind,
            parentSpanId: null,
            route,
            mapId,
            floorKey,
            attemptIndex);
    }

    public MapOperationSpanScope StartChild(
        string name,
        MapOperationWaitKind waitKind = MapOperationWaitKind.Compute,
        string? parentSpanId = null,
        string? route = null,
        string? mapId = null,
        string? floorKey = null,
        int? attemptIndex = null)
    {
        return StartSpan(
            name,
            isTopLevel: false,
            waitKind,
            parentSpanId ?? _currentSpanId.Value,
            route,
            mapId,
            floorKey,
            attemptIndex);
    }

    private MapOperationSpanScope StartSpan(
        string name,
        bool isTopLevel,
        MapOperationWaitKind waitKind,
        string? parentSpanId,
        string? route,
        string? mapId,
        string? floorKey,
        int? attemptIndex)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A span name is required.", nameof(name));

        var spanId = Guid.NewGuid().ToString("N");
        var timestamp = _clock.Timestamp;
        lock (_gate)
        {
            if (_finalized)
                return MapOperationSpanScope.Noop;

            if (isTopLevel && _active.Values.Any(static active => active.Span.IsTopLevel))
                _overlapDetected = true;

            var parent = !isTopLevel
                && parentSpanId is not null
                && _active.TryGetValue(parentSpanId, out var parentActive)
                    ? parentActive.Span
                    : null;

            var span = new MapOperationSpan
            {
                OperationId = OperationId,
                SpanId = spanId,
                ParentSpanId = isTopLevel ? null : parentSpanId,
                Name = name,
                IsTopLevel = isTopLevel,
                StartOffsetMs = ToMilliseconds(timestamp - _startTimestamp),
                DurationMs = 0d,
                Status = MapOperationSpanStatus.Completed,
                Route = route ?? parent?.Route ?? _route,
                MapId = mapId ?? parent?.MapId ?? _mapId,
                FloorKey = floorKey ?? parent?.FloorKey ?? _floorKey,
                AttemptIndex = attemptIndex ?? parent?.AttemptIndex ?? _attemptIndex,
                WaitKind = waitKind
            };
            _active.Add(spanId, new ActiveSpan(span, timestamp));
            var previousSpanId = _currentSpanId.Value;
            _currentSpanId.Value = spanId;
            return new MapOperationSpanScope(this, spanId, previousSpanId);
        }
    }

    internal void CompleteSpan(
        string spanId,
        MapOperationSpanStatus status,
        string? terminalReason)
    {
        var timestamp = _clock.Timestamp;
        lock (_gate)
        {
            if (!_active.Remove(spanId, out var active))
                return;

            active.Span.DurationMs = Math.Max(
                0d,
                ToMilliseconds(timestamp - active.StartTimestamp));
            active.Span.Status = status;
            active.Span.TerminalReason = terminalReason;
            _spans.Add(active.Span);
        }
    }
}

public sealed class MapOperationSpan
{
    public string OperationId { get; init; } = string.Empty;
    public string SpanId { get; init; } = string.Empty;
    public string? ParentSpanId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsTopLevel { get; init; }
    public double StartOffsetMs { get; internal set; }
    public double DurationMs { get; internal set; }
    public MapOperationSpanStatus Status { get; internal set; }
    public string? Route { get; internal set; }
    public string? MapId { get; internal set; }
    public string? FloorKey { get; internal set; }
    public int? AttemptIndex { get; internal set; }
    public MapOperationWaitKind WaitKind { get; init; }
    public string? TerminalReason { get; internal set; }
}

public sealed class MapOperationTraceSummary
{
    public string OperationId { get; init; } = string.Empty;
    public string OperationType { get; init; } = string.Empty;
    public string? Route { get; init; }
    public string? MapId { get; init; }
    public string? FloorKey { get; init; }
    public int? AttemptIndex { get; init; }
    public double WallClockMs { get; init; }
    public double CoveredTopLevelMs { get; init; }
    public double UnaccountedMs { get; init; }
    public double UnaccountedRatio { get; init; }
    public double OverlapMs { get; init; }
    public bool HasTopLevelOverlap { get; init; }
    public string? LongestSpanName { get; init; }
    public double LongestSpanMs { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string TerminalReason { get; init; } = string.Empty;
    public double UnaccountedThresholdMs { get; init; }
    public bool ShouldWarnUnaccounted { get; init; }
    public IReadOnlyList<MapOperationSpan> Spans { get; init; } = Array.Empty<MapOperationSpan>();

    public IReadOnlyDictionary<string, double> ToPhaseTimings()
    {
        var timings = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["wall_clock"] = WallClockMs,
            ["covered_top_level"] = CoveredTopLevelMs,
            ["unaccounted"] = UnaccountedMs,
            ["unaccounted_ratio"] = UnaccountedRatio,
            ["overlap"] = OverlapMs
        };
        foreach (var span in Spans.Where(static span => span.IsTopLevel
                     && span.Status != MapOperationSpanStatus.Skipped))
            timings[span.Name] = timings.GetValueOrDefault(span.Name) + span.DurationMs;
        return timings;
    }

    public IReadOnlyDictionary<string, string> ToPhaseStatuses()
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in Spans
                     .Where(static span => span.IsTopLevel)
                     .GroupBy(static span => span.Name, StringComparer.Ordinal))
        {
            var status = group.Any(static span => span.Status == MapOperationSpanStatus.Failed)
                ? MapOperationSpanStatus.Failed
                : group.Any(static span => span.Status == MapOperationSpanStatus.Cancelled)
                    ? MapOperationSpanStatus.Cancelled
                    : group.Any(static span => span.Status == MapOperationSpanStatus.Superseded)
                        ? MapOperationSpanStatus.Superseded
                        : group.All(static span => span.Status == MapOperationSpanStatus.Skipped)
                            ? MapOperationSpanStatus.Skipped
                            : MapOperationSpanStatus.Completed;
            statuses[group.Key] = status.ToString().ToLowerInvariant();
        }
        return statuses;
    }

    public double GetTopLevelDurationMs(string name) =>
        Spans
            .Where(span => span.IsTopLevel && string.Equals(
                span.Name,
                name,
                StringComparison.Ordinal))
            .Sum(static span => span.DurationMs);

    public double GetChildDurationMs(string name) =>
        Spans
            .Where(span => !span.IsTopLevel && string.Equals(
                span.Name,
                name,
                StringComparison.Ordinal))
            .Sum(static span => span.DurationMs);

    public Dictionary<string, object?> ToDetails()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operationId"] = OperationId,
            ["operationType"] = OperationType,
            ["route"] = Route,
            ["mapId"] = MapId,
            ["floorKey"] = FloorKey,
            ["attemptIndex"] = AttemptIndex,
            ["wallClockMs"] = WallClockMs,
            ["coveredTopLevelMs"] = CoveredTopLevelMs,
            ["unaccountedMs"] = UnaccountedMs,
            ["unaccountedRatio"] = UnaccountedRatio,
            ["overlapMs"] = OverlapMs,
            ["phaseStatuses"] = ToPhaseStatuses(),
            ["longestSpan"] = LongestSpanName,
            ["longestSpanMs"] = LongestSpanMs,
            ["outcome"] = Outcome,
            ["terminalReason"] = TerminalReason,
            ["unaccountedThresholdMs"] = UnaccountedThresholdMs,
            ["shouldWarnUnaccounted"] = ShouldWarnUnaccounted,
            // Spans are the canonical persisted timeline. Persisting the same
            // operation again as a large formatted string doubled log volume
            // and amplified the one-second diagnostic flush pause.
            ["spans"] = Spans.Select(static span => new Dictionary<string, object?>
            {
                ["operationId"] = span.OperationId,
                ["spanId"] = span.SpanId,
                ["parentSpanId"] = span.ParentSpanId,
                ["name"] = span.Name,
                ["topLevel"] = span.IsTopLevel,
                ["startOffsetMs"] = span.StartOffsetMs,
                ["durationMs"] = span.DurationMs,
                ["status"] = span.Status.ToString().ToLowerInvariant(),
                ["route"] = span.Route,
                ["mapId"] = span.MapId,
                ["floorKey"] = span.FloorKey,
                ["attemptIndex"] = span.AttemptIndex,
                ["waitKind"] = span.WaitKind.ToString().ToLowerInvariant(),
                ["terminalReason"] = span.TerminalReason
            }).ToArray()
        };
    }

    public string ToHumanTimeline()
    {
        var lines = new List<string>
        {
            $"[op={OperationId}][+0000.0ms] {OperationType} start"
        };
        var childrenByParent = Spans
            .Where(static span => !span.IsTopLevel && span.ParentSpanId is not null)
            .GroupBy(static span => span.ParentSpanId!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static span => span.StartOffsetMs).ToArray(),
                StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        void AppendSpan(MapOperationSpan span, string indent)
        {
            lines.Add(
                indent
                + $"[+{span.StartOffsetMs:0000.0}ms][{span.DurationMs,6:0.0}ms] "
                + $"{span.Name} [{span.Status.ToString().ToLowerInvariant()}]"
                + (span.WaitKind is MapOperationWaitKind.User
                    ? " [user-wait]"
                    : string.Empty));
            emitted.Add(span.SpanId);
            if (childrenByParent.TryGetValue(span.SpanId, out var children))
            {
                foreach (var child in children)
                    AppendSpan(child, indent + "  ├─ ");
            }
        }

        foreach (var span in Spans.Where(static span => span.IsTopLevel))
            AppendSpan(span, string.Empty);

        foreach (var span in Spans.Where(static span => !span.IsTopLevel)
                     .Where(span => !emitted.Contains(span.SpanId)))
        {
            AppendSpan(span, "  └─ ");
        }
        lines.Add(
            $"[+{WallClockMs:0000.0}ms] 完成 wall={WallClockMs:0.0}ms "
            + $"covered={CoveredTopLevelMs:0.0}ms unaccounted={UnaccountedMs:0.0}ms");
        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class ActiveSpan(MapOperationSpan span, long startTimestamp)
{
    public MapOperationSpan Span { get; } = span;
    public long StartTimestamp { get; } = startTimestamp;
}
