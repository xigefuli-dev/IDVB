using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private static List<AutomaticIdentityPoseEvidence> ExcludeContradictedAutomaticPoses(
        Vpsg3LiveObservation observation, List<AutomaticIdentityPoseEvidence> evidence,
        double minimumWallSpan, AutomaticGeometryInput input, CancellationToken cancellationToken)
    {
        var remaining = new List<AutomaticIdentityPoseEvidence>();
        var diagnostics = new List<object>();
        foreach (var candidate in evidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                return evidence; // The caller reports timeout; partial exclusions cannot publish.
            var transform = candidate.Attempt.Recognition!.Result.OverlayTransform!;
            var gateState = AutomaticGateEvidence(observation, candidate.Owner, transform, input);
            var interior = CountAutomaticInteriorContradictions(observation, candidate.Owner,
                transform, minimumWallSpan, cancellationToken);
            var rejected = gateState == "mismatch" || interior.UsableComponents > 0;
            if (!rejected) remaining.Add(candidate);
            diagnostics.Add(new
            {
                candidate.Owner.Map.Id,
                candidate.Owner.FloorKey,
                Transform = transform,
                candidate.CanAccept,
                GateEvidence = gateState,
                InteriorContradictoryPoints = interior.ObservedPoints,
                InteriorGeometricPoints = interior.UsableObservedPoints,
                InteriorGeometricComponents = interior.UsableComponents,
                Rejected = rejected
            });
        }
        if (diagnostics.Count > 0)
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                "Automatic identity same-frame pose contradictions", details: new()
                {
                    ["gateSearchStopReason"] = input.GateDetection.StopReason.ToString(),
                    ["gateSearchBudgetExceeded"] = input.GateDetection.BudgetExceeded,
                    ["gateCount"] = input.Gates.Count,
                    ["gateScores"] = input.GateDetection.Gates.Select(gate => gate.Score).ToArray(),
                    ["visibleInteriorAvailable"] = observation.VisibleInterior is not null,
                    ["minimumWallSpanReferencePixels"] = minimumWallSpan,
                    ["poses"] = diagnostics,
                    ["remainingPoseCount"] = remaining.Count
                });
        return remaining;
    }

    private static string AutomaticGateEvidence(Vpsg3LiveObservation observation,
        AutomaticIdentityFloorWork item, MapOverlayTransform transform, AutomaticGeometryInput input)
    {
        if (input.GateDetection.BudgetExceeded
            || input.GateDetection.StopReason != GateSearchStopReason.Completed
            || input.GateDetection.SearchModeUsed != GateSearchMode.FullSearch
            || input.Gates.Count == 0)
            return "unavailable";
        // The detector has no entrance-role classifier. Missing a possible
        // role is unknown, never evidence that a detected door is impossible.
        string[] required = item.FloorKey.ToLowerInvariant() switch
        {
            "1f" => ["main-entrance", "side-entrance"],
            "2f" => ["second-floor-primary"],
            _ => []
        };
        var profile = MapFloorRules.GetFloorProfile(item.Map, item.FloorKey);
        if (required.Length == 0 || profile is null || required.Any(key =>
            !profile.Anchors.Any(anchor => anchor.Key == key && anchor.Bounds?.IsValid == true)))
            return "annotations-incomplete";
        if (transform.OrientationDegrees != 0
            || Math.Abs(transform.ScaleX - transform.ScaleY) > 1e-6)
            return "unsupported-transform";
        return MapLocalGeometrySolver.IsGatePoseCompatible(observation, item.Lease!.Floor,
            AutomaticLocalGeometryAnchors(item), input.Gates, transform.ScaleX,
            transform.OffsetX, transform.OffsetY) ? "compatible" : "mismatch";
    }

    private static AutomaticIdentityDistinctiveEvidence CountAutomaticInteriorContradictions(
        Vpsg3LiveObservation observation, AutomaticIdentityFloorWork item,
        MapOverlayTransform transform, double minimumSpan, CancellationToken cancellationToken)
    {
        var interior = observation.VisibleInterior;
        if (interior is null || interior.Empty() || transform.OrientationDegrees != 0
            || item.Lease is null || item.Lease.Floor.ReferenceEdgePoints.IsEmpty)
            return default;
        var floor = item.Lease.Floor;
        using var reference = new Mat(floor.ReferenceHeight, floor.ReferenceWidth,
            MatType.CV_8UC1, Scalar.Black);
        foreach (var point in floor.ReferenceEdgePoints.Span)
            reference.Set(point.Y, point.X, (byte)255);
        cancellationToken.ThrowIfCancellationRequested();
        using var affine = Mat.FromArray(new double[,]
        {
            { transform.ScaleX, 0, transform.OffsetX - observation.ViewportBounds.X },
            { 0, transform.ScaleY, transform.OffsetY - observation.ViewportBounds.Y }
        });
        using var projected = new Mat();
        Cv2.WarpAffine(reference, projected, affine, new Size(observation.Width, observation.Height),
            InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.Black);
        Cv2.BitwiseAnd(projected, interior, projected);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(projected, labels, stats, centroids,
            PixelConnectivity.Connectivity8);
        var allPoints = 0;
        var geometricPoints = 0;
        var geometricComponents = 0;
        for (var label = 1; label < count; label++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var points = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            allPoints += points;
            if (!HasMeasuredComponentSpan(labels, stats, label,
                1 / transform.ScaleX, 1 / transform.ScaleY, minimumSpan, cancellationToken)) continue;
            geometricComponents++;
            geometricPoints += points;
        }
        return new(allPoints, geometricPoints, geometricComponents);
    }
}
