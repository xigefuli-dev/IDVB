using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private AdaptiveScaleCoordinator _adaptiveScale = null!;
    private long _adaptiveFrameId;
    private AdaptiveScaleKey? _lastReliableAdaptiveKey;
    private AdaptiveScaleKey? _primaryFloorAdaptiveKey;

    private void InitializeAdaptiveScale()
    {
        var options = _config.Get<AdaptiveScaleOptions>("adaptive_scale");
        _adaptiveScale = new AdaptiveScaleCoordinator(
            options,
            log: (message, details) => _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                message,
                details: details));
    }

    private Task InitializeAdaptiveScaleAsync() =>
        _adaptiveScale.InitializeAsync(_lifetimeCts.Token);

    private Task<AdaptiveAlignmentDecision> EvaluateAdaptiveInitialAsync(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame,
        MapScanDiagnostics? diagnostics,
        MapFeatureCacheSource? explicitSource = null)
    {
        var source = explicitSource ?? ResolveLegacyScaleSource(recognition, frame);
        var evidence = CreateAdaptiveInitialEvidence(recognition, diagnostics);
        return Task.FromResult(_adaptiveScale.EvaluateInitial(
            recognition,
            frame,
            source,
            evidence,
            _gameMapToggleState.Version));
    }

    private AdaptiveScaleInitialEvidence CreateAdaptiveInitialEvidence(
        RuntimeMapRecognition recognition,
        MapScanDiagnostics? diagnostics)
    {
        AdaptiveVpsgEvidence? vpsg = null;
        if (diagnostics is { ScaleBootstrapValidated: true })
        {
            vpsg = new AdaptiveVpsgEvidence(
                diagnostics.ScaleBootstrapValidated,
                diagnostics.ScaleBootstrapScale,
                diagnostics.ScaleBootstrapConfidence,
                diagnostics.ScaleBootstrapUniqueMatches,
                diagnostics.ScaleBootstrapPairVotes,
                diagnostics.ScaleBootstrapResidualPixels,
                diagnostics.ScaleBootstrapRelativeMad);
        }
        return new AdaptiveScaleInitialEvidence(
            Interlocked.Increment(ref _adaptiveFrameId),
            CreateEffectiveStructureTuning().MinimumCandidateMargin,
            recognition.Result.EvidenceKind == MapAlignmentEvidenceKind.Structure
                && !recognition.Result.ReusedLastTransform
                && !recognition.Result.SkippedStructureValidation
                && recognition.Result.StructureRejectionReason
                    == MapStructureRejectionReason.None
                && (diagnostics is null
                    || (diagnostics.StructureAccepted
                        && string.IsNullOrWhiteSpace(
                            diagnostics.StructureHardGateFailure))),
            vpsg);
    }

    private MapFeatureCacheSource? ResolveLegacyScaleSource(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame)
    {
        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
            return null;
        var key = MapFeatureCacheRules.CreateKey(
            recognition.Map,
            recognition.Result.Floor,
            resolution);
        return _mapFeatureCacheRepository.TryGet(key, out var entry)
            ? entry?.Scale.Source
            : null;
    }

    private bool CanUseAdaptiveReliableSession(
        MapAlignmentSession session,
        AdaptiveScaleKey key) =>
        _adaptiveScale.CanUseAsReliableSession(
            session,
            key,
            _gameMapToggleState.Version);

    private static AdaptiveScaleKey CreateAdaptiveScaleKey(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey) =>
        AdaptiveScaleKey.Create(
            map,
            floorKey,
            frame.ClientBounds,
            frame.ViewportBounds);

    private bool TryGetActiveAdaptiveKey(
        RuntimeMapRecognition recognition,
        out AdaptiveScaleKey key)
    {
        if (_adaptiveScale.TryGetActiveKey(out key)
            && key.Matches(recognition.Map, recognition.Result.Floor))
        {
            return true;
        }
        key = default;
        return false;
    }

    private void RememberAdaptiveReliableKey(
        RuntimeMapRecognition recognition,
        bool primary)
    {
        if (!TryGetActiveAdaptiveKey(recognition, out var key))
            return;
        _lastReliableAdaptiveKey = key;
        if (primary)
            _primaryFloorAdaptiveKey = key;
    }

    private void ClearAdaptiveSessionKeys()
    {
        _lastReliableAdaptiveKey = null;
        _primaryFloorAdaptiveKey = null;
    }

    private bool IsAdaptiveTransformConfirmed(
        OrbTrackingContext context,
        MapOverlayTransform transform) =>
        _adaptiveScale.IsConfirmedTransform(
            context.AdaptiveKey,
            context.Toggle.Version,
            transform);

    private bool TryLockCurrentAdaptiveScale(
        RuntimeMapRecognition recognition,
        double scale) =>
        TryGetActiveAdaptiveKey(recognition, out var key)
        && _adaptiveScale.TryLockCurrentScale(
            key,
            _gameMapToggleState.Version,
            scale);

    private bool IsAdaptiveScaleEnabled => _adaptiveScale.Enabled;

    private bool IsAdaptiveInitialScaleQualified(
        MapRecognitionAttempt? attempt,
        MapStructureRegistrationTuning structureTuning) =>
        attempt?.Recognition is { } recognition
        && _adaptiveScale.IsQualifiedInitialResult(
            recognition,
            structureTuning.MinimumCandidateMargin,
            attempt.StructureAccepted);

    private bool AdaptiveScaleRequiresWideSearch(OrbTrackingContext context) =>
        _adaptiveScale.RequiresWideScaleSearch(
            context.AdaptiveKey,
            context.Toggle.Version);

    private void NotifyAdaptiveStructureFailure(OrbTrackingContext context) =>
        _adaptiveScale.ObserveStructureFailure(
            context.AdaptiveKey,
            context.Toggle.Version);

    private int GetAdaptiveStructureProbeInterval(
        OrbTrackingContext context,
        int legacyMilliseconds) =>
        _adaptiveScale.GetStructureProbeInterval(
            context.AdaptiveKey,
            context.Toggle.Version,
            legacyMilliseconds);

    private AdaptiveOrbDecision EvaluateAdaptiveOrb(
        OrbTrackingContext context,
        MapOverlayTransform transform,
        double stepScale) =>
        _adaptiveScale.EvaluateOrbObservation(
            context.AdaptiveKey,
            context.Toggle.Version,
            transform,
            stepScale,
            DateTimeOffset.UtcNow);

    private AdaptiveStructureDecision EvaluateAdaptiveStructure(
        OrbTrackingContext context,
        CapturedGameFrame frame,
        RuntimeMapRecognition recognition,
        long frameId)
    {
        var margin = CreateEffectiveStructureTuning().MinimumCandidateMargin;
        var observed = _adaptiveScale.EvaluateStructureObservation(
            context.AdaptiveKey,
            context.Toggle.Version,
            recognition,
            frameId,
            margin);
        if (observed.PendingConsensus is not { } consensus)
            return observed;

        var finalAttempt = RunAdaptiveFixedScaleTranslation(
            frame,
            recognition,
            consensus.Scale);
        if (finalAttempt.Recognition is not { } finalized)
        {
            return _adaptiveScale.RejectStructureConsensus(
                context.AdaptiveKey,
                context.Toggle.Version,
                observed.Recognition);
        }
        return _adaptiveScale.CommitStructureConsensus(
            context.AdaptiveKey,
            context.Toggle.Version,
            finalized,
            consensus,
            margin);
    }

    private void EndAdaptiveMapOpen(string reason) =>
        _adaptiveScale.EndActiveOpen(reason);

    private void SuspendActiveAdaptiveFloor(string reason) =>
        _adaptiveScale.SuspendActiveFloor(
            _gameMapToggleState.Version,
            reason);

    private Task DrainAdaptiveScaleAsync() => _adaptiveScale.DrainAsync();
}
