using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private RuntimeMapRecognition? ProbeAdaptiveScaleStructure(
        OrbTrackingContext context,
        CapturedGameFrame frame,
        RuntimeMapRecognition predicted)
    {
        if (predicted.Result.OverlayTransform is not { } transform
            || _settings is null)
        {
            return null;
        }

        if (!IsAdaptiveScaleEnabled)
        {
            var legacyRadius = CreateEffectiveStructureTuning()
                .TrackingScaleSearchRadius;
            return RunAdaptiveFineScalePass(frame, predicted, legacyRadius)
                .Recognition;
        }

        var seed = predicted;
        if (AdaptiveScaleRequiresWideSearch(context))
        {
            var recoveryTuning = CreateEffectiveStructureTuning();
            recoveryTuning.ScaleSearchRadius = Math.Max(
                0.15d,
                recoveryTuning.ScaleSearchRadius);
            recoveryTuning.TrackingScaleSearchRadius = 0d;
            recoveryTuning.EnableFastAlignment = false;
            recoveryTuning.DisableScaleEarlyTermination = true;
            recoveryTuning.Normalize();
            var recovery = _recognition.AlignFloorWithoutGates(
                frame,
                predicted.Map.Id,
                predicted.Result.Floor,
                transform,
                MapOverlayAlignmentMode.Uniform,
                _settings.RecognitionTuning.Clone(),
                recoveryTuning,
                candidateHistory: null,
                isTracking: false,
                scaleSearchPolicy: MapScaleSearchPolicy.Search,
                identityPriorConfidence: predicted.Result.IdentityConfidence);
            if (recovery.Recognition is not { } recovered)
                return null;
            seed = recovered;
        }

        var probe = new AdaptiveScaleStructureProbe();
        var attempt = probe.Refine(
            seed,
            (passSeed, radius) => RunAdaptiveFineScalePass(
                frame,
                passSeed,
                radius));
        return attempt.Recognition;
    }

    private MapRecognitionAttempt RunAdaptiveFixedScaleTranslation(
        CapturedGameFrame frame,
        RuntimeMapRecognition latest,
        double consensusScale)
    {
        if (latest.Result.OverlayTransform is not { } transform)
            return new MapRecognitionAttempt { FailureReason = "missing-transform" };
        var fixedRecognition = MapCvRecognitionBuilders.ReplaceTransformAndSource(
            latest,
            AdaptiveScaleTransformArbitrator.KeepScale(transform, consensusScale),
            latest.Result.Source);
        var lockedSession = MapAlignmentSession.FromRecognition(
            fixedRecognition.Map,
            fixedRecognition.Result);
        return AlignNoDoorLocalStructure(
            frame,
            fixedRecognition,
            fixedRecognition.Result.Floor,
            lockedSession,
            MapOverlayAlignmentMode.Uniform,
            _settings!.RecognitionTuning.Clone(),
            CreateEffectiveStructureTuning(),
            [],
            fixedRecognition.Result.IdentityConfidence,
            allowTrackingScaleSearch: false);
    }

    private MapRecognitionAttempt RunAdaptiveFineScalePass(
        CapturedGameFrame frame,
        RuntimeMapRecognition seed,
        double radius)
    {
        var lockedSession = MapAlignmentSession.FromRecognition(
            seed.Map,
            seed.Result);
        var structureTuning = CreateEffectiveStructureTuning();
        structureTuning.TrackingScaleSearchRadius = radius;
        return AlignNoDoorLocalStructure(
            frame,
            seed,
            seed.Result.Floor,
            lockedSession,
            MapOverlayAlignmentMode.Uniform,
            _settings!.RecognitionTuning.Clone(),
            structureTuning,
            [],
            seed.Result.IdentityConfidence,
            allowTrackingScaleSearch: true);
    }
}
