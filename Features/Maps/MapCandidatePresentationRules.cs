namespace IDVBuff.Features.Maps;

/// <summary>
/// Pure presentation rules for the native map candidate chooser. Candidate
/// evidence remains in recognition order; catalog-only maps are appended in
/// the user's map order and deliberately carry no recognition confidence.
/// </summary>
internal static class MapCandidatePresentationRules
{
    internal static bool CanPresentChoices(ScanExecutionContext execution,
        IReadOnlyList<MapRecognitionChoice>? choices)
    {
        if (execution.CancellationToken.IsCancellationRequested || execution.IsSuperseded) return false;
        // The user may explicitly identify a map even when its provisional
        // pose failed verification. Presentation never grants automatic support.
        return execution.RetrievalCompleted && choices is { Count: > 0 };
    }

    internal const double LivePreviewZoom = 1.20d;
    internal const double MapPreviewZoom = 1.10d;
    internal const double SecondaryFloorMapPreviewZoom = 3.00d;
    internal const double PreviewSafeInset = 0.10d;

    internal static SideEntranceScanCandidate[] SelectScanReferences(
        IEnumerable<SideEntranceScanCandidate> candidates, int maximum,
        ScanPerformanceMode mode = ScanPerformanceMode.Balanced)
    {
        var references = candidates.Where(candidate => candidate.Disposition != SideEntranceCandidateDisposition.Reliable);
        if (mode == ScanPerformanceMode.DeepScan)
            return references
                // Retrieval scores use sparse samples. Dense evidence must win
                // before truncation; a contradicted pose is only a manual reference.
                .OrderBy(candidate => candidate.IdentityEvidence.State switch
                {
                    ScanIdentityState.Supported => 0,
                    ScanIdentityState.Unverified => 1,
                    _ => 2
                })
                .ThenBy(candidate => double.IsFinite(ScanIdentityVerifier.FitCost(candidate.IdentityEvidence))
                    ? ScanIdentityVerifier.FitCost(candidate.IdentityEvidence) : double.PositiveInfinity)
                .ThenByDescending(candidate => double.IsFinite(candidate.MatchScore)
                    ? candidate.MatchScore : double.NegativeInfinity)
                .ThenBy(candidate => candidate.Map.SequenceNumber)
                .ThenBy(candidate => candidate.Map.Id)
                .Take(Math.Max(1, maximum)).ToArray();
        return references
        // All seeds have already been anchored to a detected gate. Its small residual
        // is a search constraint, not cross-map identity evidence. Rank before Take.
        .OrderByDescending(candidate => double.IsFinite(candidate.MatchScore)
            ? candidate.MatchScore : double.NegativeInfinity)
        .Take(Math.Max(1, maximum))
        .ToArray();
    }

    internal sealed record MapPreviewPlan(
        MapNormalizedPoint Center,
        double Zoom,
        double TargetX,
        double TargetY,
        bool IsSecondaryFloor);

    internal static IReadOnlyList<MapRecognitionChoice> AppendCatalogMaps(
        IReadOnlyList<MapRecognitionChoice> orderedCandidates,
        IEnumerable<MapRecord> maps,
        string mapClass,
        Func<MapRecord, string, string> overlayPathResolver)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapClass);
        ArgumentNullException.ThrowIfNull(overlayPathResolver);

        var result = new List<MapRecognitionChoice>(orderedCandidates.Count);
        var includedMapIds = new HashSet<Guid>();
        foreach (var candidate in orderedCandidates)
        {
            if (!string.Equals(
                    candidate.Recognition.Map.Class,
                    mapClass,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (includedMapIds.Add(candidate.Recognition.Map.Id))
                result.Add(candidate);
        }

        var catalogMaps = maps
            .Where(map => string.Equals(
                map.Class,
                mapClass,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(map => map.SequenceNumber)
            .ThenBy(map => map.Id);
        foreach (var map in catalogMaps)
        {
            if (!includedMapIds.Add(map.Id))
                continue;
            var floorKey = MapScanFloorRules.ResolveScanFloorKey(map);
            result.Add(new MapRecognitionChoice
            {
                Recognition = new RuntimeMapRecognition
                {
                    Map = map,
                    FloorImagePath = overlayPathResolver(map, floorKey),
                    Result = new MapRecognitionResult
                    {
                        MapId = map.Id,
                        Floor = floorKey,
                        Confidence = 0d,
                        IdentityConfidence = 0d,
                        LocalizationConfidence = 0d,
                        Source = MapRecognitionSource.Automatic
                    }
                },
                IsReferenceOnly = true,
                EvidenceLabel = "未进入本次识别候选",
                PreferredOrder = int.MaxValue
            });
        }

        return result;
    }

    internal static MapNormalizedPoint? ResolveMapSideEntranceCenter(MapRecord map)
    {
        var floorKey = MapScanFloorRules.ResolveScanFloorKey(map);
        return ResolveMapScanFeatureCenter(map, floorKey);
    }

    internal static MapPreviewPlan? ResolveMapPreviewPlan(
        MapRecord map,
        string floorKey)
    {
        var center = ResolveMapScanFeatureCenter(map, floorKey);
        if (center is null)
            return null;
        if (MapScanFloorRules.IsPrimaryFloor(map, floorKey))
        {
            return new MapPreviewPlan(
                center.Value,
                MapPreviewZoom,
                ResolveSafePreviewTarget(center.Value.X),
                ResolveSafePreviewTarget(center.Value.Y),
                false);
        }

        var zoom = SecondaryFloorMapPreviewZoom;
        return new MapPreviewPlan(
            center.Value,
            zoom,
            ResolveSafePreviewTarget(center.Value.X),
            ResolveSafePreviewTarget(center.Value.Y),
            true);
    }

    internal static string ResolveFloorDisplayName(
        MapRecord map,
        string floorKey) =>
        MapFloorRules.GetOrderedFloors(map)
            .FirstOrDefault(floor => string.Equals(
                floor.Key,
                floorKey,
                StringComparison.OrdinalIgnoreCase))
            ?.DisplayName
        ?? floorKey;

    private static MapNormalizedPoint? ResolveMapScanFeatureCenter(
        MapRecord map,
        string floorKey)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        var bounds = MapScanFloorRules.GetScanFeatureAnchor(map, floorKey)?.Bounds;
        if (bounds?.IsValid is true)
        {
            return new MapNormalizedPoint(
                Math.Clamp(bounds.X + (bounds.Width / 2d), 0d, 1d),
                Math.Clamp(bounds.Y + (bounds.Height / 2d), 0d, 1d));
        }

        if (profile is { RecognitionPixelWidth: > 0, RecognitionPixelHeight: > 0 }
            && double.IsFinite(profile.SideEntranceFeatureCenterX)
            && double.IsFinite(profile.SideEntranceFeatureCenterY)
            && profile.SideEntranceFeatureCenterX > 0d
            && profile.SideEntranceFeatureCenterY > 0d)
        {
            return new MapNormalizedPoint(
                Math.Clamp(
                    profile.SideEntranceFeatureCenterX / profile.RecognitionPixelWidth,
                    0d,
                    1d),
                Math.Clamp(
                    profile.SideEntranceFeatureCenterY / profile.RecognitionPixelHeight,
                    0d,
                    1d));
        }

        return null;
    }

    private static double ResolveSafePreviewTarget(double center) =>
        Math.Clamp(center, PreviewSafeInset, 1d - PreviewSafeInset);

    internal static double EstimateSourceCoverage(MapPreviewPlan plan)
    {
        static double AxisCoverage(double center, double zoom, double target)
        {
            var start = target - (center * zoom);
            var end = target + ((1d - center) * zoom);
            return Math.Max(0d, Math.Min(1d, end) - Math.Max(0d, start));
        }

        return AxisCoverage(plan.Center.X, plan.Zoom, plan.TargetX)
            * AxisCoverage(plan.Center.Y, plan.Zoom, plan.TargetY);
    }

    internal static MapScreenRect? ResolveLiveSideEntranceBounds(
        IEnumerable<MapRecognitionChoice> choices)
    {
        foreach (var choice in choices)
        {
            var map = choice.Recognition.Map;
            var anchor = MapScanFloorRules.GetScanFeatureAnchor(
                map,
                choice.Recognition.Result.Floor);
            if (anchor is null)
                continue;
            var match = choice.Recognition.Result.AnchorMatches
                .FirstOrDefault(item => item.AnchorId == anchor.Id);
            if (match?.ScreenBounds.IsValid is true)
                return match.ScreenBounds;
        }

        return null;
    }
}
