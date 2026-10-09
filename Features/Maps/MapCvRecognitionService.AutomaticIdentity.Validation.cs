namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private MapRecognitionAttempt ValidateAutomaticIdentityPose(
        CapturedGameFrame frame,
        AutomaticIdentityFloorWork item,
        double scale,
        double offsetX,
        double offsetY,
        double confidence,
        MapRecognitionTuning recognitionTuning,
        MapStructureRegistrationTuning validationTuning)
    {
        var prior = Math.Clamp(confidence, 0d, 1d);
        var key = (scale, offsetX, offsetY, prior);
        if (item.StrictPoseResults.TryGetValue(key, out var existing))
            return existing;
        var floor = item.Lease!.Floor;
        var orientation = MapFloorRules.GetFloorProfile(item.Map, item.FloorKey)
            ?.OrientationDegrees ?? 0;
        var transform = MapCanonicalTransformMath.BuildOverlayTransform(
            scale, scale, offsetX, offsetY,
            floor.ReferenceWidth, floor.ReferenceHeight,
            orientationDegrees: orientation,
            alignmentMode: MapOverlayAlignmentMode.Uniform);

        item.StrictExecutionCount++;
        var attempt = AlignFloorWithoutGates(
            frame, item.Map.Id, item.FloorKey, transform,
            MapOverlayAlignmentMode.Uniform, recognitionTuning, validationTuning,
            playerPrior: null, predictedViewportOrigin: null,
            liveIgnoreRegions: null, candidateHistory: null, isTracking: false,
            useProjectedBoundaryMask: false,
            scaleSearchPolicy: MapScaleSearchPolicy.Fixed,
            identityPriorConfidence: prior,
            allowPrimaryFloor: true);
        item.StrictExecutedResults.Add(attempt);
        if (IsCompletedAutomaticIdentityPoseValidation(attempt))
            item.StrictPoseResults.Add(key, attempt);
        return attempt;
    }

    private static void LogAutomaticIdentityRunnerUp(
        AutomaticIdentityFloorWork item,
        MapRecognitionAttempt attempt)
    {
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
            MapLogLevel.Info, "Automatic identity runner-up strict validation",
            details: new()
            {
                ["mapId"] = item.Map.Id,
                ["floor"] = item.FloorKey,
                ["accepted"] = attempt.StructureAccepted,
                ["rejection"] = attempt.StructureResult?.RejectionReason.ToString(),
                ["budgetTermination"] = attempt.StructureResult?
                    .LowStructureBudgetTerminationReason,
                ["failureReason"] = attempt.StructureFailureReason
            });
    }
}
