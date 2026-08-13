using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SideEntranceScanPipeline
{
    /// <summary>
    /// Runs the side-feature scan against every detected gate. Each retained
    /// match carries the gate that produced its constrained search. A map that
    /// has no valid gate association gets one full-frame template-only rescue.
    /// </summary>
    public IReadOnlyList<SideEntranceScanCandidate> RunScan(
        Mat capturedFrame,
        IReadOnlyList<(MapRecord map, string floorKey, Mat featureTemplate)> candidates,
        IReadOnlyList<GateDetection> detectedGates,
        int topK = 5,
        MapScreenRect? viewportBounds = null,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(capturedFrame);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(detectedGates);
        if (capturedFrame.Empty() || candidates.Count == 0)
            return [];
        topK = Math.Max(1, topK);

        using var maskedFrame = capturedFrame.Clone();
        MaskDetectedGates(maskedFrame, detectedGates, viewportBounds);

        // 每个门分支和最后的全帧补救分支各占一个真实工作单元。
        var totalBranches = Math.Max(1, detectedGates.Count + 1);

        var associated = new List<SideEntranceScanCandidate>();
        for (var gateIndex = 0; gateIndex < detectedGates.Count; gateIndex++)
        {
            var gate = detectedGates[gateIndex];
            if (!gate.ScreenBounds.IsValid)
                continue;
            MapLogCollector.Instance.Append(
                MapLogCategory.GateDetection,
                MapLogLevel.Info,
                $"side gate branch started | gate={gateIndex}",
                details: GateDetails(gate, gateIndex));
            var branch = RunSingleGateScan(
                maskedFrame,
                candidates,
                candidates.Count,
                gate,
                viewportBounds,
                maskDetectedGate: false,
                gateIndexForDiagnostics: gateIndex,
                progress: value => progress?.Invoke((gateIndex + value) / totalBranches));
            var returnedKeys = branch
                .Select(candidate => (candidate.Map.Id, candidate.FloorKey))
                .ToHashSet();
            foreach (var input in candidates.Where(item =>
                         !returnedKeys.Contains((item.map.Id, item.floorKey))))
            {
                var rejectedDetails = GateDetails(gate, gateIndex);
                rejectedDetails["mapId"] = input.map.Id;
                rejectedDetails["floor"] = input.floorKey;
                rejectedDetails["rejectionReason"] =
                    "no-eligible-gate-constrained-result";
                MapLogCollector.Instance.Append(
                    MapLogCategory.GateDetection,
                    MapLogLevel.Info,
                    $"side candidate/gate rejected | "
                    + $"map={input.map.SequenceNumber}#{input.floorKey} | "
                    + $"gate={gateIndex}",
                    details: rejectedDetails);
            }
            foreach (var candidate in branch)
            {
                candidate.AssociatedGate = gate;
                candidate.AssociatedGateIndex = gateIndex;
                candidate.GateAssociationKind =
                    SideEntranceGateAssociationKind.DetectedGate;
                associated.Add(candidate);
                MapLogCollector.Instance.Append(
                    MapLogCategory.GateDetection,
                    MapLogLevel.Info,
                    $"side candidate/gate associated | "
                    + $"map={candidate.Map.SequenceNumber}#{candidate.FloorKey} | "
                    + $"gate={gateIndex} | residual={candidate.GateSpatialResidualPixels:F1}px",
                    details: new()
                    {
                        ["mapId"] = candidate.Map.Id,
                        ["floor"] = candidate.FloorKey,
                        ["gateIndex"] = gateIndex,
                        ["gateScore"] = gate.Score,
                        ["templateSimilarity"] = candidate.MatchScore,
                        ["gateSpatialResidualPixels"] =
                            candidate.GateSpatialResidualPixels
                    });
            }
        }

        var results = associated
            .GroupBy(candidate => (candidate.Map.Id, candidate.FloorKey))
            .Select(group => group
                .OrderBy(candidate => candidate.GateSpatialResidualPixels)
                .ThenByDescending(candidate => candidate.MatchScore)
                .First())
            .ToList();

        var associatedKeys = results
            .Select(candidate => (candidate.Map.Id, candidate.FloorKey))
            .ToHashSet();
        var rescueInputs = candidates
            .Where(item => !associatedKeys.Contains((item.map.Id, item.floorKey)))
            .ToList();
        if (rescueInputs.Count > 0)
        {
            var rescued = RunSingleGateScan(
                maskedFrame,
                rescueInputs,
                rescueInputs.Count,
                detectedGate: null,
                viewportBounds,
                maskDetectedGate: false,
                gateIndexForDiagnostics: null,
                progress: value => progress?.Invoke((detectedGates.Count + value) / totalBranches));
            foreach (var candidate in rescued)
            {
                candidate.AssociatedGate = null;
                candidate.AssociatedGateIndex = -1;
                candidate.GateSpatialResidualPixels = double.PositiveInfinity;
                candidate.GateAssociationKind = detectedGates.Count > 0
                    ? SideEntranceGateAssociationKind.TemplateOnlyRescue
                    : SideEntranceGateAssociationKind.None;
                results.Add(candidate);
            }
        }

        // Independent gate branches cannot define each other's ranking. The
        // final margin is calculated only after duplicate maps are collapsed.
        results.Sort((left, right) => right.MatchScore.CompareTo(left.MatchScore));
        for (var index = 0; index < results.Count; index++)
        {
            var candidate = results[index];
            var previousGap = index > 0
                ? results[index - 1].MatchScore - candidate.MatchScore
                : double.PositiveInfinity;
            var nextGap = index + 1 < results.Count
                ? candidate.MatchScore - results[index + 1].MatchScore
                : double.PositiveInfinity;
            candidate.TemplateMargin = results.Count == 1
                ? candidate.MatchScore
                : Math.Min(previousGap, nextGap);
            candidate.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
            candidate.RejectionReason = SideEntranceRejectionReason.None;
            candidate.RejectionDetail = string.Empty;
            ClassifyTemplateEvidence(
                candidate,
                candidate.AssociatedGate,
                viewportBounds);
            MapLogCollector.Instance.Append(
                MapLogCategory.GateDetection,
                candidate.Disposition == SideEntranceCandidateDisposition.Rejected
                    ? MapLogLevel.Warning
                    : MapLogLevel.Info,
                $"side candidate finalized | "
                + $"map={candidate.Map.SequenceNumber}#{candidate.FloorKey} | "
                + $"association={candidate.GateAssociationKind} | "
                + $"reason={candidate.RejectionReason}",
                details: new()
                {
                    ["mapId"] = candidate.Map.Id,
                    ["floor"] = candidate.FloorKey,
                    ["gateIndex"] = candidate.AssociatedGateIndex,
                    ["gateAssociation"] =
                        candidate.GateAssociationKind.ToString(),
                    ["gateSpatialResidualPixels"] =
                        candidate.GateSpatialResidualPixels,
                    ["templateSimilarity"] = candidate.MatchScore,
                    ["templateMargin"] = candidate.TemplateMargin,
                    ["rejectionReason"] = candidate.RejectionReason.ToString()
                });
        }

        return results
            .Where(candidate => candidate.Disposition !=
                SideEntranceCandidateDisposition.Rejected)
            .Take(topK)
            .ToList();
    }

    internal static void MaskDetectedGates(
        Mat frame,
        IReadOnlyList<GateDetection> detectedGates,
        MapScreenRect? viewportBounds)
    {
        if (viewportBounds is not { IsValid: true } viewport
            || detectedGates.Count == 0)
        {
            return;
        }

        using var grayFrame = new Mat();
        if (frame.Channels() == 1)
            frame.CopyTo(grayFrame);
        else
            Cv2.CvtColor(frame, grayFrame, ColorConversionCodes.BGR2GRAY);
        var mean = Cv2.Mean(grayFrame);
        var bounds = new Rect(0, 0, frame.Width, frame.Height);
        foreach (var gate in detectedGates)
        {
            if (!gate.ScreenBounds.IsValid)
                continue;
            var local = new Rect(
                (int)Math.Floor(gate.ScreenBounds.X - viewport.X),
                (int)Math.Floor(gate.ScreenBounds.Y - viewport.Y),
                (int)Math.Ceiling(gate.ScreenBounds.Width),
                (int)Math.Ceiling(gate.ScreenBounds.Height))
                .Intersect(bounds);
            if (local.Width <= 0 || local.Height <= 0)
                continue;
            var fill = frame.Channels() == 1
                ? new Scalar(mean.Val0)
                : new Scalar(mean.Val0, mean.Val0, mean.Val0);
            Cv2.Rectangle(frame, local, fill, -1);
        }
    }

    private static Dictionary<string, object?> GateDetails(
        GateDetection gate,
        int gateIndex) => new()
    {
        ["gateIndex"] = gateIndex,
        ["gateScore"] = gate.Score,
        ["gateScale"] = gate.Scale,
        ["gateBounds"] =
            $"{gate.ScreenBounds.X:F1},{gate.ScreenBounds.Y:F1},"
            + $"{gate.ScreenBounds.Width:F1},{gate.ScreenBounds.Height:F1}"
    };
}
