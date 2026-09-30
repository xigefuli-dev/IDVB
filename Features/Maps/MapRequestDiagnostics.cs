using System.Diagnostics;

namespace IDVBuff.Features.Maps;

// Diagnostic state belongs to one invocation, never to the shared session.
internal sealed class MapInputOperationContext : IDisposable
{
    private static readonly AsyncLocal<MapInputOperationContext?> Ambient = new();
    private readonly MapInputOperationContext? _previous = Ambient.Value;
    public static MapInputOperationContext? Current => Ambient.Value;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string? ScanId { get; set; }
    public string Outcome { get; set; } = "pending";
    public string Reason { get; set; } = "handler-not-returned";
    public MapInputOperationContext() => Ambient.Value = this;
    public void Dispose() => Ambient.Value = _previous;
}

internal sealed class ScanRequestDiagnostics : IDisposable
{
    private static readonly AsyncLocal<ScanRequestDiagnostics?> Ambient = new();
    private readonly ScanRequestDiagnostics? _previous = Ambient.Value;
    private readonly long _started = Stopwatch.GetTimestamp();
    public static ScanRequestDiagnostics? Current => Ambient.Value;
    public string ScanId { get; } = Guid.NewGuid().ToString("N");
    public string? InputOperationId { get; } = MapInputOperationContext.Current?.Id;
    public string Stage { get; set; } = "request";
    public string Outcome { get; private set; } = "pending";
    public string Reason { get; private set; } = "not-completed";
    public long StartedTimestamp => _started;
    public double ElapsedMilliseconds => Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
    public bool PipelineStarted { get; set; }
    public string? TraceId { get; set; }
    public ScanRequestDiagnostics() => Ambient.Value = this;
    public void Complete(string outcome, string reason)
    {
        Outcome = outcome;
        Reason = reason;
    }
    public void Dispose() => Ambient.Value = _previous;
}
