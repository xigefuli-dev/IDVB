using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private CancellationTokenSource BeginQuickScanCancellationScope()
    {
        lock (_quickScanCancellationGate)
        {
            _quickScanCancellation?.Cancel();
            var scope = CancellationTokenSource.CreateLinkedTokenSource(CurrentMatchCancellationToken, ExternalOperationCancellation.Value);
            _quickScanCancellation = scope;
            return scope;
        }
    }

    private void CompleteQuickScanCancellationScope(CancellationTokenSource scope)
    {
        lock (_quickScanCancellationGate)
            if (ReferenceEquals(_quickScanCancellation, scope)) _quickScanCancellation = null;
        scope.Dispose();
    }

    private void CancelQuickScan()
    {
        lock (_quickScanCancellationGate) _quickScanCancellation?.Cancel();
    }

    private void FinishScanExecution(ScanExecutionContext execution)
    {
        if (execution.Reported) return;
        var stopReason = execution.ComputeStopReason;
        execution.CompleteAutomaticPhase();
        execution.Reported = true;
        var met = execution.ElapsedMilliseconds <= execution.Policy.BudgetMilliseconds;
        _logCollector.Append(MapLogCategory.ScanLifecycle, met ? MapLogLevel.Info : MapLogLevel.Warning,
            $"扫描自动阶段结束 · mode={execution.Policy.Mode} · elapsed={execution.ElapsedMilliseconds:F1}ms · budgetMet={met}",
            elapsedMs: execution.ElapsedMilliseconds,
            details: new()
            {
                ["scanId"] = execution.ScanId,
                ["phase"] = "automatic-phase-only",
                ["computeStopReason"] = stopReason,
                ["requestOutcome"] = ScanRequestDiagnostics.Current?.Outcome,
                ["requestReason"] = ScanRequestDiagnostics.Current?.Reason,
                ["evidenceFrameCreated"] = execution.Frame is not null,
                ["cancelled"] = execution.CancellationToken.IsCancellationRequested,
                ["superseded"] = execution.IsSuperseded,
                ["budgetMs"] = execution.Policy.BudgetMilliseconds,
                ["samplePoints"] = execution.Frame?.SearchPoints.Length ?? 0,
                ["densePoints"] = execution.Frame?.DensePoints.Length ?? 0,
                ["searchHypotheses"] = execution.TestedHypotheses,
                ["variantRefinements"] = execution.VariantRefinementCount,
                ["retrievalComplete"] = execution.RetrievalCompleted,
                ["comparedIdentities"] = execution.ComparedIdentityCount,
                ["verifiedCandidates"] = execution.VerifiedCandidateCount
            });
    }

    private bool TryCommitAutomaticScan(InitialRecognitionPipelineState result,
        CapturedGameFrame frame, MapMatchSnapshot match, CancellationToken cancellation)
    {
        var execution = ScanExecutionContext.Current;
        var recognition = result.Recognition;
        bool Reject(string reason)
        {
            _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning,
                $"扫描自动提交拒绝 · reason={reason}", details: new()
                {
                    ["reason"] = reason, ["scanId"] = execution?.ScanId,
                    ["mapId"] = recognition?.Map.Id, ["floor"] = recognition?.Result.Floor,
                    ["matchVersion"] = match.Version, ["currentMatchVersion"] = _matchSession.Snapshot.Version,
                    ["cancelled"] = cancellation.IsCancellationRequested,
                    ["superseded"] = execution?.IsSuperseded,
                    ["remainingMs"] = execution?.RemainingMilliseconds,
                    ["computeStopReason"] = execution?.ComputeStopReason
                });
            return false;
        }
        if (execution is null) return Reject("missing-execution");
        if (recognition?.Result.OverlayTransform is not { } transform) return Reject("missing-transform");
        if (execution.Frame is not { } observation) return Reject("missing-evidence-frame");
        if (execution.Expired) return Reject("execution-expired");
        if (!ReferenceEquals(observation.Source, frame.Image)) return Reject("evidence-frame-mismatch");
        if (cancellation.IsCancellationRequested) return Reject("cancelled");
        if (!IsCurrentMatchOperation(match)) return Reject("match-changed");
        if (!IsCurrentCaptureTarget(frame)) return Reject("capture-target-changed");
        if (execution.CatalogRevision?.Equals(_recognition.CatalogRevision) != true)
            return Reject("recognition-catalog-changed");
        if (execution.CatalogRevision.Equals(_mapRepository.GetCatalogRevision()) != true)
            return Reject("repository-catalog-changed");
        if (!string.Equals(recognition.Map.Class, match.MapClass, StringComparison.OrdinalIgnoreCase))
            return Reject("map-class-mismatch");
        var scan = result.PendingSideEntranceScan;
        var candidate = scan?.Candidates.FirstOrDefault(c => c.Map.Id == recognition.Map.Id
            && c.FloorKey == recognition.Result.Floor);
        if (candidate?.StructureIndex is not { } index
            || ScanIdentityVerifier.SelectIdentity(scan!.Candidates, execution.RetrievalCompleted
                && scan.Candidates.Count == scan.EligibleMapCount, execution.CanCompute,
                execution.VariantGroups, result.IdentitySelectionPolicy) != recognition.Map.Id)
            return Reject("identity-selection-not-confirmed");
        var final = ScanIdentityVerifier.Verify(observation, index, transform, frame.ViewportBounds, execution);
        if (final.State != ScanIdentityState.Supported || !execution.CanCompute
            || !IsCurrentMatchOperation(match) || cancellation.IsCancellationRequested)
            return Reject(final.State != ScanIdentityState.Supported ? "final-evidence:" + final.Reason
                : !execution.CanCompute ? "final-compute-unavailable" : "final-context-changed");

        // No await between this generation/deadline check and first publication. The UI thread
        // owns the transaction; manual selection and future opens use their independent paths.
        using var present = _overlay.DeferPresent();
        _provisionalRecognition = null;
        _provisionalCatalogRevision = null;
        _observationFrameCache.Reset();
        _observationPresentation.Reset();
        _overlay.SetObservationRegion(null);
        _overlayStatus.Clear();
        _mapOpenSession.LockAlignedMap(recognition.Map.Id, recognition.Result.Floor,
            MapSimilarityTransform.FromOverlay(transform), MapLocationMethod.StructureTranslation,
            recognition.Result.LocalizationConfidence);
        _lastRecognition = recognition;
        _currentFloorKey = recognition.Result.Floor;
        _mapLease.Bind(match, recognition.Map.Id);
        _pendingAlignmentIdentity = null;
        _pendingAlignmentSeed = null;
        _lastAlignmentSession = UpdateAlignmentSession(result.PendingSideEntranceSeed, recognition);
        RememberPrimaryFloorSession(recognition, _lastAlignmentSession);
        RememberReliableFloorAlignment(match, recognition, _lastAlignmentSession, frame, true);
        RememberMapViewportPresenceReference(recognition, frame);
        _lastGameBounds = frame.ClientBounds;
        _lastGameWindowHandle = frame.WindowHandle;
        _hasCompletedQuickScanAlignment = true;
        if (!_gameMapToggleState.IsOpen) _gameMapToggleState.MarkOpen();
        _overlay.UpdateMap(recognition, frame.ClientBounds, frame.WindowHandle, _settings!.ShowOverlayStatus);
        _overlay.Show();
        RefreshMiniMapForCurrentFloor();
        _statusMessage = $"已确认 {recognition.Map.DisplayName} · 结构支持 {final.SupportedFraction:P0}";
        _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
            $"扫描自动提交成功 · map={recognition.Map.SequenceNumber}#{recognition.Result.Floor}",
            details: new()
            {
                ["mapId"] = recognition.Map.Id,
                ["scanId"] = execution.ScanId,
                ["floor"] = recognition.Result.Floor,
                ["matchVersion"] = match.Version,
                ["identityLocked"] = true,
                ["alignmentCommitted"] = true,
                ["overlayVisible"] = _overlay.IsVisible,
                ["selectionPolicy"] = result.IdentitySelectionPolicy.ToString(),
                ["variantGroup"] = execution.VariantGroups.FirstOrDefault(g => g.Contains(recognition.Map.Id)),
                ["support"] = final.SupportedFraction,
                ["decisionElapsedMs"] = execution.ElapsedMilliseconds
            });
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task SetScanPerformanceModeAsync(ScanPerformanceMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var settings = _settings ?? throw new InvalidOperationException("设置尚未加载。");
        var saved = settings.Clone();
        saved.ScanPerformanceMode = mode;
        await _settingsRepo.SaveAsync(saved);
        settings.ScanPerformanceMode = mode;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
