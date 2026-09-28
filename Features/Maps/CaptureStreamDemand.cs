namespace IDVBuff.Features.Maps;

/// <summary>Access under the capture service's stream gate.</summary>
internal sealed class CaptureStreamDemand
{
    private int _active;
    private long _lastDemand;
    public void Request(long now) => _lastDemand = now;
    public void Begin(long now) { _active++; Request(now); }
    public void End(long now) { _active--; Request(now); }
    public bool CanRelease(long now) => _active == 0 && now - _lastDemand >= 2000;
}

// Process-wide counters only; no per-frame allocations or file writes.
internal static class CaptureStreamDiagnostics
{
    internal static long ActiveSessions = 0;
    internal static long FrameCallbacks = 0;
    internal static long Readbacks = 0;
}
