using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void ReportScanCaptureFailure(
        string? failureReason,
        Stopwatch wallClock)
    {
        var elapsedMs = wallClock.Elapsed.TotalMilliseconds;
        _lastScanPhaseTimings = new Dictionary<string, double>
        {
            ["capture"] = elapsedMs,
            ["wall_clock"] = elapsedMs
        };
        _statusMessage = string.IsNullOrWhiteSpace(failureReason)
            ? "地图截图失败。"
            : failureReason;
        _logCollector.Append(
            MapLogCategory.ViewportCapture,
            MapLogLevel.Warning,
            _statusMessage,
            elapsedMs: elapsedMs);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
