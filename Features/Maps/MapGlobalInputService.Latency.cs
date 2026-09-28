using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapGlobalInputService
{
    private readonly InputHookLatency _keyboardLatency = new();
    private readonly InputHookLatency _mouseLatency = new();
    private int _latencyReportActive;
    private long _lastLatencyReport = Stopwatch.GetTimestamp();
    private TimeSpan _lastGcPause = GC.GetTotalPauseDuration();
    private double? _lastProcessCpuMs;
    private long _lastCaptureCallbacks;
    private long _lastCaptureReadbacks;
    private long _uiProbeQueuedAt;
    private long _lastUiProbeDelayTicks;

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
            using var process = Process.GetCurrentProcess();
            var cpuMs = process.TotalProcessorTime.TotalMilliseconds;
            var cpuDelta = _lastProcessCpuMs is { } previousCpu ? cpuMs - previousCpu : (double?)null;
            _lastProcessCpuMs = cpuMs;
            var callbacks = Interlocked.Read(ref CaptureStreamDiagnostics.FrameCallbacks);
            var readbacks = Interlocked.Read(ref CaptureStreamDiagnostics.Readbacks);
            var callbackDelta = callbacks - _lastCaptureCallbacks;
            var readbackDelta = readbacks - _lastCaptureReadbacks;
            _lastCaptureCallbacks = callbacks;
            _lastCaptureReadbacks = readbacks;
            if (Interlocked.CompareExchange(ref _uiProbeQueuedAt, now, 0) == 0)
            {
                if (!_dispatcher.TryEnqueue(() =>
                {
                    var queuedAt = Interlocked.Read(ref _uiProbeQueuedAt);
                    Interlocked.Exchange(ref _lastUiProbeDelayTicks, Stopwatch.GetTimestamp() - queuedAt);
                    Interlocked.Exchange(ref _uiProbeQueuedAt, 0);
                })) Interlocked.Exchange(ref _uiProbeQueuedAt, 0);
            }
            var pendingProbe = Interlocked.Read(ref _uiProbeQueuedAt);
            MapLogCollector.Instance.Append(MapLogCategory.System, MapLogLevel.Info,
                "Input hook latency window",
                details: new()
                {
                    ["intervalMs"] = interval,
                    ["processId"] = Environment.ProcessId,
                    ["processCpuMs"] = cpuDelta,
                    ["cpuCoreEquivalent"] = cpuDelta / Math.Max(1, interval),
                    ["processThreadCount"] = process.Threads.Count,
                    ["uiProbeLastDelayMs"] = Interlocked.Read(ref _lastUiProbeDelayTicks) * 1000d / Stopwatch.Frequency,
                    ["uiProbePendingMs"] = pendingProbe == 0 ? 0 : Stopwatch.GetElapsedTime(pendingProbe).TotalMilliseconds,
                    ["wgcActiveSessions"] = Interlocked.Read(ref CaptureStreamDiagnostics.ActiveSessions),
                    ["wgcFrameCallbacks"] = callbackDelta,
                    ["wgcReadbacks"] = readbackDelta,
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
