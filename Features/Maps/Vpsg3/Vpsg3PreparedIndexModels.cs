using System.Diagnostics.CodeAnalysis;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Cache identity for a prepared VPSG3 floor index.
/// Guaranteed to uniquely identify map content, floor, update timestamp, generation, and schema.
/// </summary>
public readonly record struct Vpsg3IndexCacheKey(
    Guid MapId,
    string FloorKey,
    string ContentFingerprint,
    DateTimeOffset UpdatedAt,
    string StructureGeneration,
    int SchemaVersion = 1)
{
    public string NormalizeFloorKey() =>
        string.IsNullOrWhiteSpace(FloorKey) ? string.Empty : FloorKey.Trim().ToLowerInvariant();

    public override string ToString() =>
        $"map={MapId:D};floor={NormalizeFloorKey()};v={SchemaVersion};fp={ContentFingerprint};gen={StructureGeneration};updated={UpdatedAt:O}";
}

/// <summary>
/// Tuning configuration for VPSG 3.0 fast scale gating and index preparation.
/// </summary>
public sealed class Vpsg3TuningConfig
{
    public static Vpsg3TuningConfig Default { get; } = new();

    /// <summary>Minimum scale supported by the fast registration path.</summary>
    public double MinSupportedScale { get; init; } = 0.70d;

    /// <summary>Maximum scale supported by the fast registration path.</summary>
    public double MaxSupportedScale { get; init; } = 1.50d;

    /// <summary>Minimum peak-to-median ratio required for high-confidence scale estimation.</summary>
    public double PeakRatioThreshold { get; init; } = 2.0d;

    /// <summary>Minimum edge pixels required to build a valid prepared index.</summary>
    public int MinEdgePixels { get; init; } = 300;

    /// <summary>Scale search radius around seed prior during refinement.</summary>
    public double CorrelationScaleTolerance { get; init; } = 0.08d;
}

/// <summary>
/// High-confidence scale prior estimated for a prepared floor or query.
/// Acts as a scale prior/seed, not an immutable final scale.
/// </summary>
public sealed record Vpsg3ScalePrior(
    double SeedScale,
    double PeakRatio,
    bool FastPathEligible,
    string RejectReason,
    double ReferencePitch,
    double ReferencePeakRatio)
{
    public static Vpsg3ScalePrior Ineligible(string reason, double refPitch = 0d, double refRatio = 0d) =>
        new(
            SeedScale: 1.0d,
            PeakRatio: 0.0d,
            FastPathEligible: false,
            RejectReason: reason,
            ReferencePitch: refPitch,
            ReferencePeakRatio: refRatio);
}

/// <summary>
/// Immutable prepared floor representation resident in memory for VPSG 3.0.
/// Supports ref-counted delayed disposal so active lease holders are never invalidated during background swaps.
/// </summary>
public sealed class Vpsg3PreparedFloor : IDisposable
{
    private int _refCount = 1;
    private int _disposed;
    private ulong[]? _dilatedBitset;

    public Vpsg3IndexCacheKey CacheKey { get; }
    public int ReferenceWidth { get; }
    public int ReferenceHeight { get; }
    public int EdgePixelCount { get; }
    public Vpsg3ScalePrior ScalePrior { get; }
    public int WordsPerRow { get; }
    public long MemoryBytes { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public ReadOnlySpan<ulong> DilatedBitsetSpan =>
        _dilatedBitset is not null ? _dilatedBitset.AsSpan() : ReadOnlySpan<ulong>.Empty;

    public ulong[]? UnsafeDilatedBitset => _dilatedBitset;

    public Vpsg3PreparedFloor(
        Vpsg3IndexCacheKey cacheKey,
        int referenceWidth,
        int referenceHeight,
        int edgePixelCount,
        Vpsg3ScalePrior scalePrior,
        int wordsPerRow,
        ulong[] dilatedBitset,
        long memoryBytes)
    {
        CacheKey = cacheKey;
        ReferenceWidth = referenceWidth;
        ReferenceHeight = referenceHeight;
        EdgePixelCount = edgePixelCount;
        ScalePrior = scalePrior;
        WordsPerRow = wordsPerRow;
        _dilatedBitset = dilatedBitset ?? throw new ArgumentNullException(nameof(dilatedBitset));
        MemoryBytes = memoryBytes;
    }

    internal void Retain()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Interlocked.Increment(ref _refCount);
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dilatedBitset = null;
        }
    }

    public void Dispose()
    {
        Release();
    }
}

/// <summary>
/// Non-blocking, thread-safe lease on a prepared floor index.
/// Releases its reference to the floor upon disposal.
/// </summary>
public sealed class Vpsg3FloorIndexLease : IDisposable
{
    private Vpsg3PreparedFloor? _floor;

    internal Vpsg3FloorIndexLease(Vpsg3PreparedFloor floor)
    {
        ArgumentNullException.ThrowIfNull(floor);
        _floor = floor;
        _floor.Retain();
    }

    public Vpsg3PreparedFloor Floor =>
        _floor ?? throw new ObjectDisposedException(nameof(Vpsg3FloorIndexLease));

    public void Dispose()
    {
        var floor = Interlocked.Exchange(ref _floor, null);
        floor?.Release();
    }
}

/// <summary>
/// Result of building a prepared floor index.
/// </summary>
public sealed record Vpsg3IndexBuildResult(
    bool Success,
    Vpsg3PreparedFloor? Floor,
    string? ErrorMessage,
    double BuildMilliseconds);
