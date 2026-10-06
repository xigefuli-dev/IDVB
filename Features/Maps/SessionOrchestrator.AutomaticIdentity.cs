using System.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    // This branch is called only while the normal map-open wrapper owns
    // _scanGate. It owns the captured frame; the recognizer owns no session state.
    private async Task RunAutomaticMapOpenIdentityAsync(
        MapGameToggleTransition toggle,
        MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        var trace = ActiveOperationTrace;
        trace?.SetContext(route: "automatic-identity");
        var requestedFloor = _currentFloorKey;
        var readinessTimer = Stopwatch.StartNew();
        var readinessBudget = _settings!.SessionTuning.OpeningTimeoutMilliseconds;
        ClearPendingBackgroundScan();
        _lastDiagnostics = null;
        _statusMessage = "游戏地图已打开，正在自动识别地图……";
        StateChanged?.Invoke(this, EventArgs.Empty);

        bool IsCurrentUnidentifiedOperation() =>
            !cancellationToken.IsCancellationRequested
            && IsCurrentMatchOperation(operationMatch)
            && _gameMapToggleState.IsCurrent(toggle)
            && string.Equals(_currentFloorKey, requestedFloor,
                StringComparison.OrdinalIgnoreCase)
            && _lastRecognition is null
            && _pendingAlignmentIdentity is null;

        await EnsureMapCacheSynchronizedAsync();
        var identityFloorGroup = await ResolveAutomaticIdentityFloorGroupAsync(operationMatch.MapClass);
        while (IsCurrentUnidentifiedOperation())
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedGameFrame? captured;
            var captureTimer = Stopwatch.StartNew();
            var usedAcceptedJobReadiness = false;
            using (trace?.StartTopLevel("stable_viewport", MapOperationWaitKind.Capture))
            {
                captured = null;
                var readinessCandidate =
                    TryGetAcceptedAutomaticIdentityReadinessCandidate(
                        operationMatch, requestedFloor, identityFloorGroup);
                if (readinessCandidate is not null)
                {
                    captured = await CaptureAcceptedJobReadyAutomaticIdentityFrameAsync(
                        readinessCandidate, identityFloorGroup!, cancellationToken,
                        () => IsCurrentUnidentifiedOperation()
                            && IsAutomaticIdentityReadinessReferenceCurrent(
                                readinessCandidate, operationMatch, requestedFloor));

                    // A revoked operation is superseded, never an invitation
                    // to continue through the ordinary stable-capture path.
                    if (!IsCurrentUnidentifiedOperation())
                    {
                        captured?.Dispose();
                        trace?.SetTerminal("superseded", "automatic-identity-context-changed");
                        return;
                    }

                    if (captured is not null
                        && IsAcceptedAutomaticIdentityReadinessCandidateCurrent(
                            readinessCandidate, operationMatch, requestedFloor, captured))
                    {
                        usedAcceptedJobReadiness = true;
                    }
                    else
                    {
                        captured?.Dispose();
                        captured = null;
                    }
                }

                if (captured is null)
                {
                    if (!IsCurrentUnidentifiedOperation())
                    {
                        trace?.SetTerminal("superseded", "automatic-identity-context-changed");
                        return;
                    }
                    // A missing, stale, floor-mismatched, or not-ready job
                    // reference immediately returns to the existing generic
                    // presence and stable-frame acquisition path.
                    captured = await CaptureStableViewportAsync(
                        "自动识别", cancellationToken,
                        shouldContinue: IsCurrentUnidentifiedOperation,
                        useAutomaticViewport: true,
                        identityFloorGroup: identityFloorGroup,
                        acceptedJobReadinessFrame: currentFrame =>
                        {
                            if (!IsCurrentUnidentifiedOperation())
                                return null;
                            var currentCandidate =
                                TryGetAcceptedAutomaticIdentityReadinessCandidate(
                                    operationMatch, requestedFloor, identityFloorGroup);
                            if (currentCandidate is null)
                                return null;

                            var readinessFrame = TryCreateAcceptedJobReadinessFrame(
                                currentCandidate, operationMatch, requestedFloor,
                                currentFrame);
                            if (readinessFrame is null)
                                return null;
                            if (!IsCurrentUnidentifiedOperation()
                                || !IsAcceptedAutomaticIdentityReadinessCandidateCurrent(
                                    currentCandidate, operationMatch, requestedFloor,
                                    readinessFrame))
                            {
                                readinessFrame.Dispose();
                                return null;
                            }

                            usedAcceptedJobReadiness = true;
                            return readinessFrame;
                        });
                }
            }
            captureTimer.Stop();
            using var frame = captured;
            if (!IsCurrentUnidentifiedOperation())
            {
                trace?.SetTerminal("superseded", "automatic-identity-context-changed");
                return;
            }
            if (frame is null)
            {
                trace?.SetTerminal("failed", "automatic-identity-capture-failed");
                ReportCliCaptureFailure(_lastStableCaptureFailureReason
                    ?? "未取得就绪地图画面，下一次开图将自动重试。");
                return;
            }

            MapDiagnosticModeCapture.BeginMapOpen(frame.Image);
            var tuning = CreateInitialAlignmentRecognitionTuning();
            tuning.ForceBestRecognitionResult = false;
            var validationTuning = MapScaleSeedResolver
                .CreateStrictInitialIdentityValidationTuning(
                    CreateInitialAlignmentStructureTuning());
            AutomaticIdentityJob job;
            MapAutomaticIdentityAttempt attempt;
            try
            {
                using (trace?.StartTopLevel("alignment_compute", MapOperationWaitKind.Compute,
                    route: "automatic-identity"))
                {
                    var requireCurrentFrame = false;
                    do
                    {
                        job = GetOrStartAutomaticIdentityJob(frame, operationMatch,
                            frame.DetectedFloorKey ?? requestedFloor, tuning, validationTuning,
                            requireCurrentFrame);
                        // Close releases this consumer immediately. The job
                        // retains its own frame and match-owned lifetime, with
                        // explicit worker handoff guarding native recognition.
                        attempt = await job.Task.WaitAsync(cancellationToken);
                        requireCurrentFrame = !attempt.Accepted
                            && !ReferenceEquals(job.OriginFrame, frame);
                    }
                    while (requireCurrentFrame && IsCurrentUnidentifiedOperation());
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Catalog refresh or explicit invalidation revoked the frozen
                // job independently of the open-map publication token.
                trace?.SetTerminal("superseded", "automatic-identity-job-invalidated");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                ClearAutomaticIdentityJob();
                if (IsCurrentUnidentifiedOperation())
                {
                    trace?.SetTerminal("failed", $"automatic-identity-exception:{exception.GetType().Name}");
                    ReportCliGuardFailure($"自动识别暂未完成：{exception.Message}；后续开图将自动重试。",
                        MapLogCategory.Session);
                }
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentUnidentifiedOperation()
                || !ReferenceEquals(_automaticIdentityJob, job)
                || job.Generation != Volatile.Read(ref _automaticIdentityInvalidation)
                || attempt.CatalogRevision != _recognition.CatalogRevision
                || attempt.CatalogRevision != _mapRepository.GetCatalogRevision())
            {
                trace?.SetTerminal("superseded", "automatic-identity-context-changed");
                return;
            }

            _logCollector.Append(MapLogCategory.Session, MapLogLevel.Info,
                "开图自动身份识别完成", details: new()
                {
                    ["status"] = attempt.Status.ToString(),
                    ["candidateCount"] = attempt.CandidateCount,
                    ["comparedCount"] = attempt.ComparedCount,
                    ["confirmedVariantGroupId"] = attempt.ConfirmedVariantGroupId,
                    ["confirmedVariantMapIds"] = attempt.ConfirmedVariantMapIds,
                    ["geometryAttempted"] = attempt.CandidateDiagnostics.Any(candidate => candidate.GeometryAttempted),
                    ["geometryComparedCount"] = attempt.CandidateDiagnostics.Count(candidate => candidate.GeometryComparisonComplete),
                    ["failureReason"] = attempt.FailureReason,
                    ["unresolvedCandidates"] = attempt.CandidateDiagnostics
                        .Where(candidate => candidate.BlocksAcceptance)
                        .Select(candidate => new
                        {
                            candidate.MapId,
                            candidate.SequenceNumber,
                            candidate.FloorKey,
                            Status = candidate.Status.ToString(),
                            candidate.VpsgAccepted,
                            candidate.VpsgVerificationScore,
                            candidate.VpsgSpatialScore,
                            candidate.ProposalScale,
                            candidate.ProposalOffsetX,
                            candidate.ProposalOffsetY,
                            Rejection = candidate.StrictRejectionReason?.ToString(),
                            candidate.FailureReason
                        }).ToArray(),
                    ["toggleVersion"] = toggle.Version,
                    ["matchVersion"] = operationMatch.Version
                });

            _lastDiagnostics = attempt.Diagnostics;
            _lastDiagnostics.StableViewportWaitMilliseconds = captureTimer.Elapsed.TotalMilliseconds;
            _lastDiagnostics.StableViewportMode = usedAcceptedJobReadiness
                ? "automatic-identity-accepted-job-reference"
                : "automatic-identity-stable-frame";
            _lastDiagnostics.ScanCandidateCount = attempt.CandidateCount;
            _lastDiagnostics.ScanVerificationCandidateCount = attempt.ComparedCount;

            if (attempt.Accepted && attempt.Recognition is { } recognized
                && attempt.AlignmentAttempt is { } alignmentAttempt)
            {
                trace?.SetContext(route: "automatic-identity", mapId: recognized.Map.Id.ToString("D"),
                    floorKey: recognized.Result.Floor);
                if (!IsCurrentUnidentifiedOperation()
                    || job.Generation != Volatile.Read(ref _automaticIdentityInvalidation)
                    || attempt.CatalogRevision != _recognition.CatalogRevision
                    || attempt.CatalogRevision != _mapRepository.GetCatalogRevision())
                {
                    trace?.SetTerminal("superseded", "automatic-identity-context-changed");
                    return;
                }

                // Identity and display authorization use the existing writers.
                // LockSelectedMapIdentity deliberately strips the transform.
                _pendingAlignmentSeed = null;
                RuntimeMapRecognition identity;
                lock (_mapOpenCancellationOwner.SyncRoot)
                {
                    if (!IsCurrentUnidentifiedOperation()
                        || job.Generation != Volatile.Read(ref _automaticIdentityInvalidation))
                    {
                        trace?.SetTerminal("superseded", "automatic-identity-before-lock");
                        return;
                    }
                    identity = LockSelectedMapIdentity(recognized, frame,
                        userConfirmed: false, continuingMapOpenOwner: cancellationToken,
                        consumeAutomaticIdentity: true, automaticIdentity: attempt);
                }
                RuntimeMapRecognition? aligned = recognized;
                if (!ReferenceEquals(job.OriginFrame, frame))
                {
                    // The earlier frame proves identity only. Never publish
                    // its transform after reopening, moving or zooming.
                    using (trace?.StartTopLevel("current_frame_pose", MapOperationWaitKind.Compute,
                        route: "automatic-identity"))
                        alignmentAttempt = await Task.Run(() =>
                        {
                            var budget = Math.Min(_settings.SessionTuning.OpeningTimeoutMilliseconds,
                                MapNoDoorAlignmentBudgetContext.RemainingMilliseconds ?? int.MaxValue);
                            var timer = Stopwatch.StartNew();
                            using var scope = MapNoDoorAlignmentBudgetContext.Enter(() =>
                                budget - (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds));
                            return _recognition.AlignAutomaticIdentityCurrentFrame(frame,
                                identity.Map.Id, identity.Result.Floor,
                                tuning, validationTuning, cancellationToken);
                        }, cancellationToken);
                    aligned = alignmentAttempt.StructureAccepted
                        ? alignmentAttempt.Recognition : null;
                    _lastDiagnostics = alignmentAttempt.Diagnostics;
                }
                if (aligned is not null)
                    RecordResearchAttempt(aligned.Map, aligned.Result.Floor,
                        frame, alignmentAttempt, "automatic-map-open-identity");
                bool CanPublish() =>
                    !cancellationToken.IsCancellationRequested
                    && job.Generation == Volatile.Read(ref _automaticIdentityInvalidation)
                    && IsCurrentMatchOperation(operationMatch)
                    && _gameMapToggleState.IsCurrent(toggle)
                    && string.Equals(_currentFloorKey, identity.Result.Floor,
                        StringComparison.OrdinalIgnoreCase)
                    && (_pendingAlignmentIdentity ?? _lastRecognition)?.Map.Id == identity.Map.Id
                    && attempt.CatalogRevision == _recognition.CatalogRevision
                    && attempt.CatalogRevision == _mapRepository.GetCatalogRevision();

                var outcome = await PublishMapOpenAlignmentResultAsync(
                    toggle, operationMatch, frame, identity, identity.Result.Floor,
                    recoveringSelectedIdentity: true,
                    aligned: aligned,
                    failureReason: alignmentAttempt.FailureReason,
                    repairCacheKey: null,
                    resetRecoveredScaleState: false,
                    canPublish: CanPublish);
                if (outcome != MapOpenAlignmentPublishOutcome.Succeeded)
                    trace?.SetTerminal(
                        outcome == MapOpenAlignmentPublishOutcome.Superseded ? "superseded" : "failed",
                        "automatic-identity-pose-not-published");
                else if (attempt.ConfirmedVariantGroupId is not null && CanPublish())
                    _statusMessage = $"相似地图组已确认并对齐（{attempt.ConfirmedVariantMapIds.Count}张）；"
                        + $"当前显示：{identity.Map.DisplayName} · {identity.Result.Floor.ToUpperInvariant()}";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Index rebuilds are asynchronous and intentionally delayed at
            // startup. Retry readiness within the existing opening timeout;
            // every retry captures a fresh frame and retains cancellation.
            if (attempt.Status == MapAutomaticIdentityStatus.ResourcesPending
                && readinessTimer.ElapsedMilliseconds < readinessBudget)
            {
                ClearAutomaticIdentityJob();
                var remaining = readinessBudget - (int)readinessTimer.ElapsedMilliseconds;
                var delay = Math.Min(remaining,
                    Math.Max(50, _settings.SessionTuning.StableFrameIntervalMilliseconds));
                if (delay > 0)
                {
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
            }

            trace?.SetTerminal("failed", $"automatic-identity-{attempt.Status}");
            ClearAutomaticIdentityJob();
            _statusMessage = string.IsNullOrWhiteSpace(attempt.FailureReason)
                ? "当前画面不足以确认地图，下一次开图将自动重试。"
                : $"暂未确认地图：{attempt.FailureReason}；下一次开图将自动重试。";
            _overlay.ClearMap();
            _overlay.SetMainContentVisible(true);
            ShowTransientOverlayStatus(MapOverlayStatusLevel.Warning,
                "等待自动识别", _statusMessage,
                "继续正常探索；下次开图会自动重试。",
                frame.ClientBounds, frame.WindowHandle);
            _overlay.Show();
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        trace?.SetTerminal("superseded", "automatic-identity-context-changed");
    }
}
