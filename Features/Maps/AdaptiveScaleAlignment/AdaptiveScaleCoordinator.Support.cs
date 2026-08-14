namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal sealed partial class AdaptiveScaleCoordinator
{
    private void AddInitialObservations(
        AdaptiveScaleController controller,
        RuntimeMapRecognition recognition,
        MapOverlayTransform transform,
        AdaptiveScaleInitialEvidence evidence)
    {
        if (IsStrongStructure(recognition, evidence))
        {
            controller.ObserveAbsolute(new AdaptiveScaleObservation(
                evidence.FrameId,
                DateTimeOffset.UtcNow,
                UniformScale(transform),
                recognition.Result.LocalizationConfidence,
                recognition.Result.StructureCandidateMargin,
                AdaptiveScaleObservationSource.Structure,
                transform));
        }
        if (!IsStrongVpsg(evidence.Vpsg, transform))
            return;
        var vpsg = evidence.Vpsg!;
        controller.ObserveAbsolute(new AdaptiveScaleObservation(
            evidence.FrameId,
            DateTimeOffset.UtcNow,
            vpsg.Scale,
            vpsg.Confidence,
            recognition.Result.StructureCandidateMargin,
            AdaptiveScaleObservationSource.Vpsg,
            AdaptiveScaleTransformArbitrator.KeepScale(transform, vpsg.Scale)));
    }

    private AdaptiveScaleInitialStreakState GetInitialStreak(
        AdaptiveScaleKey key,
        AdaptiveScaleStoreEntry? persisted)
    {
        if (_initialStreaks.TryGetValue(key, out var streak))
            return streak;
        streak = new AdaptiveScaleInitialStreakState(key, _options, persisted);
        _initialStreaks[key] = streak;
        return streak;
    }

    private void QueueInitialStreakWrite(AdaptiveScaleInitialStreakSnapshot snapshot)
    {
        Task<AdaptiveScaleStoreEntry> task;
        lock (_pendingWrites)
        {
            task = PersistInitialStreakAfterAsync(_streakWriteTail, snapshot);
            _streakWriteTail = task;
            _pendingWrites.Add(task);
        }
        _ = task.ContinueWith(
            completed => CompleteInitialStreakWrite(snapshot.Key, completed),
            TaskScheduler.Default);
    }

    private async Task<AdaptiveScaleStoreEntry> PersistInitialStreakAfterAsync(
        Task predecessor,
        AdaptiveScaleInitialStreakSnapshot snapshot)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
        }
        catch
        {
            // A failed write must not prevent newer state from reaching disk.
        }
        return await _store.RecordInitialStreakAsync(snapshot).ConfigureAwait(false);
    }

    private void CompleteInitialStreakWrite(
        AdaptiveScaleKey key,
        Task<AdaptiveScaleStoreEntry> completed)
    {
        lock (_pendingWrites)
            _pendingWrites.Remove(completed);
        if (!completed.IsFaulted)
            return;
        _log?.Invoke(
            "adaptive initial streak persistence failed",
            new Dictionary<string, object?>
            {
                ["mapId"] = key.MapId,
                ["floor"] = key.FloorKey,
                ["exception"] = completed.Exception?.GetBaseException().ToString()
            });
    }

    private AdaptiveScaleController GetController(AdaptiveScaleKey key)
    {
        if (_controllers.TryGetValue(key, out var existing))
            return existing;
        var created = new AdaptiveScaleController(key, _options);
        _controllers[key] = created;
        return created;
    }

    private bool TryGetOpenController(
        AdaptiveScaleKey expectedKey,
        long openId,
        out AdaptiveScaleController controller)
    {
        if (_activeKey == expectedKey
            && _activeOpenId == openId
            && _controllers.TryGetValue(expectedKey, out controller!)
            && controller.IsOpen
            && controller.OpenId == openId)
        {
            return true;
        }
        controller = null!;
        return false;
    }

    private AdaptiveStructureDecision HoldRuntimeScale(
        RuntimeMapRecognition recognition,
        MapOverlayTransform transform,
        AdaptiveScaleController controller) =>
        new(
            MapCvRecognitionBuilders.ReplaceTransformAndSource(
                recognition,
                AdaptiveScaleTransformArbitrator.KeepScale(transform, controller.RuntimeScale),
                recognition.Result.Source),
            false,
            false,
            false,
            controller.State);

    private AdaptiveStructureDecision RejectStructureConsensusCore(
        AdaptiveScaleKey expectedKey,
        long openId,
        RuntimeMapRecognition recognition)
    {
        if (!TryGetOpenController(expectedKey, openId, out var controller)
            || recognition.Result.OverlayTransform is not { } transform)
        {
            return new AdaptiveStructureDecision(
                recognition, false, false, false, AdaptiveScaleState.Stable);
        }
        controller.RejectConsensus();
        return HoldRuntimeScale(recognition, transform, controller);
    }

    private bool IsStrongStructure(
        RuntimeMapRecognition recognition,
        AdaptiveScaleInitialEvidence evidence) =>
        evidence.StructureValidated
        && recognition.Result.EvidenceKind == MapAlignmentEvidenceKind.Structure
        && !recognition.Result.ReusedLastTransform
        && !recognition.Result.SkippedStructureValidation
        && recognition.Result.LocalizationConfidence >= _options.ReliableConfidence
        && recognition.Result.StructureRejectionReason == MapStructureRejectionReason.None
        && recognition.Result.StructureCandidateMargin >= evidence.RequiredCandidateMargin
        && recognition.Result.OverlayTransform is { AlignmentMode: MapOverlayAlignmentMode.Uniform };

    private bool IsUsableRecoveryStructure(
        RuntimeMapRecognition recognition,
        AdaptiveScaleInitialEvidence evidence) =>
        evidence.StructureValidated
        && recognition.Result.EvidenceKind == MapAlignmentEvidenceKind.Structure
        && !recognition.Result.ReusedLastTransform
        && !recognition.Result.SkippedStructureValidation
        && recognition.Result.LocalizationConfidence >= _options.RecoveryConfidence
        && recognition.Result.StructureRejectionReason == MapStructureRejectionReason.None
        && recognition.Result.StructureCandidateMargin >= evidence.RequiredCandidateMargin
        && recognition.Result.OverlayTransform is { AlignmentMode: MapOverlayAlignmentMode.Uniform };

    private bool IsStrongVpsg(
        AdaptiveVpsgEvidence? evidence,
        MapOverlayTransform transform) =>
        evidence is
        {
            Validated: true,
            UniqueMatches: >= MapVpsgScaleEstimator.MinimumUniqueMatches,
            PairVotes: >= MapVpsgScaleEstimator.MinimumPairVotes
        }
        && evidence.Confidence >= _options.VpsgConfidence
        && evidence.ResidualPixels <= MapVpsgScaleEstimator.MaximumResidualPixels
        && evidence.RelativeMad <= MapVpsgScaleEstimator.MaximumRelativeMad
        && RelativeDifference(evidence.Scale, UniformScale(transform))
            <= _options.FastConsensusRange;

    private static AdaptiveAlignmentDecision LegacyDecision(RuntimeMapRecognition recognition) =>
        new(
            recognition,
            AdaptiveScaleReliability.Reliable,
            true,
            true,
            true,
            true,
            "Disabled",
            0,
            0,
            AdaptiveScaleReliabilityReason.Disabled);

    private static double UniformScale(MapOverlayTransform transform) =>
        (transform.ScaleX + transform.ScaleY) / 2d;

    private static double RelativeDifference(double left, double right) =>
        Math.Abs(left - right) / Math.Max(Math.Abs(right), 0.000001d);
}
