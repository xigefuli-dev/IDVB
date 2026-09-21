using System.Diagnostics;
using OpenCvSharp;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

[JsonConverter(typeof(ScanPerformanceModeJsonConverter))]
public enum ScanPerformanceMode { Fast, Balanced, Quality }
public enum ScanUncertainAction { ShowCandidates, ReportUnrecognized }

public sealed class ScanPerformanceModeJsonConverter : JsonConverter<ScanPerformanceMode>
{
    public override ScanPerformanceMode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number)
            && Enum.IsDefined((ScanPerformanceMode)number)) return (ScanPerformanceMode)number;
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<ScanPerformanceMode>(reader.GetString(), true, out var mode)
            && Enum.IsDefined(mode)) return mode;
        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject) reader.Skip();
        return ScanPerformanceMode.Balanced;
    }
    public override void Write(Utf8JsonWriter writer, ScanPerformanceMode value, JsonSerializerOptions options) => writer.WriteNumberValue((int)value);
}

internal static class ScanUncertainPolicies
{
    internal sealed record Policy(bool OfferCandidates);
    private static readonly IReadOnlyDictionary<ScanUncertainAction, Policy> Registered =
        new Dictionary<ScanUncertainAction, Policy>
        {
            [ScanUncertainAction.ShowCandidates] = new(true),
            [ScanUncertainAction.ReportUnrecognized] = new(false)
        };
    public static Policy Resolve(ScanUncertainAction action) => Registered.TryGetValue(action, out var policy)
        ? policy : Registered[ScanUncertainAction.ShowCandidates];
}

public sealed record ScanExecutionPolicy(
    ScanPerformanceMode Mode, int BudgetMilliseconds, int SparsePoints,
    double CoarseScaleStep, double FineScaleStep, int BasinCount, int ResidualStep)
{
    public double MinimumScale { get; init; } = SideEntranceScanRules.MinimumScale;
    public double MaximumScale { get; init; } = SideEntranceScanRules.MaximumScale;
    public static ScanExecutionPolicy For(ScanPerformanceMode mode) => mode switch
    {
        ScanPerformanceMode.Fast => new(mode, 500, 128, .08, .02, 1, 3),
        ScanPerformanceMode.Quality => new(mode, 2000, 512, .02, .005, 3, 1),
        _ => new(ScanPerformanceMode.Balanced, 1000, 256, .04, .01, 2, 3)
    };
}

/// <summary>One immutable policy and monotonic deadline from dispatch through publication.
/// The lease flows into worker tasks; user selection explicitly ends automatic scanning.</summary>
internal sealed class ScanExecutionContext : IDisposable
{
    private static readonly AsyncLocal<ScanExecutionContext?> Ambient = new();
    private readonly ScanExecutionContext? _previous;
    private readonly long _started;
    private readonly CancellationToken _cancellation;
    private readonly Func<bool>? _isCurrent;
    private double? _completedMilliseconds;
    private bool _disposed;
    public static ScanExecutionContext? Current => Ambient.Value;
    public ScanExecutionPolicy Policy { get; }
    public CancellationToken CancellationToken => _cancellation;
    public ScanFrameEvidence? Frame { get; private set; }
    public bool RetrievalCompleted { get; set; } = true;
    public int EligibleIdentities { get; set; }
    public object? CatalogRevision { get; set; }
    public int TestedHypotheses;
    public IReadOnlyList<Guid[]> VariantGroups { get; set; } = [];
    public int VariantRefinementCount { get; set; }
    public bool Reported { get; set; }
    public double ElapsedMilliseconds => _completedMilliseconds
        ?? Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
    public int RemainingMilliseconds => Math.Max(0,
        Policy.BudgetMilliseconds - (int)Math.Ceiling(ElapsedMilliseconds));
    public bool IsAutomatic => !_completedMilliseconds.HasValue;
    public bool IsSuperseded => _isCurrent?.Invoke() == false;
    public bool Expired => IsAutomatic && (_cancellation.IsCancellationRequested || IsSuperseded || RemainingMilliseconds == 0);
    // Reserve time for the UI-thread commit; never give each candidate a new budget.
    public bool CanCompute => !Expired && (!IsAutomatic || RemainingMilliseconds > 60);
    private ScanExecutionContext(ScanPerformanceMode mode, CancellationToken cancellation, Func<bool>? isCurrent, long? startedTimestamp)
    {
        _started = startedTimestamp ?? Stopwatch.GetTimestamp();
        Policy = ScanExecutionPolicy.For(mode);
        _cancellation = cancellation;
        _isCurrent = isCurrent;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }
    public static ScanExecutionContext Enter(ScanPerformanceMode mode, CancellationToken cancellation = default, Func<bool>? isCurrent = null,
        long? startedTimestamp = null) => new(mode, cancellation, isCurrent, startedTimestamp);
    public static IDisposable Suppress()
    {
        var previous = Ambient.Value;
        Ambient.Value = null;
        return new RestoreAmbient(previous);
    }
    private sealed class RestoreAmbient(ScanExecutionContext? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
    public void SetFrame(ScanFrameEvidence frame)
    {
        Frame?.Dispose();
        Frame = frame;
    }
    public void CompleteAutomaticPhase()
    {
        _completedMilliseconds ??= Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Frame?.Dispose();
        Ambient.Value = _previous;
    }
}

internal sealed class ScanFrameEvidence : IDisposable
{
    public Mat Source { get; }
    public Vpsg3LiveObservation Observation { get; }
    public Point[] DensePoints { get; }
    public Point[] SearchPoints { get; }
    public IReadOnlyList<Point[]> Contours { get; }
    public ScanFrameEvidence(Mat frame, MapScreenRect? viewport, IReadOnlyList<GateDetection> gates,
        ScanExecutionPolicy policy, double doorMaskWidth = 0, double doorMaskHeight = 0)
    {
        Source = frame;
        Observation = Vpsg3FastLiveExtractor.Extract(frame, viewport, policy.SparsePoints);
        // Remove icons in evidence space, not by painting artificial edges into the source frame.
        var bounds = new Rect(0, 0, frame.Width, frame.Height);
        foreach (var gate in gates)
        {
            var width = Math.Max(gate.ScreenBounds.Width, doorMaskWidth) + 18;
            var height = Math.Max(gate.ScreenBounds.Height, doorMaskHeight) + 18;
            var rect = new Rect((int)Math.Floor(gate.ScreenBounds.CenterX - (viewport?.X ?? 0) - width / 2),
                (int)Math.Floor(gate.ScreenBounds.CenterY - (viewport?.Y ?? 0) - height / 2),
                (int)Math.Ceiling(width), (int)Math.Ceiling(height)).Intersect(bounds);
            if (rect.Width > 0 && rect.Height > 0)
            {
                Cv2.Rectangle(Observation.ObservedEdges, rect, Scalar.Black, -1);
                Cv2.Rectangle(Observation.ValidMask, rect, Scalar.Black, -1);
                Cv2.Rectangle(Observation.ProposalEdges, rect, Scalar.Black, -1);
            }
        }
        using var points = Observation.ObservedEdges.FindNonZero();
        DensePoints = new Point[(int)points.Total()];
        for (var i = 0; i < DensePoints.Length; i++) DensePoints[i] = points.At<Point>(i);
        SearchPoints = SampleUniform(DensePoints, frame.Width, frame.Height, policy.SparsePoints);
        var extracted = Observation;
        Observation = new Vpsg3LiveObservation(extracted.ObservedEdges.Clone(), extracted.ValidMask.Clone(),
            frame.Width, frame.Height, DensePoints.Length, Cv2.CountNonZero(extracted.ValidMask),
            extracted.ViewportBounds, policy.SparsePoints, extractionMilliseconds: extracted.ExtractionMilliseconds,
            proposalEdges: extracted.ProposalEdges.Clone());
        extracted.Dispose();
        Cv2.FindContours(Observation.ObservedEdges, out Point[][] contours, out _,
            RetrievalModes.List, ContourApproximationModes.ApproxNone);
        Contours = contours.Where(c => Cv2.ArcLength(c, false) >= 30).ToArray();
    }
    internal static Point[] SampleUniform(IReadOnlyList<Point> points, int width, int height, int maximum)
    {
        var cells = Enumerable.Range(0, 16).Select(_ => new List<Point>()).ToArray();
        foreach (var p in points)
            cells[Math.Min(3, p.X * 4 / Math.Max(1, width))
                + 4 * Math.Min(3, p.Y * 4 / Math.Max(1, height))].Add(p);
        var active = cells.Where(c => c.Count > 0).ToArray();
        if (active.Length == 0) return [];
        var result = new List<Point>(maximum);
        var quotas = new int[active.Length];
        for (var remaining = Math.Min(maximum, points.Count); remaining > 0;)
            for (var i = 0; i < active.Length && remaining > 0; i++)
                if (quotas[i] < active[i].Count) { quotas[i]++; remaining--; }
        for (var cellIndex = 0; cellIndex < active.Length; cellIndex++)
        {
            var cell = active[cellIndex];
            var count = quotas[cellIndex];
            for (var i = 0; i < count; i++) result.Add(cell[i * cell.Count / count]);
        }
        return result.ToArray();
    }
    public void Dispose() => Observation.Dispose();
}
