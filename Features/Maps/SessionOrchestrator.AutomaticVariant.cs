using System.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    // Runs under the existing map-open gate, on its already captured frame.
    // False leaves the ordinary locked-map alignment in charge of that frame.
    private async Task<bool> TryResolveAutomaticVariantAsync(
        CapturedGameFrame frame, RuntimeMapRecognition locked,
        MapGameToggleTransition toggle, MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        var lease = _mapLease.VariantIdentity;
        if (lease is null || lease.ConfirmedMemberId is not null) return false;
        var requestedFloor = _currentFloorKey;
        bool IsCurrent() => !cancellationToken.IsCancellationRequested
            && IsCurrentMatchOperation(operationMatch)
            && _gameMapToggleState.IsCurrent(toggle)
            && _mapLease.IsCurrent(operationMatch, locked.Map.Id)
            && ReferenceEquals(_mapLease.VariantIdentity, lease)
            && string.Equals(_currentFloorKey, requestedFloor,
                StringComparison.OrdinalIgnoreCase);
        bool Superseded()
        {
            ActiveOperationTrace?.SetTerminal("superseded", "automatic-variant-context-changed");
            return true;
        }

        await EnsureMapCacheSynchronizedAsync();
        var catalog = await _mapRepository.GetCatalogSnapshotAsync();
        if (!IsCurrent()) return Superseded();
        var configured = catalog.VariantGroups.SingleOrDefault(g => g.Id == lease.GroupId);
        if (configured is null || !lease.MapIds.ToHashSet().SetEquals(configured.MapIds)
            || !string.Equals(configured.Class, operationMatch.MapClass,
                StringComparison.OrdinalIgnoreCase))
            return false;

        var currentGroup = lease with { CatalogRevision = catalog.Revision };
        var tuning = CreateInitialAlignmentRecognitionTuning();
        tuning.ForceBestRecognitionResult = false;
        var validation = MapScaleSeedResolver.CreateStrictInitialIdentityValidationTuning(
            CreateInitialAlignmentStructureTuning());
        MapAutomaticIdentityAttempt attempt;
        using (ActiveOperationTrace?.StartTopLevel("variant_identity_compute",
            MapOperationWaitKind.Compute, route: "automatic-variant"))
        {
            attempt = await Task.Run(() =>
            {
                var timer = Stopwatch.StartNew();
                var budget = Math.Min(_settings!.SessionTuning.OpeningTimeoutMilliseconds,
                    MapNoDoorAlignmentBudgetContext.RemainingMilliseconds ?? int.MaxValue);
                using var scope = MapNoDoorAlignmentBudgetContext.Enter(() =>
                    budget - (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds));
                return _recognition.RecognizeAutomaticIdentity(frame,
                    operationMatch.MapClass, frame.DetectedFloorKey ?? requestedFloor,
                    tuning, validation, cancellationToken, currentGroup);
            }, cancellationToken);
        }
        if (!IsCurrent()) return Superseded();
        _logCollector.Append(MapLogCategory.Session, MapLogLevel.Info,
            "开图相似组自动判断完成", details: new()
            {
                ["groupId"] = lease.GroupId,
                ["previousMapId"] = locked.Map.Id,
                ["confirmedMemberId"] = attempt.ConfirmedVariantMemberId,
                ["candidateCount"] = attempt.CandidateCount,
                ["status"] = attempt.Status.ToString(),
                ["failureReason"] = attempt.FailureReason,
                ["captureTicks"] = frame.CaptureSystemRelativeTicks
            });
        if (!attempt.Accepted || attempt.ConfirmedVariantGroupId != lease.GroupId
            || attempt.ConfirmedVariantMemberId is not { } member
            || attempt.Recognition is not { } recognized
            || recognized.Map.Id != member || attempt.AlignmentAttempt is not { } alignment
            || attempt.CatalogRevision != _recognition.CatalogRevision
            || attempt.CatalogRevision != _mapRepository.GetCatalogRevision())
            return false;

        if (member == locked.Map.Id)
        {
            // Identity becomes unique; the existing pipeline still owns alignment.
            lock (_mapOpenCancellationOwner.SyncRoot)
                if (IsCurrent()) _mapLease.Bind(operationMatch, member, attempt);
            return false;
        }

        await DrainMapCacheWritesAsync();
        RuntimeMapRecognition identity;
        lock (_mapOpenCancellationOwner.SyncRoot)
        {
            if (!IsCurrent()
                || attempt.CatalogRevision != _mapRepository.GetCatalogRevision()) return Superseded();
            // The existing identity writer invalidates old consumers while keeping
            // this operation's token alive. Do not call the manual switch routine:
            // it drains map operations, including this very operation.
            InvalidateActiveMapOpenOperation("automatic-variant-committed", cancellationToken);
            CancelMapObservation(clearPreview: true);
            DiscardAutomaticMapCacheSamples("自动确认相似组成员，丢弃旧图缓存样本");
            _lowStructureRecoveryCursor.Reset();
            ResetVariantAlignmentState();
            identity = LockSelectedMapIdentity(recognized, frame, userConfirmed: false,
                continuingMapOpenOwner: cancellationToken,
                consumeAutomaticIdentity: true, automaticIdentity: attempt);
        }
        _lastDiagnostics = attempt.Diagnostics;
        ActiveOperationTrace?.SetContext(route: "automatic-variant",
            mapId: member.ToString("D"), floorKey: identity.Result.Floor);
        bool CanPublish() => !cancellationToken.IsCancellationRequested
            && IsCurrentMatchOperation(operationMatch) && _gameMapToggleState.IsCurrent(toggle)
            && _mapLease.IsCurrent(operationMatch, member)
            && (_pendingAlignmentIdentity ?? _lastRecognition)?.Map.Id == member
            && string.Equals(_currentFloorKey, identity.Result.Floor,
                StringComparison.OrdinalIgnoreCase)
            && attempt.CatalogRevision == _recognition.CatalogRevision
            && attempt.CatalogRevision == _mapRepository.GetCatalogRevision();
        RecordResearchAttempt(recognized.Map, recognized.Result.Floor, frame,
            alignment, "automatic-variant-current-frame");
        var outcome = await PublishMapOpenAlignmentResultAsync(toggle, operationMatch,
            frame, identity, identity.Result.Floor, recoveringSelectedIdentity: true,
            aligned: recognized, failureReason: alignment.FailureReason,
            repairCacheKey: null, resetRecoveredScaleState: false, canPublish: CanPublish);
        ActiveOperationTrace?.SetTerminal(
            outcome == MapOpenAlignmentPublishOutcome.Succeeded ? "success"
                : outcome == MapOpenAlignmentPublishOutcome.Superseded ? "superseded" : "failed",
            "automatic-variant-current-frame");
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
