using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    private static MapRecognitionAttempt AlignSelectedWithGatePair(
        MapCvRecognitionService service,
        CapturedGameFrame frame,
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession? compatibleSession,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        MapReferencePoint? playerPrior,
        MapViewportOrigin? predictedViewportOrigin,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions,
        List<Rect> dynamicIgnoreRegions,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory,
        double nativeScaleChangeRatio,
        MapScanDiagnostics diagnostics,
        Stopwatch stopwatch,
        GateDetectionResult gateResult,
        IReadOnlyList<GateDetection> gates)
    {
        stopwatch.Restart();
        var ranked = MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            gates,
            frame.ViewportBounds,
            tuning.VectorErrorTolerance);
        stopwatch.Stop();
        diagnostics.GeometryMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        if (!MapCvRecognitionDiagnostics.TryValidateRanking(
                ranked, tuning, diagnostics, out var failure))
        {
            if (tuning.ForceBestRecognitionResult
                && compatibleSession is not null)
            {
                return MapCvRecognitionBuilders.ReuseLastTransformAttempt(
                    fingerprint,
                    compatibleSession,
                    diagnostics);
            }

            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return failure!;
        }

        var winner = ranked[0];
        if (!MapCvRecognitionBuilders.TryBuildRecognition(
                winner,
                alignmentMode,
                tuning,
                margin: double.PositiveInfinity,
                usedConfirmation: false,
                MapRecognitionSource.SelectedMapGatePair,
                wasForcedBestResult: false,
                out var recognition,
                out var transformFailure))
        {
            if (tuning.ForceBestRecognitionResult
                && compatibleSession is not null)
            {
                return MapCvRecognitionBuilders.ReuseLastTransformAttempt(
                    fingerprint,
                    compatibleSession,
                    diagnostics);
            }

            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"双门与已选地图一致，但无法安全对齐覆盖层：{transformFailure}");
        }

        if (recognition!.Result.Confidence < tuning.MinimumConfidence
            && !tuning.ForceBestRecognitionResult)
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"已选地图的双门对齐置信度 {recognition.Result.Confidence:P0} "
                + $"低于阈值 {tuning.MinimumConfidence:P0}。");
        }

        if (compatibleSession is not null
            && recognition.Result.OverlayTransform is { } measured)
        {
            var scaleChange = Math.Abs(
                (measured.ScaleX
                    / compatibleSession.LockedTransform.ScaleX) - 1d);
            if (scaleChange > nativeScaleChangeRatio)
            {
                diagnostics.TrackingMode =
                    MapAlignmentTrackingMode.NeedsGatePair;
                diagnostics.StructureRejectionReason =
                    MapStructureRejectionReason.NativeScaleChanged;
                return MapCvRecognitionDiagnostics.Failure(
                    diagnostics,
                    $"双门测得的原生地图缩放与固定标定相差超过 "
                    + $"{nativeScaleChangeRatio:P0}，"
                    + "本次结果已拒绝，需要重新确认地图缩放。");
            }

            if (MapOverlayTransformSolver.TryTranslateWithLockedScale(
                    compatibleSession.LockedTransform,
                    recognition.Result.AnchorMatches,
                    out var fixedScaleTransform,
                    out _))
            {
                recognition = MapCvRecognitionBuilders.ReplaceTransform(
                    recognition,
                    fixedScaleTransform);
            }
        }

        if (MapCvRecognitionBuilders.CanDirectLockGatePair(recognition, tuning))
        {
            recognition = MapCvRecognitionBuilders.MarkFastEvidence(
                recognition,
                MapAlignmentEvidenceKind.DualGate,
                MapStructureEvidenceDisposition.None,
                skippedStructure: true);
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                "双门快速锁定，跳过结构复核");

            service.GateDetector.RememberSuccessfulScale(
                (winner.MainGate.Scale + winner.SideGate.Scale) / 2d);
            diagnostics.UsedForcedBestResult = false;
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.GatePairLocked;
            diagnostics.AlignmentEvidence =
                MapAlignmentEvidenceKind.DualGate;
            diagnostics.SkippedStructureValidation = true;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                Recognition = recognition,
                GateDetectionResult = gateResult,
                SearchStage = diagnostics.SearchStage,
            };
        }

        if (!MapCvAlignmentService.TryValidateAnchorRecognitionWithStructure(
                service,
                fingerprint,
                frame,
                recognition,
                structureTuning,
                tuning.MinimumConfidence,
                playerPrior,
                predictedViewportOrigin,
                liveIgnoreRegions,
                dynamicIgnoreRegions,
                candidateHistory,
                out var validatedRecognition,
                out var anchorStructure,
                out var structureFailure))
        {
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.NeedsGatePair;
            diagnostics.StructureRejectionReason =
                anchorStructure?.RejectionReason
                ?? MapStructureRejectionReason.NoCandidate;
            diagnostics.StructureDisposition =
                diagnostics.StructureRejectionReason.ToDisposition();
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"双门几何已匹配，但静态结构与地图边界复核失败：{structureFailure}");
        }

        recognition = validatedRecognition;

        if (anchorStructure is not null)
        {
            diagnostics.StructurePreprocessMilliseconds =
                anchorStructure.PreprocessMilliseconds;
            diagnostics.StructureSearchMilliseconds =
                anchorStructure.SearchMilliseconds;
            diagnostics.StructureRefineMilliseconds =
                anchorStructure.RefineMilliseconds;
            diagnostics.StructureBestScore = anchorStructure.BestScore;
            diagnostics.StructureSecondScore = anchorStructure.SecondScore;
            diagnostics.StructureCandidateMargin =
                anchorStructure.CandidateMargin;
            diagnostics.StructureRejectionReason =
                anchorStructure.RejectionReason;
            diagnostics.StructureDisposition =
                anchorStructure.RejectionReason.ToDisposition(
                    anchorStructure.Accepted);
            diagnostics.AlignmentEvidence =
                MapAlignmentEvidenceKind.Structure;
            PopulateStructureDiagnostics(
                diagnostics,
                anchorStructure);
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"结构复核：{(anchorStructure.Accepted ? "通过" : "未通过")} · 置信度 {anchorStructure.Confidence:P0}",
                elapsedMs: anchorStructure.SearchMilliseconds
                    + anchorStructure.RefineMilliseconds,
                details: new()
                {
                    ["accepted"] = anchorStructure.Accepted,
                    ["confidence"] = anchorStructure.Confidence,
                    ["bestScore"] = anchorStructure.BestScore,
                ["rejectionReason"] = anchorStructure.RejectionReason.ToString()
                });
        }

        service.GateDetector.RememberSuccessfulScale(
            (winner.MainGate.Scale + winner.SideGate.Scale) / 2d);
        diagnostics.UsedForcedBestResult =
            recognition.Result.WasForcedBestResult;
        diagnostics.TrackingMode = MapAlignmentTrackingMode.GatePairLocked;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            Recognition = recognition,
            GateDetectionResult = gateResult,
            SearchStage = diagnostics.SearchStage,
        };
    }
}
