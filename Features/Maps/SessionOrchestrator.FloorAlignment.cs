namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private static void CopyScaleBootstrapDiagnostics(
        MapScanDiagnostics source,
        MapScanDiagnostics destination)
    {
        destination.ScaleBootstrapAttempted = source.ScaleBootstrapAttempted;
        destination.ScaleBootstrapSucceeded = source.ScaleBootstrapSucceeded;
        destination.ScaleBootstrapValidated = source.ScaleBootstrapValidated;
        destination.ScaleBootstrapScale = source.ScaleBootstrapScale;
        destination.ScaleBootstrapConfidence = source.ScaleBootstrapConfidence;
        destination.ScaleBootstrapUniqueMatches =
            source.ScaleBootstrapUniqueMatches;
        destination.ScaleBootstrapPairVotes = source.ScaleBootstrapPairVotes;
        destination.ScaleBootstrapResidualPixels =
            source.ScaleBootstrapResidualPixels;
        destination.ScaleBootstrapRelativeMad =
            source.ScaleBootstrapRelativeMad;
    }

    private MapRecognitionAttempt AlignExactManualFloor(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence)
    {
        var featureAttempt = _recognition.AlignLockedFloorFeature(
            frame,
            locked.Map.Id,
            floorKey,
            scaleSeed,
            alignmentMode,
            tuning,
            structureTuning,
            identityPriorConfidence);
        if (featureAttempt.Recognition is not null)
            return featureAttempt;

        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(locked.Map);
        var hasFloorCalibration = _settings!.FloorScaleCalibrations
            .Any(calibration => calibration.Matches(
                locked.Map.Id,
                locked.Map.UpdatedAt,
                primaryFloorKey,
                floorKey));
        var radii = MapFloorScaleSearchPolicy.GetRadii(hasFloorCalibration);

        MapRecognitionAttempt SearchStructure(double radius)
        {
            var recoveryTuning = structureTuning.Clone();
            recoveryTuning.ScaleSearchRadius = Math.Max(
                recoveryTuning.ScaleSearchRadius,
                radius);
            var recovery = _recognition.AlignFloorWithoutGates(
                frame,
                locked.Map.Id,
                floorKey,
                scaleSeed,
                alignmentMode,
                tuning,
                recoveryTuning,
                isTracking: false,
                identityPriorConfidence: identityPriorConfidence);
            CopyScaleBootstrapDiagnostics(
                featureAttempt.Diagnostics,
                recovery.Diagnostics);
            return recovery;
        }

        var initialRecovery = SearchStructure(radii.InitialRadius);
        return initialRecovery.Recognition is not null
            ? initialRecovery
            : SearchStructure(radii.ExpandedRadius);
    }

    private async Task<(
        RuntimeMapRecognition? Recognition,
        string? FailureReason,
        MapScanDiagnostics? Diagnostics)> AlignQuickScanManualFloorAsync(
            CapturedGameFrame frame,
            RuntimeMapRecognition identityLock)
    {
        if (_currentFloorKey is not { } manualFloorKey
            || string.Equals(
                manualFloorKey,
                identityLock.Result.Floor,
                StringComparison.Ordinal)
            || MapFloorRules.GetFloorProfile(
                identityLock.Map,
                manualFloorKey) is null)
        {
            return (identityLock, null, null);
        }

        if (identityLock.Result.OverlayTransform is not { } identityTransform)
        {
            return (
                null,
                $"地图已锁定，但当前手动楼层 {manualFloorKey.ToUpperInvariant()} 缺少可用缩放种子。",
                null);
        }

        var scaleSeed = CreateCrossFloorScaleSeed(
            identityLock.Map,
            identityLock.Result.Floor,
            manualFloorKey,
            identityTransform);
        var recognitionTuning = CreateInitialAlignmentRecognitionTuning();
        var structureTuning = CreateInitialAlignmentStructureTuning();
        var attempt = await Task.Run(() => AlignExactManualFloor(
            frame,
            identityLock,
            manualFloorKey,
            scaleSeed,
            _settings!.OverlayAlignmentMode,
            recognitionTuning,
            structureTuning,
            0d));
        if (attempt.Recognition is not null)
            return (attempt.Recognition, null, attempt.Diagnostics);

        var floorLabel = MapFloorRules.GetFloorDisplayName(
            identityLock.Map,
            manualFloorKey);
        return (
            null,
            $"地图已锁定，但按当前手动楼层 {floorLabel} 对齐失败："
                + attempt.FailureReason,
            attempt.Diagnostics);
    }

    private static MapOverlayTransform CreateCrossFloorScaleSeed(
        MapRecord map,
        string sourceFloorKey,
        string targetFloorKey,
        MapOverlayTransform sourceTransform)
    {
        var sourceFloor = MapFloorRules.GetFloorProfile(map, sourceFloorKey);
        var targetFloor = MapFloorRules.GetFloorProfile(map, targetFloorKey);
        return sourceFloor is not null && targetFloor is not null
            ? MapFloorScaleSeedRules.RenormalizeTransformToFloor(
                sourceTransform,
                sourceFloor,
                targetFloor)
            : sourceTransform;
    }
}
