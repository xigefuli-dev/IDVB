using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapCvRecognitionBuilders
{
    internal static bool TryBuildRecognition(
        MapGeometryCandidate winner,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        double margin,
        bool usedConfirmation,
        MapRecognitionSource source,
        bool wasForcedBestResult,
        out RuntimeMapRecognition? recognition,
        out string failureReason)
    {
        recognition = null;
        if (!MapOverlayTransformSolver.TrySolve(
                winner,
                alignmentMode,
                out var transform,
                out failureReason))
        {
            return false;
        }

        var fingerprint = winner.Fingerprint;
        var map = fingerprint.Map;
        var profile = MapFloorRules.GetFloorProfile(map, fingerprint.FloorKey)
            ?? map.Recognition.FirstFloor;
        var mainAnchor = profile.FindAnchor("main-entrance")!;
        var sideAnchor = profile.FindAnchor("side-entrance")!;
        // Gate score is the primary confidence driver; geometry is a soft
        // secondary check (see MapAlignmentConfidence.ComputeDualGateConfidence).
        var confidence = MapAlignmentConfidence.ComputeDualGateConfidence(
            winner.MainGate.Score,
            winner.SideGate.Score,
            winner.VectorError,
            tuning.VectorErrorTolerance);
        recognition = new RuntimeMapRecognition
        {
            Map = map,
            FloorImagePath = fingerprint.OverlayImagePath,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = fingerprint.FloorKey,
                OrientationDegrees = 0,
                Confidence = confidence,
                IdentityConfidence = confidence,
                LocalizationConfidence = confidence,
                Source = source,
                HasAllRequiredAnchorEvidence = true,
                GeometryMargin = double.IsPositiveInfinity(margin) ? 1d : Math.Max(0d, margin),
                UsedLocalConfirmation = usedConfirmation,
                OverlayTransform = transform,
                WasForcedBestResult = wasForcedBestResult
                    || (tuning.ForceBestRecognitionResult
                        && confidence < tuning.MinimumConfidence),
                AnchorMatches =
                [
                    MapCvRecognitionHelpers.CreateEvidence(mainAnchor, winner.MainGate, fingerprint),
                    MapCvRecognitionHelpers.CreateEvidence(sideAnchor, winner.SideGate, fingerprint)
                ],
                EvidenceKind = MapAlignmentEvidenceKind.DualGate
            }
        };
        failureReason = string.Empty;
        return true;
    }

    internal static bool CanDirectLockGatePair(
        RuntimeMapRecognition recognition,
        MapRecognitionTuning tuning) =>
        MapFastAlignmentRules.CanDirectLockDualGate(
            recognition.Result,
            tuning);

    internal static bool TryBuildDirectAuxiliaryRecognition(
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        RuntimeMapRecognition? singleGateProposal,
        MapAuxiliaryTrackingResult auxiliary,
        MapScreenRect viewportBounds,
        double auxiliaryDirectLockConfidence,
        out RuntimeMapRecognition? recognition)
    {
        recognition = null;
        IReadOnlyList<CvAnchorEvidence> matches;
        MapAlignmentEvidenceKind evidenceKind;
        double confidence;
        if (auxiliary.HasIndependentConsensus
            && auxiliary.Confidence >= auxiliaryDirectLockConfidence)
        {
            matches = auxiliary.Matches;
            confidence = auxiliary.Confidence;
            evidenceKind = MapAlignmentEvidenceKind.AuxiliaryConsensus;
        }
        else if (singleGateProposal is not null
            && auxiliary.Matches.Count > 0)
        {
            matches = singleGateProposal.Result.AnchorMatches
                .Concat(auxiliary.Matches.Take(1))
                .DistinctBy(match => match.AnchorId)
                .ToArray();
            if (matches.Count < 2 || matches.Any(match => match.Score < 0.78d))
                return false;

            var referenceDiagonal = Math.Sqrt(
                (fingerprint.ReferenceWidth * fingerprint.ReferenceWidth)
                + (fingerprint.ReferenceHeight * fingerprint.ReferenceHeight));
            if (MapCvRecognitionHelpers.Distance(
                    new Point2d(
                        matches[0].ReferenceBounds.CenterX,
                        matches[0].ReferenceBounds.CenterY),
                    new Point2d(
                        matches[1].ReferenceBounds.CenterX,
                        matches[1].ReferenceBounds.CenterY))
                < referenceDiagonal * 0.05d)
            {
                return false;
            }

            confidence = Math.Clamp(
                matches.Average(match => match.Score),
                0d,
                1d);
            if (confidence < auxiliaryDirectLockConfidence)
                return false;

            evidenceKind =
                MapAlignmentEvidenceKind.SingleGateAndAuxiliary;
        }
        else
        {
            return false;
        }

        if (!MapOverlayTransformSolver.TryTranslateWithLockedScale(
                session.LockedTransform,
                matches,
                out var transform,
                out _))
        {
            return false;
        }

        var tolerance = Math.Max(
            6d,
            Math.Sqrt(
                (viewportBounds.Width * viewportBounds.Width)
                + (viewportBounds.Height * viewportBounds.Height))
            * 0.005d);
        if (transform.MaximumResidualPixels > tolerance)
            return false;

        recognition = BuildTrackedRecognition(
            fingerprint,
            transform,
            matches,
            MapRecognitionSource.AuxiliaryAnchorTracking,
            confidence,
            evidenceKind);
        return true;
    }

    internal static RuntimeMapRecognition MarkFastEvidence(
        RuntimeMapRecognition recognition,
        MapAlignmentEvidenceKind evidenceKind,
        MapStructureEvidenceDisposition structureDisposition,
        bool skippedStructure) =>
        new()
        {
            Map = recognition.Map,
            FloorImagePath = recognition.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = recognition.Result.MapId,
                Floor = recognition.Result.Floor,
                OrientationDegrees =
                    recognition.Result.OrientationDegrees,
                Confidence = recognition.Result.Confidence,
                IdentityConfidence =
                    recognition.Result.IdentityConfidence,
                LocalizationConfidence =
                    recognition.Result.LocalizationConfidence,
                Source = recognition.Result.Source,
                HasAllRequiredAnchorEvidence =
                    recognition.Result.HasAllRequiredAnchorEvidence,
                GeometryMargin = recognition.Result.GeometryMargin,
                UsedLocalConfirmation =
                    recognition.Result.UsedLocalConfirmation,
                OverlayTransform = recognition.Result.OverlayTransform,
                AnchorMatches = recognition.Result.AnchorMatches,
                StructureBestScore =
                    recognition.Result.StructureBestScore,
                StructureSecondScore =
                    recognition.Result.StructureSecondScore,
                StructureCandidateMargin =
                    recognition.Result.StructureCandidateMargin,
                StructureRejectionReason =
                    recognition.Result.StructureRejectionReason,
                WasForcedBestResult =
                    recognition.Result.WasForcedBestResult,
                ReusedLastTransform =
                    recognition.Result.ReusedLastTransform,
                EvidenceKind = evidenceKind,
                StructureDisposition = structureDisposition,
                SkippedStructureValidation = skippedStructure
            }
        };

    internal static RuntimeMapRecognition BuildTrackedRecognition(
        MapGeometryFingerprint fingerprint,
        MapOverlayTransform transform,
        IReadOnlyList<CvAnchorEvidence> matches,
        MapRecognitionSource source,
        double? confidenceOverride = null,
        MapAlignmentEvidenceKind evidenceKind =
            MapAlignmentEvidenceKind.None)
    {
        var confidence = confidenceOverride ?? (matches.Count == 0
            ? 0d
            : Math.Clamp(matches.Average(match => match.Score), 0d, 1d));
        return new RuntimeMapRecognition
        {
            Map = fingerprint.Map,
            FloorImagePath = fingerprint.OverlayImagePath,
            Result = new MapRecognitionResult
            {
                MapId = fingerprint.Map.Id,
                Floor = fingerprint.FloorKey,
                OrientationDegrees = 0,
                Confidence = confidence,
                IdentityConfidence = confidence,
                LocalizationConfidence = confidence,
                Source = source,
                HasAllRequiredAnchorEvidence = false,
                GeometryMargin = 0d,
                UsedLocalConfirmation = true,
                OverlayTransform = transform,
                AnchorMatches = matches,
                EvidenceKind = evidenceKind,
                StructureDisposition =
                    MapStructureEvidenceDisposition.None,
                SkippedStructureValidation = true
            }
        };
    }

    internal static RuntimeMapRecognition ReplaceTransform(
        RuntimeMapRecognition recognition,
        MapOverlayTransform transform) =>
        new()
        {
            Map = recognition.Map,
            FloorImagePath = recognition.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = recognition.Result.MapId,
                Floor = recognition.Result.Floor,
                OrientationDegrees = recognition.Result.OrientationDegrees,
                Confidence = recognition.Result.Confidence,
                IdentityConfidence =
                    recognition.Result.IdentityConfidence,
                LocalizationConfidence =
                    recognition.Result.LocalizationConfidence,
                Source = recognition.Result.Source,
                HasAllRequiredAnchorEvidence =
                    recognition.Result.HasAllRequiredAnchorEvidence,
                GeometryMargin = recognition.Result.GeometryMargin,
                UsedLocalConfirmation =
                    recognition.Result.UsedLocalConfirmation,
                OverlayTransform = transform,
                AnchorMatches = recognition.Result.AnchorMatches,
                StructureBestScore = recognition.Result.StructureBestScore,
                StructureSecondScore =
                    recognition.Result.StructureSecondScore,
                StructureCandidateMargin =
                    recognition.Result.StructureCandidateMargin,
                StructureRejectionReason =
                    recognition.Result.StructureRejectionReason,
                WasForcedBestResult =
                    recognition.Result.WasForcedBestResult,
                ReusedLastTransform =
                    recognition.Result.ReusedLastTransform,
                EvidenceKind = recognition.Result.EvidenceKind,
                StructureDisposition =
                    recognition.Result.StructureDisposition,
                SkippedStructureValidation =
                    recognition.Result.SkippedStructureValidation
            }
        };

    internal static Rect ToLocalRect(
        MapScreenRect screen,
        MapScreenRect viewport,
        Size imageSize)
    {
        var left = Math.Clamp(
            (int)Math.Floor(screen.X - viewport.X),
            0,
            Math.Max(0, imageSize.Width - 1));
        var top = Math.Clamp(
            (int)Math.Floor(screen.Y - viewport.Y),
            0,
            Math.Max(0, imageSize.Height - 1));
        var right = Math.Clamp(
            (int)Math.Ceiling(screen.X + screen.Width - viewport.X),
            left + 1,
            imageSize.Width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling(screen.Y + screen.Height - viewport.Y),
            top + 1,
            imageSize.Height);
        return new Rect(left, top, right - left, bottom - top);
    }

    internal static IReadOnlyList<Rect> BuildProjectedOutsideIgnoreRegions(
        MapGeometryFingerprint fingerprint,
        CapturedGameFrame frame,
        MapOverlayTransform transform) =>
        BuildProjectedOutsideIgnoreRegions(
            fingerprint.Map,
            fingerprint.FloorKey,
            frame,
            transform);

    internal static IReadOnlyList<Rect> BuildProjectedOutsideIgnoreRegions(
        MapRecord map,
        string floor,
        CapturedGameFrame frame,
        MapOverlayTransform transform)
    {
        if (frame.Image.Empty()
            || !double.IsFinite(transform.ScaleX)
            || transform.ScaleX <= 0d)
        {
            return [];
        }

        var bounds = (map.Recognition.GetFloor(floor)
            ?? map.Recognition.FirstFloor)
            .GetEffectiveValidMapBounds();
        var projectedLeft = (bounds.X * transform.ScaleX)
            + transform.OffsetX
            - frame.ViewportBounds.X;
        var projectedTop = (bounds.Y * transform.ScaleY)
            + transform.OffsetY
            - frame.ViewportBounds.Y;
        var projectedRight = (bounds.Right * transform.ScaleX)
            + transform.OffsetX
            - frame.ViewportBounds.X;
        var projectedBottom = (bounds.Bottom * transform.ScaleY)
            + transform.OffsetY
            - frame.ViewportBounds.Y;
        var left = Math.Clamp(
            (int)Math.Floor(projectedLeft),
            0,
            frame.Image.Width);
        var top = Math.Clamp(
            (int)Math.Floor(projectedTop),
            0,
            frame.Image.Height);
        var right = Math.Clamp(
            (int)Math.Ceiling(projectedRight),
            0,
            frame.Image.Width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling(projectedBottom),
            0,
            frame.Image.Height);
        if (right <= left || bottom <= top)
            return [new Rect(0, 0, frame.Image.Width, frame.Image.Height)];

        var regions = new List<Rect>(4);
        if (top > 0)
            regions.Add(new Rect(0, 0, frame.Image.Width, top));
        if (bottom < frame.Image.Height)
        {
            regions.Add(new Rect(
                0,
                bottom,
                frame.Image.Width,
                frame.Image.Height - bottom));
        }
        if (left > 0)
            regions.Add(new Rect(0, top, left, bottom - top));
        if (right < frame.Image.Width)
        {
            regions.Add(new Rect(
                right,
                top,
                frame.Image.Width - right,
                bottom - top));
        }

        return regions;
    }

    internal static RuntimeMapRecognition BuildStructureRecognition(
        MapGeometryFingerprint fingerprint,
        MapOverlayTransform transform,
        MapStructureRegistrationResult structure,
        bool wasForcedBestResult,
        RuntimeMapRecognition? anchorProposal = null,
        double? confidenceOverride = null)
    {
        var localizationConfidence = confidenceOverride
            ?? (anchorProposal is null
                ? structure.Confidence
                : new MapRegistrationConfidenceEvidence
                {
                    AnchorGeometry =
                        anchorProposal.Result.LocalizationConfidence,
                    StructureQuality = structure.Confidence
                }.Calculate());
        return new()
        {
            Map = fingerprint.Map,
            FloorImagePath = fingerprint.OverlayImagePath,
            Result = new MapRecognitionResult
            {
                MapId = fingerprint.Map.Id,
                Floor = fingerprint.FloorKey,
                OrientationDegrees = 0,
                Confidence = localizationConfidence,
                IdentityConfidence = anchorProposal?.Result.IdentityConfidence
                    ?? localizationConfidence,
                LocalizationConfidence = localizationConfidence,
                Source = anchorProposal?.Result.Source
                    ?? MapRecognitionSource.StructureMatching,
                HasAllRequiredAnchorEvidence = false,
                UsedLocalConfirmation = true,
                OverlayTransform = transform,
                AnchorMatches =
                    anchorProposal?.Result.AnchorMatches ?? [],
                StructureBestScore = structure.BestScore,
                StructureSecondScore = structure.SecondScore,
                StructureCandidateMargin = structure.CandidateMargin,
                StructureRejectionReason = structure.RejectionReason,
                EvidenceKind = MapAlignmentEvidenceKind.Structure,
                StructureDisposition =
                    structure.RejectionReason.ToDisposition(
                        structure.Accepted),
                WasForcedBestResult = wasForcedBestResult
            }
        };
    }

    internal static RuntimeMapRecognition BuildFloorStructureRecognition(
        MapRecord map,
        string floorKey,
        string overlayPath,
        MapOverlayTransform transform,
        MapStructureRegistrationResult structure,
        double identityPriorConfidence = 0d) =>
        new()
        {
            Map = map,
            FloorImagePath = overlayPath,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = floorKey,
                OrientationDegrees =
                    MapFloorRules.GetFloorProfile(map, floorKey)?.OrientationDegrees ?? 0,
                Confidence = structure.Confidence,
                IdentityConfidence = double.IsFinite(identityPriorConfidence)
                    && identityPriorConfidence > 0d
                        ? Math.Clamp(identityPriorConfidence, 0d, 1d)
                        : structure.Confidence,
                LocalizationConfidence = structure.Confidence,
                Source = MapRecognitionSource.StructureMatching,
                HasAllRequiredAnchorEvidence = false,
                UsedLocalConfirmation = true,
                OverlayTransform = transform,
                AnchorMatches = [],
                StructureBestScore = structure.BestScore,
                StructureSecondScore = structure.SecondScore,
                StructureCandidateMargin = structure.CandidateMargin,
                StructureRejectionReason = structure.RejectionReason,
                EvidenceKind = MapAlignmentEvidenceKind.Structure,
                StructureDisposition =
                    structure.RejectionReason.ToDisposition(
                        structure.Accepted),
                WasForcedBestResult = false
            }
        };

    internal static RuntimeMapRecognition BuildReusedTransformRecognition(
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        MapStructureRegistrationResult? structure) =>
        new()
        {
            Map = fingerprint.Map,
            FloorImagePath = fingerprint.OverlayImagePath,
            Result = new MapRecognitionResult
            {
                MapId = fingerprint.Map.Id,
                Floor = fingerprint.FloorKey,
                OrientationDegrees = 0,
                Confidence = session.LastConfidence,
                IdentityConfidence = session.LastConfidence,
                LocalizationConfidence = session.LastConfidence,
                Source = MapRecognitionSource.ReusedLastTransform,
                HasAllRequiredAnchorEvidence = false,
                UsedLocalConfirmation = false,
                OverlayTransform = session.LockedTransform,
                StructureBestScore =
                    structure?.BestScore ?? session.LastBestScore,
                StructureSecondScore =
                    structure?.SecondScore ?? session.LastSecondScore,
                StructureCandidateMargin =
                    structure?.CandidateMargin
                    ?? session.LastCandidateMargin,
                StructureRejectionReason =
                    structure?.RejectionReason
                    ?? MapStructureRejectionReason.NoCandidate,
                WasForcedBestResult = true,
                ReusedLastTransform = true,
                EvidenceKind = MapAlignmentEvidenceKind.None,
                StructureDisposition =
                    (structure?.RejectionReason
                        ?? MapStructureRejectionReason.NoCandidate)
                    .ToDisposition()
            }
        };

    internal static MapRecognitionAttempt ReuseLastTransformAttempt(
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        MapScanDiagnostics diagnostics,
        MapStructureRegistrationResult? structure = null)
    {
        // 侧门扫描只确认地图身份，不提供双门缩放锁定；其会话的锁定变换可能
        // 来自单门或辅助锚点，位置不够可信。直接复用可能继续渲染错误的叠加层，
        // 因此侧门策略下不复用，返回失败让下一帧重新扫描。
        if (session.SideEntranceScanPriorConfidence > 0d)
        {
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.NeedsGatePair;
            diagnostics.StructureRejectionReason =
                MapStructureRejectionReason.AnchorTransformConflict;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "侧门扫描会话尚未建立可信的双门缩放锁定，已拒绝复用上次变换，等待重新扫描。");
        }

        diagnostics.TrackingMode =
            MapAlignmentTrackingMode.HoldingLastTransform;
        diagnostics.UsedForcedBestResult = true;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition = BuildReusedTransformRecognition(
                fingerprint,
                session,
                structure)
        };
    }

    internal static RuntimeMapRecognition MarkForcedBestResult(
        RuntimeMapRecognition original)
    {
        var result = original.Result;
        return new RuntimeMapRecognition
        {
            Map = original.Map,
            FloorImagePath = original.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = result.MapId,
                Floor = result.Floor,
                OrientationDegrees = result.OrientationDegrees,
                Confidence = result.Confidence,
                IdentityConfidence = result.IdentityConfidence,
                LocalizationConfidence = result.LocalizationConfidence,
                Source = result.Source,
                HasAllRequiredAnchorEvidence =
                    result.HasAllRequiredAnchorEvidence,
                GeometryMargin = result.GeometryMargin,
                UsedLocalConfirmation = result.UsedLocalConfirmation,
                OverlayTransform = result.OverlayTransform,
                AnchorMatches = result.AnchorMatches,
                StructureBestScore = result.StructureBestScore,
                StructureSecondScore = result.StructureSecondScore,
                StructureCandidateMargin =
                    result.StructureCandidateMargin,
                StructureRejectionReason =
                    result.StructureRejectionReason,
                WasForcedBestResult = true,
                ReusedLastTransform = result.ReusedLastTransform,
                EvidenceKind = result.EvidenceKind,
                StructureDisposition = result.StructureDisposition,
                SkippedStructureValidation =
                    result.SkippedStructureValidation
            }
        };
    }

    internal static IReadOnlyList<MapRecognitionChoice> BuildChoices(
        IReadOnlyList<MapGeometryCandidate> ranked,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        double margin,
        MapRecognitionSource source,
        int maxCount = 9)
    {
        var choices = new List<MapRecognitionChoice>();
        foreach (var candidate in ranked.Take(maxCount))
        {
            if (candidate.VectorError > tuning.VectorErrorTolerance)
                continue;
            if (!TryBuildRecognition(
                    candidate,
                    alignmentMode,
                    tuning,
                    margin,
                    usedConfirmation: false,
                    source,
                    wasForcedBestResult: false,
                    out var recognition,
                    out _))
            {
                continue;
            }

            choices.Add(new MapRecognitionChoice
            {
                Recognition = recognition!,
                VectorError = candidate.VectorError
            });
        }

        return choices;
    }

    /// <summary>
    /// Constructs a rejected-structure attempt uniform across the scale-change
    /// and side-entrance-deviation rejection branches, keeping the caller
    /// responsible for producing the <paramref name="rejected"/> result and the
    /// human-readable <paramref name="failureReason"/>.
    /// </summary>
    internal static MapRecognitionAttempt BuildStructureRejectedAttempt(
        MapScanDiagnostics diagnostics,
        MapStructureRegistrationResult rejected,
        string failureReason,
        GateDetectionResult? gateResult,
        AlignmentSearchStage searchStage)
    {
        diagnostics.TrackingMode =
            MapAlignmentTrackingMode.HoldingLastTransform;
        diagnostics.StructureRejectionReason = rejected.RejectionReason;
        diagnostics.StructureAttempted = true;
        diagnostics.StructureAccepted = false;
        diagnostics.StructureFailureReason = rejected.FailureReason;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = rejected,
            FailureReason = failureReason,
            GateDetectionResult = gateResult,
            SearchStage = searchStage,
            StructureAttempted = true,
            StructureAccepted = false,
            StructureFailureReason = rejected.FailureReason,
        };
    }

    internal static MapRecognitionAttempt FailureWithChoices(
        IReadOnlyList<MapGeometryCandidate> ranked,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        double margin,
        MapRecognitionSource source,
        MapScanDiagnostics diagnostics,
        string reason,
        int maxCandidates = 9)
    {
        var choices = BuildChoices(ranked, alignmentMode, tuning, margin, source, maxCandidates);
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            Choices = choices,
            FailureReason = choices.Count > 0
                ? reason + " 请从候选中选择，或取消后重试。"
                : reason
        };
    }
}
