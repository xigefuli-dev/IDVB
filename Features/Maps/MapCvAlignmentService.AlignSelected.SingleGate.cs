using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    private static MapRecognitionAttempt? TryPrepareSelectedSingleGate(
        MapCvRecognitionService service,
        CapturedGameFrame frame,
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        MapRecognitionTuning tuning,
        SelectedAlignmentRoute route,
        AlignmentSearchContext? searchCtx,
        Mat reference,
        IReadOnlyList<GateDetection> gates,
        MapScanDiagnostics diagnostics,
        Stopwatch stopwatch,
        out string? singleGateFallbackReason,
        out RuntimeMapRecognition? singleGateProposal,
        out MapOverlayTransform? freshAnchorTransform,
        out MapOverlayTransform structureSeed)
    {
        singleGateFallbackReason = null;
        singleGateProposal = null;
        freshAnchorTransform = null;
        structureSeed = session.LockedTransform;
        GateDetection? singleGate = gates.Count == 1 ? gates[0] : null;

        // The side-entrance route deliberately never promotes a frame with
        // two visible gates into a dual-gate alignment. Such a frame uses the
        // selected map's structure only; a single-gate proposal is allowed
        // only when this frame contains exactly one detectable gate.

        if (singleGate is { } gate)
        {
            if (session.GateTemplateScale is { } lockedGateScale
                && Math.Abs((gate.Scale / lockedGateScale) - 1d) > 0.12d)
            {
                if (route == SelectedAlignmentRoute.SideEntrance)
                {
                    // A side scan gate is a useful proposal only while its
                    // template scale agrees with the locked scan evidence.
                    // It must not block the independent structure-only path.
                    singleGate = null;
                    singleGateFallbackReason =
                        "侧门单门缩放与扫描证据不一致，转入仅结构配准";
                }
                else if (tuning.ForceBestRecognitionResult)
                {
                    return MapCvRecognitionBuilders.ReuseLastTransformAttempt(
                        fingerprint,
                        session,
                        diagnostics);
                }

                else
                {
                    diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
                    return MapCvRecognitionDiagnostics.Failure(
                        diagnostics,
                        "单门尺寸与已锁定缩放不一致，可能发生了地图缩放；请等待双门重新锁定。");
                }
            }

            if (singleGate is not null)
            {
                if (session.SideEntranceScanPriorConfidence > 0d)
                {
                    if (searchCtx?.UseInitialHighPrecisionRecovery == true)
                    {
                        // This is the same frame that produced the side-feature
                        // match. Its gate identity, scale and translation have
                        // already been measured together, so rerunning the
                        // generic single-gate classifier is redundant and can
                        // reject sparse but valid side-entrance screenshots.
                        singleGateProposal =
                            MapCvRecognitionBuilders.BuildTrackedRecognition(
                                fingerprint,
                                session.LockedTransform,
                                session.LockedGateEvidence,
                                MapRecognitionSource.SideEntranceSelection,
                                confidenceOverride:
                                    session.SideEntranceScanPriorConfidence,
                                evidenceKind: MapAlignmentEvidenceKind.None);
                        structureSeed = session.LockedTransform;
                    }
                    else
                    {
                    // A later frame may expose the other gate. Re-identify the
                    // single visible gate against the locked map; if it cannot be
                    // identified safely, continue with structure-only alignment.
                    var seProfile = MapFloorRules.GetFloorProfile(
                        fingerprint.Map,
                        fingerprint.FloorKey)
                        ?? fingerprint.Map.Recognition.FirstFloor;
                    var sideAnchorId = seProfile.FindAnchor("side-entrance")?.Id;
                    stopwatch.Restart();
                    var resolved = MapAnchorTracker.TryResolveSingleGate(
                        reference,
                        frame.Image,
                        fingerprint,
                        gate,
                        frame.ViewportBounds,
                        session.LockedTransform,
                        tuning.MinimumConfidence,
                        tuning.ConfirmationAdvantage,
                        out var evidence,
                        out var identityFailure);
                    stopwatch.Stop();
                    diagnostics.ConfirmationMilliseconds =
                        stopwatch.Elapsed.TotalMilliseconds;

                    if (!resolved || sideAnchorId is null
                        || evidence.AnchorId != sideAnchorId.Value)
                    {
                        singleGateFallbackReason = string.IsNullOrWhiteSpace(identityFailure)
                            ? "侧门链路无法确认当前单门身份，转入仅结构配准"
                            : $"侧门单门身份确认失败：{identityFailure}";
                    }
                    else if (!MapOverlayTransformSolver.TryTranslateWithLockedScale(
                                 session.LockedTransform,
                                 [evidence],
                                 out var seTransform,
                                 out var seTransformFailure))
                    {
                        singleGateFallbackReason =
                            $"侧门单门平移失败：{seTransformFailure}";
                    }
                    else
                    {
                        diagnostics.TrackingMode =
                            MapAlignmentTrackingMode.SingleGateTracking;
                        var scaleAgreement = MapAlignmentConfidence
                            .ComputeScaleAgreement(
                                gate.Scale,
                                session.GateTemplateScale
                                    ?? (GateTemplateRules.ReferenceScale
                                        * session.BaselineGateScale));
                        var singleGateConfidence = MapAlignmentConfidence
                            .ComputeSideEntranceSingleGateConfidence(
                                session.SideEntranceScanPriorConfidence,
                                evidence.Score,
                                scaleAgreement);
                        singleGateProposal =
                            MapCvRecognitionBuilders.BuildTrackedRecognition(
                                fingerprint,
                                seTransform,
                                [evidence],
                                MapRecognitionSource.SingleGateTracking,
                                confidenceOverride: singleGateConfidence,
                                evidenceKind: MapAlignmentEvidenceKind.None);
                        structureSeed = seTransform;
                        freshAnchorTransform = seTransform;
                    }
                    }
                }
                else
                {
                    stopwatch.Restart();
                    var resolved = MapAnchorTracker.TryResolveSingleGate(
                        reference,
                        frame.Image,
                        fingerprint,
                        gate,
                        frame.ViewportBounds,
                        session.LockedTransform,
                        tuning.MinimumConfidence,
                        tuning.ConfirmationAdvantage,
                        out var evidence,
                        out var identityFailure);
                    stopwatch.Stop();
                    diagnostics.ConfirmationMilliseconds =
                        stopwatch.Elapsed.TotalMilliseconds;
                    MapLogCollector.Instance.Append(
                        MapLogCategory.GateDetection,
                        MapLogLevel.Info,
                        $"单门身份识别{(resolved ? "成功" : "失败")} · {stopwatch.Elapsed.TotalMilliseconds:F0}ms",
                        elapsedMs: stopwatch.Elapsed.TotalMilliseconds);

                    if (!resolved)
                    {
                        singleGateFallbackReason = identityFailure;
                    }
                    else if (!MapOverlayTransformSolver.TryTranslateWithLockedScale(
                                 session.LockedTransform,
                                 [evidence],
                                 out var transform,
                                 out var transformFailure))
                    {
                        singleGateFallbackReason = transformFailure;
                    }
                    else
                    {
                        diagnostics.TrackingMode =
                            MapAlignmentTrackingMode.SingleGateTracking;

                        var scaleAgreement = MapAlignmentConfidence.ComputeScaleAgreement(
                            gate.Scale,
                            session.GateTemplateScale ?? session.BaselineGateScale);
                        var singleGateConfidence = MapAlignmentConfidence
                            .ComputeSingleGateTrackingConfidence(
                                evidence.Score,
                                session.LastConfidence,
                                scaleAgreement);

                        singleGateProposal =
                            MapCvRecognitionBuilders.BuildTrackedRecognition(
                                fingerprint,
                                transform,
                                [evidence],
                                MapRecognitionSource.SingleGateTracking,
                                confidenceOverride: singleGateConfidence,
                                evidenceKind: MapAlignmentEvidenceKind.None);
                        structureSeed = transform;
                        freshAnchorTransform = transform;
                    }
                }
            }

            diagnostics.UsedSingleGateStructureFallback =
                singleGateProposal is null;
            diagnostics.SingleGateFallbackReason =
                singleGateFallbackReason ?? string.Empty;
        }

        return null;
    }
}
