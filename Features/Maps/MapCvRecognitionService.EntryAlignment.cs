using System.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal MapRecognitionAttempt AlignEntryFloor(CapturedGameFrame frame, Guid mapId, string floorKey,
        double identityPriorConfidence = 0, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(ReadyMapCount, TotalMapCount);
        diagnostics.StructureAttempted = true;
        diagnostics.ScaleBootstrapMode = "EntryGeometry";
        var revision = CatalogRevision;
        var index = _entryIdentityIndex;
        bool CanCompute() => !IsDisposed && !cancellationToken.IsCancellationRequested
            && (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is not { } left || left > 0)
            && ReferenceEquals(index, _entryIdentityIndex) && CatalogRevision.Equals(revision);
        MapRecognitionAttempt Reject(string reason)
        {
            diagnostics.TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
            diagnostics.StructureRejectionReason = MapStructureRejectionReason.NoCandidate;
            return MapCvRecognitionDiagnostics.Failure(diagnostics, reason);
        }

        var map = TryGetMap(mapId);
        var floor = map?.Floors.FirstOrDefault(item => item.Key == floorKey);
        if (map is null || floor?.EntryIdentityAsset is not { } asset || index is null)
            return Reject("入口对齐资源不可用。");
        if (frame.DetectedFloorKey is { } observedFloor
            && MapScanFloorRules.NormalizeFloorIdentity(observedFloor) != MapScanFloorRules.NormalizeFloorIdentity(floorKey))
            return Reject("当前帧楼层与目标楼层不一致，等待目标楼层的新画面。");
        if (!CanCompute() || !Repository.GetCatalogRevision().Equals(revision))
            return Reject("入口对齐已取消或地图库已变化。");

        // Fit this selected floor again from the new capture. Neither the
        // identity-pass pose nor another floor's scale is an alignment input.
        var decision = IdentifyEntryMap(frame, map.Class, floorKey, cancellationToken, CanCompute,
            selectedMapId: mapId);
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            $"已选地图入口对齐 · {decision.Reason}", elapsedMs: timer.Elapsed.TotalMilliseconds,
            details: new() { ["mapId"] = mapId, ["floor"] = floorKey,
                ["validationPolicy"] = decision.ValidationPolicy, ["pose"] = decision.SelectedPose,
                ["evidence"] = decision.Evidence });
        if (!CanCompute() || !Repository.GetCatalogRevision().Equals(revision)
            || decision.Map is not { } selected || selected.Id != mapId || selected.UpdatedAt != map.UpdatedAt
            || decision.FloorKey != floorKey || decision.SelectedPose is not { } pose)
            return Reject($"已锁定地图，等待本层入口对齐：{decision.Reason}");
        if (!double.IsFinite(pose.Scale) || pose.Scale <= 0 || !double.IsFinite(pose.Tx)
            || !double.IsFinite(pose.Ty) || !double.IsFinite(decision.Confidence))
            return Reject("入口对齐未产生有效的当前帧变换。");

        // Source poses use viewport pixels; the display transform uses physical
        // screen pixels. Apply the capture origin exactly once at this boundary.
        var transform = MapCanonicalTransformMath.BuildOverlayTransform(pose.Scale, pose.Scale,
            pose.Tx + frame.ViewportBounds.X, pose.Ty + frame.ViewportBounds.Y,
            asset.SourceWidth, asset.SourceHeight);
        var structure = new MapStructureRegistrationResult
        {
            Accepted = true, Transform = transform, Confidence = decision.Confidence,
            BestScore = 1 - decision.Confidence, SecondScore = double.PositiveInfinity,
            RejectionReason = MapStructureRejectionReason.None, LockedScale = pose.Scale,
            ReferenceWidth = asset.SourceWidth, ReferenceHeight = asset.SourceHeight,
            SearchMilliseconds = timer.Elapsed.TotalMilliseconds
        };
        var recognition = MapCvRecognitionBuilders.BuildFloorStructureRecognition(map, floorKey,
            Repository.GetFloorOverlayPath(map, floorKey), transform, structure, identityPriorConfidence);
        recognition.EntryCatalogRevision = revision;
        diagnostics.StructureAccepted = true;
        diagnostics.StructureRejectionReason = MapStructureRejectionReason.None;
        diagnostics.AlignmentEvidence = MapAlignmentEvidenceKind.Structure;
        diagnostics.TrackingMode = MapAlignmentTrackingMode.StructureMatched;
        diagnostics.ScaleBootstrapMethod = decision.ValidationPolicy ?? "entry-geometry";
        diagnostics.StructureBestScore = structure.BestScore;
        diagnostics.StructureSearchMilliseconds = timer.Elapsed.TotalMilliseconds;
        diagnostics.TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics, StructureResult = structure, Recognition = recognition,
            StructureAttempted = true, StructureAccepted = true,
            SearchStage = AlignmentSearchStage.StructureFallback
        };
    }
}
