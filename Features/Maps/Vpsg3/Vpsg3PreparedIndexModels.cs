using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Status of a floor index in the VPSG 3.0 registry.
/// </summary>
public enum Vpsg3IndexStatus
{
    Missing,
    Building,
    Ready,
    Failed,
    Stale
}

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

    /// <summary>
    /// Computes the generation identity for a prebuilt structure line asset,
    /// binding line Sha256, algorithm Sha256, algorithm schema version, and VPSG3 schema version.
    /// Any upstream IDVA change immediately invalidates the prepared index.
    /// </summary>
    public static string CreatePrebuiltGenerationIdentity(
        PrebuiltStructureLineAsset prebuilt,
        int schemaVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(prebuilt);
        return $"{prebuilt.Sha256}_{prebuilt.AlgorithmSha256}_{prebuilt.AlgorithmSchemaVersion}_v{schemaVersion}";
    }

    public override string ToString() =>
        $"map={MapId:D};floor={NormalizeFloorKey()};v={SchemaVersion};fp={ContentFingerprint};gen={StructureGeneration};updated={UpdatedAt:O}";
}

/// <summary>
/// A slot in the VPSG3 registry representing the lifecycle state of a specific (MapId, FloorKey).
/// </summary>
public sealed class Vpsg3FloorSlot
{
    public Vpsg3IndexCacheKey ExpectedKey { get; }
    public Vpsg3IndexStatus Status { get; }
    public Vpsg3PreparedFloor? Floor { get; }
    public string? FailureReason { get; }
    public DateTimeOffset StatusChangedAt { get; }

    public bool IsReady => Status == Vpsg3IndexStatus.Ready && Floor is not null && !Floor.IsDisposed;

    public Vpsg3FloorSlot(
        Vpsg3IndexCacheKey expectedKey,
        Vpsg3IndexStatus status,
        Vpsg3PreparedFloor? floor = null,
        string? failureReason = null)
    {
        ExpectedKey = expectedKey;
        Status = status;
        Floor = floor;
        FailureReason = failureReason;
        StatusChangedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Tuning configuration for VPSG 3.0 fast scale gating and index preparation.
/// </summary>
public sealed class Vpsg3TuningConfig
{
    public static Vpsg3TuningConfig Default { get; } = new();

    /// <summary>
    /// Morphological dilation kernel size for the prepared bitset.
    /// Defaults to 5 (i.e. 5x5 window, +/-2px tolerance) matching the approved V-A Verification Benchmark.
    /// </summary>
    public int DilationKernelSize { get; init; } = 5;

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

    /// <summary>Minimum global verification score required to pass.</summary>
    public double MinVerificationScore { get; init; } = 0.60d;

    /// <summary>Minimum aperture score margin over distinct runner-up required to pass.</summary>
    public double MinApertureMargin { get; init; } = 0.09d;

    /// <summary>Minimum number of 2x2 spatial quadrants that must pass the quadrant score threshold.</summary>
    public int MinPassedPartitions { get; init; } = 3;

    /// <summary>Suppression basin radius for distinct runner-up search (px in screen space).</summary>
    public double NmsSuppressionRadius { get; init; } = 6.0d;

    /// <summary>Minimum spatial distance (px in screen space) for a candidate to be considered distinct from top-1.</summary>
    public double MinDistinctDistance { get; init; } = 6.0d;

    /// <summary>Minimum points required in a 2x2 quadrant for it to be statistically valid.</summary>
    public int MinPointsPerPartition { get; init; } = 5;

    /// <summary>Individual quadrant threshold ratio required to pass that quadrant.</summary>
    public double PartitionScoreThreshold { get; init; } = 0.50d;

    /// <summary>Coarse grid stride for T-3 correlation search (px in reference space).</summary>
    public int CoarseTranslationStride { get; init; } = 4;

    /// <summary>Maximum number of raw translation candidates considered before NMS.</summary>
    public int MaxTranslationCandidates { get; init; } = 50;

    /// <summary>Maximum sparse query edge points sampled for verification.</summary>
    public int MaxSparsePoints { get; init; } = 150;
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
/// Prohibits 0->1 resurrection via CAS TryRetain.
/// </summary>
public sealed class Vpsg3PreparedFloor : IDisposable
{
    private int _refCount = 1;
    private int _disposed;
    private ulong[]? _dilatedBitsetK5;
    private ulong[]? _dilatedBitsetK3;

    public Vpsg3IndexCacheKey CacheKey { get; }
    public int ReferenceWidth { get; }
    public int ReferenceHeight { get; }
    public int EdgePixelCount { get; }
    public Vpsg3ScalePrior ScalePrior { get; }
    public int WordsPerRow { get; }
    public long MemoryBytes { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public ReadOnlySpan<ulong> DilatedBitsetSpan =>
        _dilatedBitsetK5 is not null ? _dilatedBitsetK5.AsSpan() : ReadOnlySpan<ulong>.Empty;

    public ReadOnlySpan<ulong> DilatedBitsetK5Span =>
        _dilatedBitsetK5 is not null ? _dilatedBitsetK5.AsSpan() : ReadOnlySpan<ulong>.Empty;

    public ReadOnlySpan<ulong> DilatedBitsetK3Span =>
        _dilatedBitsetK3 is not null ? _dilatedBitsetK3.AsSpan() : ReadOnlySpan<ulong>.Empty;

    public ReadOnlyMemory<ulong> DilatedBitsetMemory =>
        _dilatedBitsetK5 is not null ? _dilatedBitsetK5.AsMemory() : ReadOnlyMemory<ulong>.Empty;

    public int BitsetWordCount => _dilatedBitsetK5?.Length ?? 0;

    public ulong GetBitsetWord(int index)
    {
        var bitset = _dilatedBitsetK5;
        if (bitset is null || (uint)index >= (uint)bitset.Length)
            throw new IndexOutOfRangeException($"Bitset word index {index} out of range.");
        return bitset[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsHitK5(int rx, int ry)
    {
        var bitset = _dilatedBitsetK5;
        if ((uint)rx < (uint)ReferenceWidth && (uint)ry < (uint)ReferenceHeight && bitset is not null)
        {
            return (bitset[ry * WordsPerRow + (rx >> 6)] & (1UL << (rx & 63))) != 0;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsHitK3(int rx, int ry)
    {
        var bitset = _dilatedBitsetK3;
        if ((uint)rx < (uint)ReferenceWidth && (uint)ry < (uint)ReferenceHeight && bitset is not null)
        {
            return (bitset[ry * WordsPerRow + (rx >> 6)] & (1UL << (rx & 63))) != 0;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TestK3K5(int rx, int ry, out bool isK5, out bool isK3)
    {
        var b5 = _dilatedBitsetK5;
        var b3 = _dilatedBitsetK3;
        if ((uint)rx < (uint)ReferenceWidth && (uint)ry < (uint)ReferenceHeight && b5 is not null && b3 is not null)
        {
            var idx = ry * WordsPerRow + (rx >> 6);
            var mask = 1UL << (rx & 63);
            isK5 = (b5[idx] & mask) != 0;
            isK3 = (b3[idx] & mask) != 0;
        }
        else
        {
            isK5 = false;
            isK3 = false;
        }
    }

    public Vpsg3PreparedFloor(
        Vpsg3IndexCacheKey cacheKey,
        int referenceWidth,
        int referenceHeight,
        int edgePixelCount,
        Vpsg3ScalePrior scalePrior,
        int wordsPerRow,
        ulong[] dilatedBitsetK5,
        ulong[] dilatedBitsetK3,
        long memoryBytes)
    {
        CacheKey = cacheKey;
        ReferenceWidth = referenceWidth;
        ReferenceHeight = referenceHeight;
        EdgePixelCount = edgePixelCount;
        ScalePrior = scalePrior;
        WordsPerRow = wordsPerRow;
        _dilatedBitsetK5 = dilatedBitsetK5 ?? throw new ArgumentNullException(nameof(dilatedBitsetK5));
        _dilatedBitsetK3 = dilatedBitsetK3 ?? throw new ArgumentNullException(nameof(dilatedBitsetK3));
        MemoryBytes = memoryBytes;
    }

    public Vpsg3PreparedFloor(
        Vpsg3IndexCacheKey cacheKey,
        int referenceWidth,
        int referenceHeight,
        int edgePixelCount,
        Vpsg3ScalePrior scalePrior,
        int wordsPerRow,
        ulong[] dilatedBitset,
        long memoryBytes)
        : this(cacheKey, referenceWidth, referenceHeight, edgePixelCount, scalePrior, wordsPerRow, dilatedBitset, dilatedBitset, memoryBytes)
    {
    }

    /// <summary>
    /// Atomically increments reference count from a positive value.
    /// Returns false if refCount is &lt;= 0 or object has been disposed, strictly preventing 0->1 resurrection.
    /// </summary>
    internal bool TryRetain()
    {
        while (true)
        {
            var current = Volatile.Read(ref _refCount);
            if (current <= 0 || Volatile.Read(ref _disposed) != 0)
                return false;

            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                return true;
        }
    }

    internal void Release()
    {
        while (true)
        {
            var current = Volatile.Read(ref _refCount);
            if (current <= 0)
                return;

            var next = current - 1;
            if (Interlocked.CompareExchange(ref _refCount, next, current) == current)
            {
                if (next == 0)
                {
                    Cleanup();
                }
                return;
            }
        }
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dilatedBitsetK5 = null;
            _dilatedBitsetK3 = null;
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

    private Vpsg3FloorIndexLease(Vpsg3PreparedFloor floor)
    {
        _floor = floor;
    }

    /// <summary>
    /// Safely attempts to construct a lease. Fails if the floor cannot be retained (e.g. 0-ref or disposed).
    /// </summary>
    internal static bool TryCreate(Vpsg3PreparedFloor floor, [NotNullWhen(true)] out Vpsg3FloorIndexLease? lease)
    {
        ArgumentNullException.ThrowIfNull(floor);
        if (floor.TryRetain())
        {
            lease = new Vpsg3FloorIndexLease(floor);
            return true;
        }

        lease = null;
        return false;
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
