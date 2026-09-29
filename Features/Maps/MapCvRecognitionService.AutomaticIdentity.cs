using System.Diagnostics;

namespace IDVBuff.Features.Maps;

internal sealed record AutomaticMapIdentityDecision(
    MapLocalFeatureIdentitySearch Search,
    MapLocalFeatureIdentityCandidate? Identity,
    string Reason,
    double StructureSupport,
    int StructurePoints,
    double ElapsedMilliseconds,
    IReadOnlyList<AutomaticIdentityStructureEvidence> StructureComparisons);

internal sealed record AutomaticIdentityStructureEvidence(Guid MapId, double Support,
    double Scale, double OffsetX, double OffsetY);

public sealed partial class MapCvRecognitionService
{
    public int RegisteredEntryMapCount => _maps.Count(map => map.Floors.Any(floor => floor.EntryIdentityAsset is not null));

    internal bool HasEntryIdentityResources(string? mapClass) => _entryIdentityIndex?.HasResources(mapClass) == true;

    internal MapRecord? GetAutomaticIdentityFloorTemplate(string mapClass)
    {
        var maps = _maps.Where(m => string.Equals(m.Class, mapClass, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (maps.Length == 0) return null;
        var floors = MapFloorRules.GetOrderedFloors(maps[0]).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        return maps.All(m => floors.SetEquals(MapFloorRules.GetOrderedFloors(m).Select(f => f.Key)))
            ? maps[0] : null;
    }

    internal string? ResolveAutomaticIdentityFloor(string mapClass, string? detectedFloor = null)
    {
        var maps = _maps.Where(map => string.Equals(map.Class, mapClass,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (maps.Length == 0) return null;
        var currentFloor = MapScanFloorRules.NormalizeFloorIdentity(detectedFloor);
        if (HasEntryIdentityResources(mapClass) && currentFloor is not null)
            return maps.All(m => MapFloorRules.GetOrderedFloors(m).Any(f =>
                MapScanFloorRules.NormalizeFloorIdentity(f.Key) == currentFloor)) ? currentFloor : null;
        var floors = maps.Select(MapScanFloorRules.ResolveScanFloorKey)
            .Select(MapScanFloorRules.NormalizeFloorIdentity).Distinct().ToArray();
        if (floors.Length != 1) return null;
        var detected = MapScanFloorRules.NormalizeFloorIdentity(detectedFloor);
        // Initial identity uses the class's configured scanning floor. Other
        // floors have independent post-lock alignment, not this reference pool.
        return detected is null || detected == floors[0] ? floors[0] : null;
    }

    internal Task PrepareAutomaticIdentityAsync(string mapClass, string floorKey) =>
        HasEntryIdentityResources(mapClass) ? _entryIdentityIndex!.PrepareAsync(mapClass, floorKey)
            : _localFeatureIdentityIndex?.PrepareAsync(mapClass, floorKey) ?? Task.CompletedTask;

    internal bool AutomaticIdentityPreparationPending(string? mapClass) =>
        !string.IsNullOrWhiteSpace(mapClass) && ResolveAutomaticIdentityFloor(mapClass) is { } floor
        && (HasEntryIdentityResources(mapClass) ? !_entryIdentityIndex!.IsPreparationFinished(mapClass, floor)
            : _localFeatureIdentityIndex?.IsPreparationFinished(mapClass, floor) == false);

    internal MapEntryIdentityDecision IdentifyEntryMap(CapturedGameFrame frame, string mapClass,
        string floorKey, CancellationToken token, Func<bool> canCompute, Guid? selectedMapId = null) =>
        _entryIdentityIndex!.Identify(frame, mapClass, floorKey, token, canCompute, () =>
        {
            using var match = GateTemplateDetector.CreateMatchImage(frame.Image);
            var result = DetectScanGates(match, frame.ViewportBounds, frame.ClientBounds.Width, .58,
                new GateSearchContext { Mode = GateSearchMode.FullSearch, AllowDualGateEarlyExit = false,
                    TimeBudgetMilliseconds = Math.Max(1, (ScanExecutionContext.Current?.RemainingMilliseconds ?? 1000) - 60) });
            return result.Gates;
        }, selectedMapId);

    internal AutomaticMapIdentityDecision IdentifyAutomaticMap(CapturedGameFrame frame,
        string mapClass, string floorKey, CancellationToken token, Func<bool> canCompute)
    {
        var watch = Stopwatch.StartNew();
        var search = _localFeatureIdentityIndex!.Identify(frame, mapClass, floorKey, token, canCompute);
        var structures = new List<AutomaticIdentityStructureEvidence>();
        AutomaticMapIdentityDecision Reject(string reason) => new(search, null, reason, 0, 0,
            watch.Elapsed.TotalMilliseconds, structures);
        if (!search.RetrievalCompleted || !canCompute()) return Reject("incomplete-comparison");
        var ranked = search.Candidates.OrderByDescending(candidate => candidate.Score).ToArray();
        if (ranked.Length == 0) return Reject("empty-map-class");
        var first = ranked[0];
        if (!HasDistributedIdentityFeatures(first)) return Reject("insufficient-distributed-features");
        // Similar authored maps must remain unresolved. Descriptor rank alone is
        // never a reason to accept a map or publish the feature fit as a pose.
        if (ranked.Skip(1).Any(candidate => first.Inliers < candidate.Inliers + 5
            || first.Inliers < candidate.Inliers * 1.5 || first.Score - candidate.Score < 6))
            return Reject("competing-local-features");

        using var evidence = new ScanFrameEvidence(frame.Image, frame.ViewportBounds, [],
            ScanExecutionContext.Current?.Policy ?? ScanExecutionPolicy.For(ScanPerformanceMode.Balanced));
        if (evidence.DensePoints.Length < 80) return Reject("insufficient-visible-structure");
        double winnerSupport = 0;
        foreach (var candidate in ranked)
        {
            if (!canCompute()) return Reject("comparison-deadline");
            if (!_sideEntranceFeatureCache.TryGetValue((candidate.Map.Id, candidate.FloorKey), out var line))
                return Reject("missing-floor-structure");
            if (candidate.Inliers < 3 || !IsIdentityFitValid(candidate.Transform)) continue;
            var index = ScanStructureIndex.Get(line);
            var transform = candidate.Transform;
            if (index.Width != transform.ReferenceWidth || index.Height != transform.ReferenceHeight)
                return Reject("incompatible-floor-structure");
            var scale = transform.ScaleX;
            var x = transform.OffsetX - frame.ViewportBounds.X;
            var y = transform.OffsetY - frame.ViewportBounds.Y;
            var hits = 0;
            for (var i = 0; i < evidence.DensePoints.Length; i++)
            {
                if ((i & 255) == 0 && !canCompute()) return Reject("comparison-deadline");
                var point = evidence.DensePoints[i];
                if (index.Distance((point.X - x) / scale, (point.Y - y) / scale, scale)
                    <= ScanIdentityVerifier.SupportTolerancePixels) hits++;
            }
            var support = hits / (double)evidence.DensePoints.Length;
            structures.Add(new(candidate.Map.Id, support, scale, x, y));
            if (ReferenceEquals(candidate, first)) winnerSupport = support;
            // Common corridors can explain many contour pixels under a weak
            // three-point fit. A competing identity needs the same distributed
            // feature evidence as the winner, not contour coverage alone.
            else if (HasDistributedIdentityFeatures(candidate)
                && support >= ScanIdentityVerifier.MinimumSupport)
                return Reject("competing-visible-structure");
        }
        if (winnerSupport < ScanIdentityVerifier.MinimumSupport)
            return Reject("unexplained-visible-structure");
        return new(search, first, "distributed-features-and-visible-structure", winnerSupport,
            evidence.DensePoints.Length, watch.Elapsed.TotalMilliseconds, structures);
    }

    internal static bool HasDistributedIdentityFeatures(MapLocalFeatureIdentityCandidate candidate) =>
        candidate.AxisAlignedInliers >= 12 && candidate.Matches > 0
        && candidate.AxisAlignedInliers / (double)candidate.Matches >= .30
        && candidate.MedianError <= 2.5 && candidate.P90Error <= 5
        && candidate.HullFraction >= .04 && candidate.SpanX >= .25 && candidate.SpanY >= .25
        && Math.Abs(candidate.RotationDegrees) <= 2 && IsIdentityFitValid(candidate.Transform);

    private static bool IsIdentityFitValid(MapOverlayTransform transform) =>
        double.IsFinite(transform.ScaleX) && transform.ScaleX >= SideEntranceScanRules.MinimumScale
        && transform.ScaleX <= SideEntranceScanRules.MaximumScale
        && Math.Abs(transform.ScaleX - transform.ScaleY) < .000001
        && double.IsFinite(transform.OffsetX) && double.IsFinite(transform.OffsetY);
}
