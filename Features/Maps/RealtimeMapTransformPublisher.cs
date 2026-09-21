using System.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Minimal high-frequency map state. It intentionally carries no recognition,
/// alignment-session, cache, or lease object.
/// </summary>
internal readonly record struct RealtimeTransformState(
    double Scale,
    double Tx,
    double Ty,
    long Timestamp,
    double Confidence,
    long Generation);

internal readonly record struct RealtimeTransformTelemetry(
    long PublishedTransforms,
    long AppliedTransforms,
    long CoalescedTransforms,
    long DroppedTransforms,
    double AverageDispatcherQueueMs,
    double P95DispatcherQueueMs,
    double AverageRenderMs,
    double P95RenderMs,
    double AverageEndToEndMs,
    double P95EndToEndMs);

/// <summary>
/// Latest-only dispatcher bridge for realtime transforms. Publishing is
/// allocation-free after construction and never mutates business session state.
/// </summary>
internal sealed class RealtimeMapTransformPublisher
{
    private const int TimingCapacity = 256;
    private readonly object _gate = new();
    private readonly Func<Action, bool> _schedule;
    private readonly Func<RealtimeTransformState, bool> _apply;
    private readonly Action _drainAction;
    private readonly double[] _queueSamples = new double[TimingCapacity];
    private readonly double[] _renderSamples = new double[TimingCapacity];
    private readonly double[] _endToEndSamples = new double[TimingCapacity];
    private RealtimeTransformState _pending;
    private long _pendingQueuedAt;
    private bool _hasPending;
    private bool _dispatchScheduled;
    private long _published;
    private long _applied;
    private long _coalesced;
    private long _dropped;
    private int _timingCount;
    private int _timingCursor;

    internal RealtimeMapTransformPublisher(
        Func<Action, bool> schedule,
        Func<RealtimeTransformState, bool> apply)
    {
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _drainAction = Drain;
    }

    internal void Publish(RealtimeTransformState state)
    {
        var schedule = false;
        lock (_gate)
        {
            _published++;
            if (_hasPending)
                _coalesced++;
            _pending = state;
            _pendingQueuedAt = Stopwatch.GetTimestamp();
            _hasPending = true;
            if (!_dispatchScheduled)
            {
                _dispatchScheduled = true;
                schedule = true;
            }
        }

        if (schedule && !_schedule(_drainAction))
        {
            lock (_gate)
            {
                _dispatchScheduled = false;
                if (_hasPending)
                {
                    _hasPending = false;
                    _dropped++;
                }
            }
        }
    }

    internal RealtimeTransformTelemetry Snapshot(bool reset)
    {
        lock (_gate)
        {
            var queue = CopySamples(_queueSamples, _timingCount);
            var render = CopySamples(_renderSamples, _timingCount);
            var endToEnd = CopySamples(_endToEndSamples, _timingCount);
            var result = new RealtimeTransformTelemetry(
                _published,
                _applied,
                _coalesced,
                _dropped,
                Average(queue),
                Percentile95(queue),
                Average(render),
                Percentile95(render),
                Average(endToEnd),
                Percentile95(endToEnd));
            if (reset)
            {
                _published = 0;
                _applied = 0;
                _coalesced = 0;
                _dropped = 0;
                _timingCount = 0;
                _timingCursor = 0;
            }
            return result;
        }
    }

    internal void DiscardPending()
    {
        lock (_gate)
        {
            if (_hasPending)
            {
                _hasPending = false;
                _dropped++;
            }
        }
    }

    private void Drain()
    {
        RealtimeTransformState state;
        long queuedAt;
        lock (_gate)
        {
            if (!_hasPending)
            {
                _dispatchScheduled = false;
                return;
            }
            state = _pending;
            queuedAt = _pendingQueuedAt;
            _hasPending = false;
        }

        var started = Stopwatch.GetTimestamp();
        var applied = false;
        try
        {
            applied = _apply(state);
        }
        finally
        {
            var completed = Stopwatch.GetTimestamp();
            var scheduleAgain = false;
            lock (_gate)
            {
                if (applied)
                {
                    _applied++;
                    AddTimingSample(
                        Stopwatch.GetElapsedTime(queuedAt, started).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds,
                        GetSourceAgeMilliseconds(state.Timestamp));
                }
                else
                {
                    _dropped++;
                }

                _dispatchScheduled = false;
                if (_hasPending)
                {
                    _dispatchScheduled = true;
                    scheduleAgain = true;
                }
            }

            if (scheduleAgain && !_schedule(_drainAction))
            {
                lock (_gate)
                {
                    _dispatchScheduled = false;
                    if (_hasPending)
                    {
                        _hasPending = false;
                        _dropped++;
                    }
                }
            }
        }
    }

    private void AddTimingSample(double queueMs, double renderMs, double endToEndMs)
    {
        _queueSamples[_timingCursor] = queueMs;
        _renderSamples[_timingCursor] = renderMs;
        _endToEndSamples[_timingCursor] = endToEndMs;
        _timingCursor = (_timingCursor + 1) % TimingCapacity;
        _timingCount = Math.Min(TimingCapacity, _timingCount + 1);
    }

    private static double[] CopySamples(double[] source, int count)
    {
        if (count <= 0)
            return [];
        var copy = new double[count];
        Array.Copy(source, copy, count);
        return copy;
    }

    private static double Average(double[] samples) =>
        samples.Length == 0 ? 0d : samples.Average();

    private static double Percentile95(double[] samples)
    {
        if (samples.Length == 0)
            return 0d;
        Array.Sort(samples);
        return samples[(int)Math.Ceiling(samples.Length * 0.95d) - 1];
    }

    private static double GetSourceAgeMilliseconds(long sourceTimestamp)
    {
        if (sourceTimestamp <= 0)
            return 0d;
        var now = (long)(Stopwatch.GetTimestamp()
            * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
        return Math.Max(0d,
            (now - sourceTimestamp) / (double)TimeSpan.TicksPerMillisecond);
    }
}
