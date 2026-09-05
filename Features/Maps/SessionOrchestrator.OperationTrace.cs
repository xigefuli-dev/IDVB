using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private static readonly string[] QuickScanTracePhases =
    [
        "route_prepare",
        "opening_animation_wait",
        "stable_viewport",
        "resolution_preset",
        "recognition_dispatch_wait",
        "initial_recognition",
        "candidate_selection_wait",
        "selected_candidate_alignment",
        "manual_floor_alignment",
        "adaptive_scale_evaluation",
        "persistence",
        "session_commit",
        "overlay_publish",
        "cleanup"
    ];

    private static readonly string[] AlignmentTracePhases =
    [
        "route_prepare",
        "opening_animation_wait",
        "stable_viewport",
        "alignment_dispatch_wait",
        "alignment_compute",
        "research_record",
        "result_publish",
        "session_commit",
        "overlay_publish",
        "tracking_start",
        "persistence",
        "cleanup"
    ];

    private static readonly string[] CandidateConfirmationTracePhases =
    [
        "route_prepare",
        "stable_viewport",
        "manual_capture",
        "manual_recognition",
        "candidate_selection_wait",
        "selected_candidate_alignment",
        "result_publish",
        "session_commit",
        "persistence",
        "overlay_publish",
        "tracking_start",
        "mini_map_publish",
        "cleanup"
    ];

    private MapOperationTrace? _activeOperationTrace;
    private MapOperationTraceSummary? _lastScanOperationTrace;
    private MapOperationTraceSummary? _lastAlignmentOperationTrace;
    private MapOperationTraceSummary? _lastCandidateOperationTrace;
    private int _operationPresentStartCount;

    public MapOperationTraceSummary? LastScanOperationTrace => _lastScanOperationTrace;
    public MapOperationTraceSummary? LastAlignmentOperationTrace => _lastAlignmentOperationTrace;
    public MapOperationTraceSummary? LastCandidateOperationTrace => _lastCandidateOperationTrace;

    private MapOperationTrace BeginMapOperationTrace(
        string operationType,
        IEnumerable<string> topLevelPhases,
        string? route = null,
        string? mapId = null,
        string? floorKey = null,
        int? attemptIndex = null)
    {
        var trace = new MapOperationTrace(
            operationType,
            topLevelPhases,
            route: route,
            mapId: mapId,
            floorKey: floorKey,
            attemptIndex: attemptIndex);
        _activeOperationTrace = trace;
        _operationPresentStartCount = _overlay.PresentCount;
        MapOperationTraceAmbient.SetCurrent(trace);
        return trace;
    }

    private void FinishMapOperationTrace(
        MapOperationTrace trace,
        bool isAlignment,
        string outcome,
        string terminalReason,
        string? route = null,
        string? mapId = null,
        string? floorKey = null,
        int? attemptIndex = null)
    {
        var summary = trace.Complete(
            outcome,
            terminalReason,
            route,
            mapId,
            floorKey,
            attemptIndex);
        if (ReferenceEquals(_activeOperationTrace, trace))
        {
            _activeOperationTrace = null;
            MapOperationTraceAmbient.SetCurrent(null);
        }

        if (isAlignment)
        {
            _lastAlignmentOperationTrace = summary;
            _lastAlignmentPhaseTimings = summary.ToPhaseTimings();
            if (_lastDiagnostics is { } diagnostics)
            {
                diagnostics.OpeningAnimationWaitMilliseconds =
                    summary.GetTopLevelDurationMs("opening_animation_wait");
                diagnostics.StableViewportWaitMilliseconds =
                    summary.GetTopLevelDurationMs("stable_viewport");
                diagnostics.StableViewportAttempts = summary.Spans
                    .Where(static span => span.Name == "capture")
                    .Select(static span => span.AttemptIndex ?? 0)
                    .DefaultIfEmpty()
                    .Max();
                diagnostics.StableViewportSuccessfulCaptures = summary.Spans
                    .Count(static span => span.Name == "capture"
                        && span.Status == MapOperationSpanStatus.Completed);
                diagnostics.FloorQueueMilliseconds =
                    summary.GetChildDurationMs("floor_dispatch_wait");
                diagnostics.FloorWorkerMilliseconds =
                    summary.GetChildDurationMs("floor_worker_execution");
                diagnostics.FloorRequestMilliseconds =
                    diagnostics.FloorQueueMilliseconds
                    + diagnostics.FloorWorkerMilliseconds;
                diagnostics.FloorInputToResultMilliseconds =
                    summary.GetTopLevelDurationMs("manual_floor_alignment");
                diagnostics.AlignmentDispatchMilliseconds =
                    summary.GetTopLevelDurationMs("alignment_dispatch_wait");
                diagnostics.SessionCommitMilliseconds =
                    summary.GetTopLevelDurationMs("session_commit");
                diagnostics.OverlayMilliseconds =
                    summary.GetTopLevelDurationMs("overlay_publish");
                var finalPresentSpans = summary.Spans
                    .Where(static span => !span.IsTopLevel
                        && span.Name == "final_present")
                    .ToArray();
                diagnostics.FinalPresentMilliseconds = finalPresentSpans
                    .Sum(static span => span.DurationMs);
                diagnostics.InputToPresentMilliseconds = finalPresentSpans.Length > 0
                    ? finalPresentSpans.Max(static span =>
                        span.StartOffsetMs + span.DurationMs)
                    : summary.WallClockMs;
                diagnostics.PresentCount = Math.Max(
                    0,
                    _overlay.PresentCount - _operationPresentStartCount);
                diagnostics.AlignmentPipelineMilliseconds =
                    summary.GetTopLevelDurationMs("alignment_compute")
                    + summary.GetTopLevelDurationMs("research_record")
                    + summary.GetTopLevelDurationMs("result_publish")
                    + diagnostics.SessionCommitMilliseconds
                    + summary.GetTopLevelDurationMs("persistence")
                    + diagnostics.OverlayMilliseconds
                    + summary.GetTopLevelDurationMs("tracking_start")
                    + summary.GetTopLevelDurationMs("mini_map_publish");
                diagnostics.InputToLockedMilliseconds = summary.WallClockMs;
                diagnostics.FirstCandidateMilliseconds =
                    summary.GetTopLevelDurationMs("alignment_compute");
                diagnostics.AlignmentCaptureMilliseconds =
                    summary.GetChildDurationMs("capture");
                diagnostics.StableViewportCaptureMilliseconds =
                    diagnostics.AlignmentCaptureMilliseconds;
                diagnostics.ConfirmationComputeMilliseconds =
                    summary.GetChildDurationMs("candidate_confirmation")
                    + summary.GetChildDurationMs("candidate_confirmation_alignment");
            }
        }
        else if (string.Equals(
                     summary.OperationType,
                     MapOperationTypes.CandidateConfirmation,
                     StringComparison.Ordinal))
        {
            _lastCandidateOperationTrace = summary;
            if (_lastDiagnostics is { } diagnostics)
            {
                diagnostics.OpeningAnimationWaitMilliseconds =
                    summary.GetTopLevelDurationMs("opening_animation_wait");
                diagnostics.StableViewportWaitMilliseconds =
                    summary.GetTopLevelDurationMs("stable_viewport");
                diagnostics.StableViewportAttempts = summary.Spans
                    .Where(static span => span.Name == "capture")
                    .Select(static span => span.AttemptIndex ?? 0)
                    .DefaultIfEmpty()
                    .Max();
                diagnostics.StableViewportSuccessfulCaptures = summary.Spans
                    .Count(static span => span.Name == "capture"
                        && span.Status == MapOperationSpanStatus.Completed);
                diagnostics.ConfirmationDelayMilliseconds =
                    summary.Spans
                        .Where(static span => span.WaitKind == MapOperationWaitKind.Timer)
                        .Sum(static span => span.DurationMs);
                diagnostics.ConfirmationCaptureMilliseconds =
                    summary.GetChildDurationMs("capture");
                diagnostics.ConfirmationComputeMilliseconds =
                    summary.GetChildDurationMs("candidate_confirmation")
                    + summary.GetChildDurationMs("candidate_confirmation_alignment")
                    + summary.GetChildDurationMs("candidate_worker_execution");
                diagnostics.AlignmentDispatchMilliseconds =
                    summary.GetChildDurationMs("candidate_dispatch_wait");
                diagnostics.SessionCommitMilliseconds =
                    summary.GetTopLevelDurationMs("session_commit");
                diagnostics.OverlayMilliseconds =
                    summary.GetTopLevelDurationMs("overlay_publish");
                diagnostics.FirstCandidateMilliseconds =
                    summary.GetTopLevelDurationMs("selected_candidate_alignment");
                diagnostics.AlignmentPipelineMilliseconds =
                    diagnostics.FirstCandidateMilliseconds
                    + summary.GetTopLevelDurationMs("result_publish")
                    + diagnostics.SessionCommitMilliseconds
                    + summary.GetTopLevelDurationMs("persistence")
                    + diagnostics.OverlayMilliseconds
                    + summary.GetTopLevelDurationMs("tracking_start")
                    + summary.GetTopLevelDurationMs("mini_map_publish");
                diagnostics.InputToLockedMilliseconds = summary.WallClockMs;
            }
        }
        else
        {
            _lastScanOperationTrace = summary;
            _lastScanPhaseTimings = summary.ToPhaseTimings();
            if (_lastDiagnostics is { } diagnostics)
            {
                diagnostics.SessionCommitMilliseconds =
                    summary.GetTopLevelDurationMs("session_commit");
                diagnostics.OverlayMilliseconds =
                    summary.GetTopLevelDurationMs("overlay_publish");
                diagnostics.FirstCandidateMilliseconds =
                    summary.GetTopLevelDurationMs("selected_candidate_alignment");
                diagnostics.AlignmentDispatchMilliseconds =
                    summary.GetChildDurationMs("candidate_dispatch_wait");
                diagnostics.AlignmentCaptureMilliseconds =
                    summary.GetChildDurationMs("capture");
                diagnostics.InputToLockedMilliseconds = summary.WallClockMs;
                diagnostics.AlignmentPipelineMilliseconds =
                    diagnostics.FirstCandidateMilliseconds
                    + summary.GetTopLevelDurationMs("manual_floor_alignment")
                    + summary.GetTopLevelDurationMs("adaptive_scale_evaluation")
                    + summary.GetTopLevelDurationMs("persistence")
                    + diagnostics.SessionCommitMilliseconds
                    + diagnostics.OverlayMilliseconds;
            }
        }

        var level = summary.HasTopLevelOverlap
            ? MapLogLevel.Error
            : summary.ShouldWarnUnaccounted
                ? MapLogLevel.Warning
                : MapLogLevel.Info;
        var traceDetails = summary.ToDetails();
        if (isAlignment && _lastDiagnostics is { } alignmentDiagnostics)
        {
            traceDetails["alignmentClass"] = alignmentDiagnostics.AlignmentClass;
            traceDetails["alignmentContextKey"] = alignmentDiagnostics.AlignmentContextKey;
            traceDetails["alignmentChannel"] = alignmentDiagnostics.AlignmentChannel;
            traceDetails["floorMarkerKeys"] = alignmentDiagnostics.FloorMarkerKeys;
            traceDetails["alignmentConfigFingerprint"] =
                alignmentDiagnostics.AlignmentConfigFingerprint;
            traceDetails["warmStateHit"] = alignmentDiagnostics.WarmStateHit;
            traceDetails["warmStateMissReason"] = alignmentDiagnostics.WarmStateMissReason;
            traceDetails["stableViewportMode"] = alignmentDiagnostics.StableViewportMode;
            traceDetails["stableViewportFallback"] = alignmentDiagnostics.StableViewportFallback;
            traceDetails["inputToFirstCaptureMs"] =
                alignmentDiagnostics.InputToFirstCaptureMilliseconds;
            traceDetails["gameReadyDelayMs"] = alignmentDiagnostics.GameReadyDelayMilliseconds;
            traceDetails["preprocessMs"] = alignmentDiagnostics.StructurePreprocessMilliseconds;
            traceDetails["coarseGlobalMs"] = alignmentDiagnostics.CoarseGlobalMilliseconds;
            traceDetails["pyramidRefineMs"] = alignmentDiagnostics.PyramidRefineMilliseconds;
            traceDetails["exactEvaluateMs"] = alignmentDiagnostics.ExactEvaluateMilliseconds;
            traceDetails["stateCommitMs"] = alignmentDiagnostics.SessionCommitMilliseconds;
            traceDetails["finalPresentMs"] = alignmentDiagnostics.FinalPresentMilliseconds;
            traceDetails["inputToPresentMs"] = alignmentDiagnostics.InputToPresentMilliseconds;
            traceDetails["presentCount"] = alignmentDiagnostics.PresentCount;
            traceDetails["referenceDiskReadCount"] = alignmentDiagnostics.ReferenceDiskReadCount;
            traceDetails["fullResolutionTemplateMatchCount"] =
                alignmentDiagnostics.FullResolutionTemplateMatchCount;
            traceDetails["structurePreprocessCount"] =
                alignmentDiagnostics.StructurePreprocessCount;
            traceDetails["vpsgAttempted"] = alignmentDiagnostics.VpsgAttempted;
            traceDetails["scan_candidate_count"] =
                alignmentDiagnostics.ScanCandidateCount;
            traceDetails["scan_verification_candidate_count"] =
                alignmentDiagnostics.ScanVerificationCandidateCount;
            traceDetails["candidate_0_template_validation_ms"] =
                alignmentDiagnostics.ScanCandidate0TemplateValidationMilliseconds;
            traceDetails["candidate_0_vpsg_ms"] =
                alignmentDiagnostics.ScanCandidate0VpsgMilliseconds;
            traceDetails["candidate_0_structure_ms"] =
                alignmentDiagnostics.ScanCandidate0StructureMilliseconds;
            traceDetails["cheap_reject_count"] =
                alignmentDiagnostics.ScanCheapRejectCount;
            traceDetails["cheap_reject_ms"] =
                alignmentDiagnostics.ScanCheapRejectMilliseconds;
            traceDetails["scan_formal_structure_attempt_count"] =
                alignmentDiagnostics.ScanFormalStructureAttemptCount;
            traceDetails["shadow_pair_count"] =
                alignmentDiagnostics.ScanShadowPairCount;
            traceDetails["shadow_true_formal_false"] =
                alignmentDiagnostics.ScanShadowTrueFormalFalseCount;
            traceDetails["shadow_false_formal_true"] =
                alignmentDiagnostics.ScanShadowFalseFormalTrueCount;
            traceDetails["shadow_true_formal_true"] =
                alignmentDiagnostics.ScanShadowTrueFormalTrueCount;
            traceDetails["shadow_false_formal_false"] =
                alignmentDiagnostics.ScanShadowFalseFormalFalseCount;
            traceDetails["shadow_collection"] =
                alignmentDiagnostics.ScanShadowCollectionEnabled;
            traceDetails["effective_budget_ms"] =
                alignmentDiagnostics.ScanEffectiveBudgetMilliseconds;
            traceDetails["scan_total_verification_ms"] =
                alignmentDiagnostics.ScanTotalVerificationMilliseconds;
            traceDetails["scan_vpsg_attempt_count"] =
                alignmentDiagnostics.ScanVpsgAttemptCount;
            traceDetails["scan_full_recovery_count"] =
                alignmentDiagnostics.ScanFullRecoveryCount;
            traceDetails["gateDetectionAttempted"] =
                alignmentDiagnostics.GateDetectionAttempted;
            traceDetails["umatAttempted"] = alignmentDiagnostics.UmatAttempted;
            traceDetails["scaleHypothesisCount"] =
                alignmentDiagnostics.ScaleHypothesisCount;
        }
        _logCollector.Append(
            MapLogCategory.ScanLifecycle,
            level,
            $"操作时间轴完成 · op={summary.OperationId} · type={summary.OperationType} "
                + $"· outcome={summary.Outcome} · terminal={summary.TerminalReason}",
            elapsedMs: summary.WallClockMs,
            details: traceDetails);

        if (summary.HasTopLevelOverlap)
        {
            _logCollector.Append(
                MapLogCategory.ScanLifecycle,
                MapLogLevel.Error,
                $"操作时间轴出现顶层阶段重叠 · op={summary.OperationId}",
                elapsedMs: summary.OverlapMs,
                details: new()
                {
                    ["operationId"] = summary.OperationId,
                    ["overlapMs"] = summary.OverlapMs,
                    ["wallClockMs"] = summary.WallClockMs
                });
        }
        else if (summary.ShouldWarnUnaccounted)
        {
            _logCollector.Append(
                MapLogCategory.ScanLifecycle,
                MapLogLevel.Warning,
                $"操作时间轴存在未归账时间 · op={summary.OperationId} "
                    + $"· unaccounted={summary.UnaccountedMs:0.0}ms",
                elapsedMs: summary.UnaccountedMs,
                details: new()
                {
                    ["operationId"] = summary.OperationId,
                    ["unaccountedMs"] = summary.UnaccountedMs,
                    ["unaccountedRatio"] = summary.UnaccountedRatio,
                    ["thresholdMs"] = summary.UnaccountedThresholdMs,
                    ["longestSpan"] = summary.LongestSpanName,
                    ["longestSpanMs"] = summary.LongestSpanMs
                });
        }
    }

    private MapOperationTrace? ActiveOperationTrace => _activeOperationTrace;
}
