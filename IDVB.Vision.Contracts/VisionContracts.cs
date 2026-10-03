namespace IdentityVisionBridge.Vision;

/// <summary>Embeddable image processing. Results do not mutate a desktop host session.</summary>
public interface IIdvbVisionEngine : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VisionMap>> GetMapsAsync(CancellationToken cancellationToken = default);
    Task<VisionScanResult> ScanAsync(VisionScanRequest request, CancellationToken cancellationToken = default);
    Task<VisionAlignmentResult> AlignAsync(VisionAlignmentRequest request, CancellationToken cancellationToken = default);
}

public enum VisionScanMode { Fast, Balanced, Quality, DeepScan }
public enum VisionOutcome { Succeeded, Unrecognized, NeedsSelection, Rejected, TimedOut, Cancelled, NotReady, Busy }

/// <summary>PNG/JPEG viewport pixels. Viewport X/Y are in client pixels; Width/Height must match the decoded image.</summary>
public sealed record VisionFrame
{
    public required byte[] EncodedImage { get; init; }
    public VisionRectangle? Viewport { get; init; }
    public int? ClientWidth { get; init; }
    public int? ClientHeight { get; init; }
}

public sealed record VisionRectangle(double X, double Y, double Width, double Height);

public sealed record VisionScanRequest
{
    public required VisionFrame Frame { get; init; }
    public string? MapClass { get; init; }
    public VisionScanMode Mode { get; init; } = VisionScanMode.Balanced;
}

public sealed record VisionAlignmentRequest
{
    public required VisionFrame Frame { get; init; }
    public required Guid MapId { get; init; }
    public required string FloorKey { get; init; }
    public int BudgetMilliseconds { get; init; } = 1000;
}

public sealed record VisionMap(Guid Id, string DisplayName, string MapClass, IReadOnlyList<string> FloorKeys);

/// <summary>Reference-map pixels to client pixels: rotate about the origin, scale, then add OffsetX/OffsetY.</summary>
public sealed record VisionTransform
{
    public required double ScaleX { get; init; }
    public required double ScaleY { get; init; }
    public required double OffsetX { get; init; }
    public required double OffsetY { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public int OrientationDegrees { get; init; }
}

public sealed record VisionCandidate
{
    public required Guid MapId { get; init; }
    public required string DisplayName { get; init; }
    public required string FloorKey { get; init; }
    public double Score { get; init; }
    public string EvidenceState { get; init; } = "Unverified";
    public string Reason { get; init; } = "";
}

public sealed record VisionScanResult
{
    public required string OperationId { get; init; }
    public required VisionOutcome Outcome { get; init; }
    public bool Succeeded => Outcome == VisionOutcome.Succeeded;
    public Guid? MapId { get; init; }
    public string? FloorKey { get; init; }
    public double Confidence { get; init; }
    public VisionTransform? Transform { get; init; }
    public IReadOnlyList<VisionCandidate> Candidates { get; init; } = [];
    public double ElapsedMilliseconds { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>True only for a confirmed transaction in the desktop host; false for embedded processing.</summary>
    public bool CommittedToHost { get; init; }
}

public sealed record VisionAlignmentResult
{
    public required string OperationId { get; init; }
    public required VisionOutcome Outcome { get; init; }
    public bool Succeeded => Outcome == VisionOutcome.Succeeded;
    public Guid? MapId { get; init; }
    public string? FloorKey { get; init; }
    public double Confidence { get; init; }
    public VisionTransform? Transform { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public string Reason { get; init; } = "";
    public bool CommittedToHost { get; init; }
}

public sealed record HostVisionSnapshot
{
    public bool IsMatchStarted { get; init; }
    public string? MatchId { get; init; }
    public string? MapClass { get; init; }
    public bool IsScanning { get; init; }
    public bool IsMapOpen { get; init; }
    public bool IsOverlayVisible { get; init; }
    public string? SelectedMapClass { get; init; }
    public bool IsIdentityLocked { get; init; }
    public bool HasTrustedAlignment { get; init; }
    public Guid? MapId { get; init; }
    public string? FloorKey { get; init; }
    public long AlignmentRevision { get; init; }
    public VisionTransform? Transform { get; init; }
    public string StatusMessage { get; init; } = "";
}

public sealed record HostScanRequest
{
    /// <summary>Explicitly selects this identity if offered. Null returns candidates without opening a chooser.</summary>
    public Guid? SelectedMapId { get; init; }
}
