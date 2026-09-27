using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapGlobalInputService
{
    private readonly InputHookLatency _keyboardLatency = new();
    private readonly InputHookLatency _mouseLatency = new();
    private int _latencyReportActive;
    private long _lastLatencyReport = Stopwatch.GetTimestamp();
    private TimeSpan _lastGcPause = GC.GetTotalPauseDuration();

    private void ReportInputHookLatency()
    {
        // A stalled disk must not accumulate reporter callbacks or block hooks.
        if (Interlocked.Exchange(ref _latencyReportActive, 1) != 0) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            var pause = GC.GetTotalPauseDuration();
            var keyboard = _keyboardLatency.Take();
            var mouse = _mouseLatency.Take();
            var interval = Stopwatch.GetElapsedTime(_lastLatencyReport, now).TotalMilliseconds;
            var gcPause = (pause - _lastGcPause).TotalMilliseconds;
            _lastLatencyReport = now;
            _lastGcPause = pause;
            MapLogCollector.Instance.Append(MapLogCategory.System, MapLogLevel.Info,
                "Input hook latency window",
                details: new()
                {
                    ["intervalMs"] = interval,
                    ["keyboardCount"] = keyboard.Count,
                    ["keyboardArrivalMaxMs"] = keyboard.MaximumArrivalMilliseconds,
                    ["keyboardArrival50msCount"] = keyboard.Delayed50Milliseconds,
                    ["keyboardCallbackMaxMs"] = keyboard.MaximumCallbackMilliseconds,
                    ["mouseCount"] = mouse.Count,
                    ["mouseArrivalMaxMs"] = mouse.MaximumArrivalMilliseconds,
                    ["mouseArrival50msCount"] = mouse.Delayed50Milliseconds,
                    ["mouseCallbackMaxMs"] = mouse.MaximumCallbackMilliseconds,
                    ["gcPauseMs"] = gcPause,
                    ["workingSetMb"] = Environment.WorkingSet / 1048576d,
                    ["managedHeapMb"] = GC.GetTotalMemory(false) / 1048576d,
                    ["poolPending"] = ThreadPool.PendingWorkItemCount
                });
        }
        catch { /* Diagnostics must not terminate input monitoring. */ }
        finally { Volatile.Write(ref _latencyReportActive, 0); }
    }
}
