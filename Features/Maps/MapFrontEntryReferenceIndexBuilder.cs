using System.Collections.Immutable;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Source-only preprocessing for one prepared floor. The caller supplies a
/// protected floor and a catalog/anchor snapshot, then owns freshness checks
/// and publication. This builder takes no lease and retains no mutable input.
/// </summary>
public static class MapFrontEntryReferenceIndexBuilder
{
    private static readonly string[] EntranceRoleKeys =
        ["main-entrance", "side-entrance", "second-floor-primary"];

    private readonly record struct AnchorSnapshot(int InputOrdinal, Guid Id,
        string RoleKey, RecognitionAnchorRole AnnotationRole,
        MapFrontEntryReferenceBounds? Bounds);

    public static MapFrontEntryReferenceIndexBuildResult Build(
        MapRecord map,
        string floorKey,
        Vpsg3PreparedFloor floor,
        Vpsg3IndexCacheKey expectedKey,
        MapCatalogRevision catalogRevision,
        IReadOnlyList<RecognitionAnchor> anchors,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentNullException.ThrowIfNull(anchors);
        var issues = new List<MapFrontEntryReferenceIssue>();
        MapFrontEntryReferenceIndexBuildResult Unavailable(string code, string detail)
        {
            issues.Add(new(code, detail));
            return new(null, issues.ToImmutableArray());
        }

        if (string.IsNullOrWhiteSpace(floorKey))
            return Unavailable("MissingFloorKey", "No actual floor key was supplied.");
        var normalizedFloorKey = floorKey.Trim().ToLowerInvariant();
        if (map.Id != expectedKey.MapId || floor.CacheKey != expectedKey
            || expectedKey.NormalizeFloorKey() != normalizedFloorKey)
            return Unavailable("FloorBindingMismatch", "Map, floor and supplied prepared cache key differ.");
        if (floor.IsDisposed)
            return Unavailable("PreparedFloorDisposed", "The prepared floor is disposed.");
        if (catalogRevision == MapCatalogRevision.Empty)
            return Unavailable("MissingCatalogRevision", "An actual catalog revision is required.");
        var definitions = (map.Floors ?? []).Where(definition => definition is not null
            && string.Equals(definition.Key, floorKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (definitions.Length != 1)
            return Unavailable("FloorDefinitionMismatch", "The map does not contain one matching floor definition.");
        var definition = definitions[0];
        var width = floor.ReferenceWidth;
        var height = floor.ReferenceHeight;
        if (width <= 0 || height <= 0)
            return Unavailable("InvalidReferenceDimensions", "Prepared reference dimensions must be positive.");
        if (definition.RecognitionWidth > 0 && definition.RecognitionHeight > 0
            && (definition.RecognitionWidth != width || definition.RecognitionHeight != height))
            return Unavailable("RecognitionDimensionsMismatch", "Recognition and prepared floor dimensions differ.");

        // Snapshot catalog scalars and anchors before the native extraction.
        // The caller, not this helper, prevents concurrent catalog mutation.
        var mapId = map.Id;
        var mapSequence = map.SequenceNumber;
        var actualFloorKey = definition.Key;
        var anchorSnapshots = CaptureAnchors(anchors, issues, cancellationToken);
        var points = floor.ReferenceEdgePoints.ToArray();
        if (points.Length == 0)
            return Unavailable("ReferenceEdgePointsMissing", "The original prepared edge-point set is empty.");
        var whiteRows = new List<int>?[height];
        foreach (var point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)point.X >= (uint)width || (uint)point.Y >= (uint)height)
                return Unavailable("ReferenceEdgePointOutOfBounds", "An original edge point lies outside its own floor.");
            (whiteRows[point.Y] ??= []).Add(point.X);
        }

        var rawRows = ImmutableArray.CreateBuilder<ImmutableArray<int>>(height);
        var rawWhitePixelCount = 0;
        using var reference = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = whiteRows[y];
            if (row is null)
            {
                rawRows.Add(ImmutableArray<int>.Empty);
                continue;
            }
            row.Sort();
            var unique = ImmutableArray.CreateBuilder<int>();
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0 && row[i] == row[i - 1]) continue;
                unique.Add(row[i]);
                reference.Set(y, row[i], (byte)255);
            }
            rawWhitePixelCount = checked(rawWhitePixelCount + unique.Count);
            rawRows.Add(unique.ToImmutable());
        }

        ImmutableArray<MapLocalCornerGeometry> corners;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Exactly the same full-floor reconstruction and extractor as the
            // existing geometry solver; preserve every returned corner/order.
            corners = MapLocalCornerGeometryExtractor.ExtractCorners(reference).ToImmutableArray();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && exception is not OutOfMemoryException)
        {
            return Unavailable("CornerExtractionFailed", $"{exception.GetType().Name}: {exception.Message}");
        }
        if (corners.IsEmpty)
            issues.Add(new("NoReferenceCorners", "The exact original bitmap produced no supported corners."));
        if (corners.Any(corner => !IsFinite(corner)))
            return Unavailable("NonFiniteCornerGeometry", "The actual extractor returned nonfinite geometry.");
        if (anchorSnapshots.Count == 0)
            issues.Add(new("NoEntranceAnchors", "No MAIN, SIDE or second-floor anchor was supplied."));

        var descriptors = ImmutableArray.CreateBuilder<MapFrontEntryReferenceAnchor>(anchorSnapshots.Count);
        foreach (var anchor in anchorSnapshots)
            descriptors.Add(BuildAnchor(anchor, corners, width, height, issues, cancellationToken));
        var immutableAnchors = descriptors.ToImmutable();
        var coverage = EntranceRoleKeys.Select(role =>
        {
            var supplied = immutableAnchors.Where(anchor => anchor.RoleKey == role).ToArray();
            return new MapFrontEntryReferenceRoleCoverage(role, supplied.Length,
                supplied.Count(anchor => anchor.HasUsableBounds),
                supplied.Sum(anchor => (long)anchor.Pairs.Length));
        }).ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (floor.IsDisposed)
            return Unavailable("PreparedFloorDisposed", "The floor was disposed during source preparation.");
        var immutableIssues = issues.ToImmutableArray();
        var index = new MapFrontEntryReferenceIndex(mapId, mapSequence, actualFloorKey,
            expectedKey, catalogRevision, width, height, points.Length, rawWhitePixelCount,
            corners, rawRows.ToImmutable(), immutableAnchors, coverage, immutableIssues);
        cancellationToken.ThrowIfCancellationRequested();
        return new(index, immutableIssues);
    }

    private static List<AnchorSnapshot> CaptureAnchors(IReadOnlyList<RecognitionAnchor> anchors,
        List<MapFrontEntryReferenceIssue> issues, CancellationToken cancellationToken)
    {
        var result = new List<AnchorSnapshot>();
        for (var i = 0; i < anchors.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchor = anchors[i];
            if (anchor is null)
            {
                issues.Add(new("NullSuppliedAnchor", "A supplied anchor is null.", i));
                continue;
            }
            if (!EntranceRoleKeys.Contains(anchor.Key, StringComparer.Ordinal)) continue;
            var bounds = anchor.Bounds;
            if (bounds is null && anchor.Role == RecognitionAnchorRole.Optional)
                continue;
            result.Add(new(i, anchor.Id, anchor.Key, anchor.Role, bounds is null ? null
                : new(bounds.X, bounds.Y, bounds.Width, bounds.Height)));
        }
        return result;
    }

    private static MapFrontEntryReferenceAnchor BuildAnchor(AnchorSnapshot anchor,
        ImmutableArray<MapLocalCornerGeometry> corners, int width, int height,
        List<MapFrontEntryReferenceIssue> issues, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (anchor.Bounds is not { } bounds || !double.IsFinite(bounds.X)
            || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width)
            || !double.IsFinite(bounds.Height) || bounds.Width < .01 || bounds.Height < .01)
        {
            issues.Add(new("InvalidEntranceAnchorBounds", "The supplied anchor has no usable finite bounds.",
                anchor.InputOrdinal));
            return new(anchor.InputOrdinal, anchor.Id, anchor.RoleKey, anchor.AnnotationRole,
                anchor.Bounds, null, null, null, [], []);
        }
        var pixelBounds = new MapFrontEntryReferenceBounds(bounds.X * width, bounds.Y * height,
            bounds.Width * width, bounds.Height * height);
        // Preserve the existing solver's normalized-to-pixel expression and radius.
        var center = new Point2d((bounds.X + bounds.Width / 2) * width,
            (bounds.Y + bounds.Height / 2) * height);
        var radius = Math.Max(80, Math.Max(bounds.Width * width, bounds.Height * height) * 3);
        if (!double.IsFinite(pixelBounds.X) || !double.IsFinite(pixelBounds.Y)
            || !double.IsFinite(pixelBounds.Width) || !double.IsFinite(pixelBounds.Height)
            || !double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(radius))
        {
            issues.Add(new("NonFiniteEntranceAnchorPixels", "The supplied bounds overflow own reference coordinates.",
                anchor.InputOrdinal));
            return new(anchor.InputOrdinal, anchor.Id, anchor.RoleKey, anchor.AnnotationRole,
                anchor.Bounds, null, null, null, [], []);
        }
        var near = ImmutableArray.CreateBuilder<int>();
        for (var i = 0; i < corners.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (double.Hypot(corners[i].Point.X - center.X, corners[i].Point.Y - center.Y) <= radius)
                near.Add(i);
        }
        var pairs = ImmutableArray.CreateBuilder<MapFrontEntryReferenceCornerPair>();
        for (var ai = 0; ai < near.Count; ai++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var bi = ai + 1; bi < near.Count; bi++)
            {
                var a = corners[near[ai]].Point;
                var b = corners[near[bi]].Point;
                var delta = b - a;
                pairs.Add(new(near[ai], near[bi], delta, delta.X * delta.X + delta.Y * delta.Y,
                    (a + b) * .5));
            }
        }
        if (pairs.Count == 0)
            issues.Add(new("NoAnchorCornerPairs", "Fewer than two corner ordinals lie inside the existing radius.",
                anchor.InputOrdinal));
        cancellationToken.ThrowIfCancellationRequested();
        return new(anchor.InputOrdinal, anchor.Id, anchor.RoleKey, anchor.AnnotationRole,
            bounds, pixelBounds, center, radius, near.ToImmutable(), pairs.ToImmutable());
    }

    private static bool IsFinite(MapLocalCornerGeometry corner) =>
        double.IsFinite(corner.Point.X) && double.IsFinite(corner.Point.Y)
        && double.IsFinite(corner.RayA.X) && double.IsFinite(corner.RayA.Y)
        && double.IsFinite(corner.RayB.X) && double.IsFinite(corner.RayB.Y)
        && double.IsFinite(corner.RayALength) && double.IsFinite(corner.RayBLength)
        && double.IsFinite(corner.RayAAngleDegrees) && double.IsFinite(corner.RayBAngleDegrees)
        && double.IsFinite(corner.DirectionDeterminant);
}
