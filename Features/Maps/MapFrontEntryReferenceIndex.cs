using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Value-only bounds in one reference floor's own coordinates.</summary>
public readonly record struct MapFrontEntryReferenceBounds(
    double X, double Y, double Width, double Height);

/// <summary>
/// One distinct ordinal pair, in the original extractor order (A &lt; B).
/// No span, ray-direction or semantic filter has been applied to this pair.
/// </summary>
public readonly record struct MapFrontEntryReferenceCornerPair(
    int CornerOrdinalA,
    int CornerOrdinalB,
    Point2d Delta,
    double SquaredLength,
    Point2d Midpoint);

/// <summary>
/// An immutable copy of a supplied entrance anchor and its full radius neighborhood.
/// Repeated anchor IDs/roles remain separate, identified by InputOrdinal.
/// </summary>
public sealed class MapFrontEntryReferenceAnchor
{
    public int InputOrdinal { get; }
    public Guid Id { get; }
    public string RoleKey { get; }
    public RecognitionAnchorRole AnnotationRole { get; }
    public MapFrontEntryReferenceBounds? NormalizedBounds { get; }
    public MapFrontEntryReferenceBounds? PixelBounds { get; }
    public Point2d? Center { get; }
    public double? Radius { get; }
    public ImmutableArray<int> CornerOrdinals { get; }
    public ImmutableArray<MapFrontEntryReferenceCornerPair> Pairs { get; }
    public bool HasUsableBounds => PixelBounds.HasValue && Center.HasValue && Radius.HasValue;

    internal MapFrontEntryReferenceAnchor(int inputOrdinal, Guid id, string roleKey,
        RecognitionAnchorRole annotationRole, MapFrontEntryReferenceBounds? normalizedBounds,
        MapFrontEntryReferenceBounds? pixelBounds, Point2d? center, double? radius,
        ImmutableArray<int> cornerOrdinals, ImmutableArray<MapFrontEntryReferenceCornerPair> pairs)
    {
        InputOrdinal = inputOrdinal;
        Id = id;
        RoleKey = roleKey;
        AnnotationRole = annotationRole;
        NormalizedBounds = normalizedBounds;
        PixelBounds = pixelBounds;
        Center = center;
        Radius = radius;
        CornerOrdinals = cornerOrdinals;
        Pairs = pairs;
    }
}

/// <summary>
/// Reports every canonical role, including roles absent from the supplied list.
/// Applicability to a query or floor is decided by the caller, never by this record.
/// </summary>
public readonly record struct MapFrontEntryReferenceRoleCoverage(
    string RoleKey, int SuppliedAnchorCount, int UsableAnchorCount, long PairCount)
{
    public bool IsAbsent => SuppliedAnchorCount == 0;
    public bool HasPairDescriptors => PairCount > 0;
}

/// <summary>A source-preparation gap, not negative map or pose evidence.</summary>
public sealed record MapFrontEntryReferenceIssue(
    string Code, string Detail, int? AnchorInputOrdinal = null);

/// <summary>
/// Immutable managed geometry of one actual prepared floor. It owns no Mat,
/// floor, lease, query, scale prior or identity/session state. Corners cover the
/// full floor, so a future third-corner query is not restricted to anchor radii.
/// RawWhiteXByRow contains sorted unique X coordinates of the exact original
/// reference-edge pixel set; no dilation, interpolation or mask change is used.
/// </summary>
public sealed class MapFrontEntryReferenceIndex
{
    public Guid MapId { get; }
    public int MapSequence { get; }
    public string FloorKey { get; }
    public Vpsg3IndexCacheKey CacheKey { get; }
    public MapCatalogRevision CatalogRevision { get; }
    public int ReferenceWidth { get; }
    public int ReferenceHeight { get; }
    public int SourceEdgePointCount { get; }
    public int RawWhitePixelCount { get; }
    public ImmutableArray<MapLocalCornerGeometry> Corners { get; }
    /// <summary>One stable, read-only interface view; no mutable array is exposed.</summary>
    internal IReadOnlyList<MapLocalCornerGeometry> CornerGeometry { get; }
    public ImmutableArray<ImmutableArray<int>> RawWhiteXByRow { get; }
    public ImmutableArray<MapFrontEntryReferenceAnchor> Anchors { get; }
    public ImmutableArray<MapFrontEntryReferenceRoleCoverage> RoleCoverage { get; }
    public ImmutableArray<MapFrontEntryReferenceIssue> CoverageIssues { get; }
    /// <summary>
    /// Approximate retained managed arrays/descriptors, with object/array
    /// allowances and string payloads counted without sharing deduplication.
    /// Excludes native memory, the temporary Mat, the prepared floor and leases;
    /// this is not an exact CLR allocator or process-memory measurement.
    /// </summary>
    public long EstimatedManagedBytes { get; }

    internal MapFrontEntryReferenceIndex(Guid mapId, int mapSequence, string floorKey,
        Vpsg3IndexCacheKey cacheKey, MapCatalogRevision catalogRevision,
        int referenceWidth, int referenceHeight, int sourceEdgePointCount,
        int rawWhitePixelCount, ImmutableArray<MapLocalCornerGeometry> corners,
        ImmutableArray<ImmutableArray<int>> rawWhiteXByRow,
        ImmutableArray<MapFrontEntryReferenceAnchor> anchors,
        ImmutableArray<MapFrontEntryReferenceRoleCoverage> roleCoverage,
        ImmutableArray<MapFrontEntryReferenceIssue> coverageIssues)
    {
        MapId = mapId;
        MapSequence = mapSequence;
        FloorKey = floorKey;
        CacheKey = cacheKey;
        CatalogRevision = catalogRevision;
        ReferenceWidth = referenceWidth;
        ReferenceHeight = referenceHeight;
        SourceEdgePointCount = sourceEdgePointCount;
        RawWhitePixelCount = rawWhitePixelCount;
        Corners = corners;
        CornerGeometry = corners;
        RawWhiteXByRow = rawWhiteXByRow;
        Anchors = anchors;
        RoleCoverage = roleCoverage;
        CoverageIssues = coverageIssues;
        EstimatedManagedBytes = EstimateManagedBytes();
    }

    private long EstimateManagedBytes()
    {
        const int headerAllowance = 24;
        long bytes = 256 + headerAllowance // index and boxed read-only corner view
            + ArrayBytes(Corners.Length, Unsafe.SizeOf<MapLocalCornerGeometry>())
            + ArrayBytes(RawWhiteXByRow.Length, IntPtr.Size)
            + ArrayBytes(Anchors.Length, IntPtr.Size)
            + ArrayBytes(RoleCoverage.Length, Unsafe.SizeOf<MapFrontEntryReferenceRoleCoverage>())
            + ArrayBytes(CoverageIssues.Length, IntPtr.Size)
            + StringBytes(FloorKey) + StringBytes(CacheKey.FloorKey)
            + StringBytes(CacheKey.ContentFingerprint) + StringBytes(CacheKey.StructureGeneration);
        foreach (var row in RawWhiteXByRow)
            bytes += ArrayBytes(row.Length, sizeof(int));
        foreach (var anchor in Anchors)
            bytes += 160 + StringBytes(anchor.RoleKey)
                + ArrayBytes(anchor.CornerOrdinals.Length, sizeof(int))
                + ArrayBytes(anchor.Pairs.Length, Unsafe.SizeOf<MapFrontEntryReferenceCornerPair>());
        foreach (var role in RoleCoverage)
            bytes += StringBytes(role.RoleKey);
        foreach (var issue in CoverageIssues)
            bytes += 48 + StringBytes(issue.Code) + StringBytes(issue.Detail);
        return bytes;

        static long ArrayBytes(int length, int elementBytes) =>
            length == 0 ? 0 : headerAllowance + (long)length * elementBytes;
        static long StringBytes(string? text) =>
            text is null ? 0 : headerAllowance + ((long)text.Length + 1) * sizeof(char);
    }
}

/// <summary>
/// IsReady means the supplied source descriptors were built without preparation
/// gaps. It never means all roles exist, all query poses were searched, or an
/// identity comparison is complete. An available partial index retains its data
/// and explicit coverage gaps; a missing candidate must remain unknown.
/// </summary>
public sealed record MapFrontEntryReferenceIndexBuildResult(
    MapFrontEntryReferenceIndex? Index,
    ImmutableArray<MapFrontEntryReferenceIssue> Issues)
{
    public bool IsAvailable => Index is not null;
    public bool IsReady => IsAvailable && Issues.IsEmpty;
}
