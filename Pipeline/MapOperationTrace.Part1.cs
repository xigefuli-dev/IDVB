using System.Diagnostics;

namespace IDVBuff.Pipeline;
/// <summary>
/// Lightweight operation trace. Top-level spans are explicitly non-overlapping;
/// child spans can nest and are explanatory only.
/// </summary>
public sealed partial class MapOperationTrace
{

    public MapOperationTraceSummary Complete(
        string outcome,
        string terminalReason,
        string? route = null,
        string? mapId = null,
        string? floorKey = null,
        int? attemptIndex = null)
    {
        lock (_gate)
        {
            if (_summary is not null)
                return _summary;

            SetContext(route, mapId, floorKey, attemptIndex);
            var timestamp = _clock.Timestamp;
            var effectiveOutcome = _terminalOutcome ?? outcome;
            var effectiveTerminalReason = _terminalReason ?? terminalReason;
            var activeStatus = StatusForOutcome(effectiveOutcome);
            foreach (var active in _active.Values.ToArray())
            {
                active.Span.DurationMs = Math.Max(
                    0d,
                    ToMilliseconds(timestamp - active.StartTimestamp));
                active.Span.Status = activeStatus;
                active.Span.TerminalReason = effectiveTerminalReason;
                _spans.Add(active.Span);
            }
            _active.Clear();
            _currentSpanId.Value = null;

            var elapsed = Math.Max(0d, ToMilliseconds(timestamp - _startTimestamp));
            var presentTopLevel = _spans
                .Where(static span => span.IsTopLevel)
                .Select(static span => span.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var phase in _declaredTopLevelPhases)
            {
                if (presentTopLevel.Contains(phase))
                    continue;
                _spans.Add(new MapOperationSpan
                {
                    OperationId = OperationId,
                    SpanId = Guid.NewGuid().ToString("N"),
                    Name = phase,
                    IsTopLevel = true,
                    StartOffsetMs = elapsed,
                    DurationMs = 0d,
                    Status = MapOperationSpanStatus.Skipped,
                    Route = _route,
                    MapId = _mapId,
                    FloorKey = _floorKey,
                    AttemptIndex = _attemptIndex,
                    WaitKind = MapOperationWaitKind.Compute,
                    TerminalReason = "not-executed"
                });
            }

            var orderedSpans = _spans
                .OrderBy(static span => span.StartOffsetMs)
                .ThenBy(static span => span.IsTopLevel ? 0 : 1)
                .ToArray();
            var topLevel = orderedSpans
                .Where(static span => span.IsTopLevel && span.Status != MapOperationSpanStatus.Skipped)
                .ToArray();
            var covered = topLevel.Sum(static span => span.DurationMs);
            var overlapMs = CalculateOverlap(topLevel);
            var unaccounted = elapsed - covered;
            var longest = orderedSpans
                .OrderByDescending(static span => span.DurationMs)
                .FirstOrDefault();
            var ratio = elapsed <= 0d ? 0d : unaccounted / elapsed;
            var threshold = Math.Max(2d, elapsed * 0.01d);

            var summary = new MapOperationTraceSummary
            {
                OperationId = OperationId,
                OperationType = OperationType,
                Route = _route,
                MapId = _mapId,
                FloorKey = _floorKey,
                AttemptIndex = _attemptIndex,
                WallClockMs = elapsed,
                CoveredTopLevelMs = covered,
                UnaccountedMs = unaccounted,
                UnaccountedRatio = ratio,
                OverlapMs = overlapMs,
                HasTopLevelOverlap = _overlapDetected || overlapMs > 0.001d,
                LongestSpanName = longest?.Name,
                LongestSpanMs = longest?.DurationMs ?? 0d,
                Outcome = effectiveOutcome,
                TerminalReason = effectiveTerminalReason,
                UnaccountedThresholdMs = threshold,
                ShouldWarnUnaccounted = unaccounted > threshold,
                Spans = orderedSpans
            };
            _summary = summary;
            _finalized = true;
            return summary;
        }
    }

    private static MapOperationSpanStatus StatusForOutcome(string outcome) =>
        outcome switch
        {
            "cancelled" => MapOperationSpanStatus.Cancelled,
            "superseded" => MapOperationSpanStatus.Superseded,
            "failed" => MapOperationSpanStatus.Failed,
            _ => MapOperationSpanStatus.Completed
        };

    private double ToMilliseconds(long ticks) => ticks * 1000d / _frequency;

    private static double CalculateOverlap(IReadOnlyList<MapOperationSpan> spans)
    {
        var overlap = 0d;
        for (var i = 0; i < spans.Count; i++)
        {
            var leftEnd = spans[i].StartOffsetMs + spans[i].DurationMs;
            for (var j = i + 1; j < spans.Count; j++)
            {
                var rightEnd = spans[j].StartOffsetMs + spans[j].DurationMs;
                var start = Math.Max(spans[i].StartOffsetMs, spans[j].StartOffsetMs);
                var end = Math.Min(leftEnd, rightEnd);
                if (end > start)
                    overlap += end - start;
            }
        }
        return overlap;
    }

    public sealed class MapOperationSpanScope : IDisposable
    {
        private readonly MapOperationTrace? _trace;
        private readonly string? _spanId;
        private readonly string? _previousSpanId;
        private int _completed;

        private MapOperationSpanScope()
        {
        }

        internal MapOperationSpanScope(
            MapOperationTrace trace,
            string spanId,
            string? previousSpanId)
        {
            _trace = trace;
            _spanId = spanId;
            _previousSpanId = previousSpanId;
        }

        public static MapOperationSpanScope Noop { get; } = new();

        public string? SpanId => _spanId;

        public void Complete(
            MapOperationSpanStatus status = MapOperationSpanStatus.Completed,
            string? terminalReason = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0
                && _trace is not null
                && _spanId is not null)
            {
                _trace.CompleteSpan(_spanId, status, terminalReason);
            }

            // A queue span is commonly completed once on the worker thread
            // and once in the awaiting caller's finally block. AsyncLocal is
            // copied, not shared, across Task.Run, so the second call must
            // still restore the caller's previous parent when its local
            // context still points at this span.
            if (_trace is not null
                && _spanId is not null
                && string.Equals(
                    _trace._currentSpanId.Value,
                    _spanId,
                    StringComparison.Ordinal))
            {
                _trace._currentSpanId.Value = _previousSpanId;
            }
        }

        public void Dispose() => Complete();
    }
}
