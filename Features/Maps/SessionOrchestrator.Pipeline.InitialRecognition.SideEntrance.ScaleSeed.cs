namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private List<MapRecognitionChoice> BuildScanVerificationChoices(
        IReadOnlyList<(
            SideEntranceScanCandidate Candidate,
            MapAlignmentSession Seed,
            MapRecognitionAttempt Attempt)> reliable,
        IReadOnlyList<SideEntranceScanCandidate> candidates,
        CapturedGameFrame frame,
        bool requireStrictStructureRegistration,
        out SideEntranceScanCandidate[] referenceCandidates)
    {
        var choices = new List<MapRecognitionChoice>();
        for (var index = 0; index < reliable.Count; index++)
        {
            var item = reliable[index];
            var rawChamfer = double.IsFinite(
                item.Candidate.RawChamferPixels)
                    ? $"{item.Candidate.RawChamferPixels:F2}px"
                    : "n/a";
            choices.Add(new MapRecognitionChoice
            {
                Recognition = item.Attempt.Recognition!,
                VectorError = 0d,
                EvidenceScore = item.Candidate.IdentityConfidence,
                IsReferenceOnly = false,
                PreferredOrder = index,
                EvidenceLabel =
                    $"结构已验证 · Chamfer {rawChamfer} · "
                    + $"边缘覆盖 {item.Candidate.StructureEdgeCoverage:P0} · "
                    + $"模板相似度 {item.Candidate.MatchScore:P0}"
            });
        }

        referenceCandidates = candidates
            .Where(candidate => candidate.Disposition !=
                SideEntranceCandidateDisposition.Reliable)
            .OrderByDescending(candidate => candidate.MatchScore)
            .Take(SideEntranceScanRules.MaximumReferenceCandidates)
            .ToArray();
        for (var index = 0; index < referenceCandidates.Length; index++)
        {
            var candidate = referenceCandidates[index];
            if (_recognition.TryCreateSideEntranceSelection(
                    candidate,
                    frame.ViewportBounds,
                    out var referenceSelection,
                    out _,
                    out _))
            {
                choices.Add(new MapRecognitionChoice
                {
                    Recognition = referenceSelection,
                    VectorError = 0d,
                    EvidenceScore = candidate.MatchScore,
                    IsReferenceOnly = true,
                    PreferredOrder = index,
                    EvidenceLabel = (requireStrictStructureRegistration
                            ? "仅供参考（未通过结构验证） · "
                            : "扫描阶段未验证 · ")
                        + $"模板相似度 {candidate.MatchScore:P0} · "
                        + candidate.RejectionDetail
                });
            }
        }

        return choices;
    }

    private MapRecognitionAttempt AlignSideEntranceFromSeed(
        CapturedGameFrame frame,
        SideEntranceScanCandidate candidate,
        MapAlignmentSession seed,
        MapRecognitionTuning alignmentTuning,
        MapStructureRegistrationTuning structureTuning)
    {
        var searchContext = CreateSideEntranceSearchContext(
            seed,
            alignmentTuning,
            useInitialHighPrecisionRecovery: true);
        return _recognition.AlignSideEntrance(
            frame,
            candidate.Map.Id,
            seed,
            _settings!.OverlayAlignmentMode,
            alignmentTuning,
            structureTuning,
            alignmentSearchContext: searchContext);
    }

    private static void SetScaleSeedDiagnostics(
        MapRecognitionAttempt attempt,
        ResolvedMapScaleSeed seed,
        string rejectionReason)
    {
        SetScaleSeedDiagnostics(
            attempt,
            seed.Source,
            seed.Scale,
            seed.SourceResolution,
            seed.TargetResolution,
            rejectionReason,
            seed.CacheSource.ToString(),
            projected: false,
            projectedScale: 0d);
    }

    private static void SetScaleSeedDiagnostics(
        MapRecognitionAttempt attempt,
        MapScaleSeedSource source,
        double scale,
        MapCacheResolutionSignature? sourceResolution,
        MapCacheResolutionSignature targetResolution,
        string rejectionReason,
        string cacheSource = "",
        bool projected = false,
        double projectedScale = 0d)
    {
        var diagnostics = attempt.Diagnostics;
        diagnostics.ScaleSeedSource = ScaleSeedSourceName(source);
        diagnostics.ScaleSeedCacheSource = cacheSource;
        diagnostics.ScaleSeedScale = double.IsFinite(scale) ? scale : 0d;
        diagnostics.ScaleSeedProjected = projected;
        diagnostics.ScaleSeedSourceViewportWidth =
            sourceResolution?.ViewportWidth ?? 0;
        diagnostics.ScaleSeedSourceViewportHeight =
            sourceResolution?.ViewportHeight ?? 0;
        diagnostics.ScaleSeedTargetViewportWidth =
            targetResolution.ViewportWidth;
        diagnostics.ScaleSeedTargetViewportHeight =
            targetResolution.ViewportHeight;
        diagnostics.ProjectedScale = double.IsFinite(projectedScale)
            && projectedScale > 0d
                ? projectedScale
                : 0d;
        diagnostics.FinalValidatedScale =
            attempt.Recognition?.Result.OverlayTransform?.ScaleX ?? 0d;
        diagnostics.ScaleSeedRejectionReason = rejectionReason;
    }

    private void LogScaleSeedDecision(
        SideEntranceScanCandidate candidate,
        string source,
        double scale,
        MapCacheResolutionSignature? sourceResolution,
        MapCacheResolutionSignature targetResolution,
        MapRecognitionAttempt? attempt,
        string rejectionReason)
    {
        var details = SideEntranceCandidateEvidence.BuildStructureMetricLogDetails(
            attempt?.StructureResult,
            effectiveChamferLimit: 3.0d);
        details["outcome"] = source;
        details["mapId"] = candidate.Map.Id;
        details["floor"] = candidate.FloorKey;
        details["sourceViewport"] = sourceResolution is null
            ? null
            : $"{sourceResolution.ViewportWidth}x{sourceResolution.ViewportHeight}";
        details["targetViewport"] =
            $"{targetResolution.ViewportWidth}x{targetResolution.ViewportHeight}";
        details["seedScale"] = double.IsFinite(scale) ? scale : null;
        details["finalScale"] =
            attempt?.Recognition?.Result.OverlayTransform?.ScaleX;
        details["structureAccepted"] = attempt?.StructureAccepted;
        details["rejectionReason"] = rejectionReason;
        details["attemptFailure"] = attempt is null
            ? null
            : DescribeAttemptFailure(attempt);

        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            attempt?.Recognition is not null
                ? MapLogLevel.Info
                : MapLogLevel.Warning,
            $"侧门尺度路径 · {source}",
            details: details);
    }

    private static string ScaleSeedSourceName(MapScaleSeedSource source) =>
        source switch
        {
            MapScaleSeedSource.ExactCache => "exact-cache",
            MapScaleSeedSource.Vpsg => "vpsg",
            _ => "side-template"
        };

    private static string DescribeAttemptFailure(MapRecognitionAttempt attempt) =>
        string.IsNullOrWhiteSpace(attempt.StructureFailureReason)
            ? attempt.FailureReason ?? "rejected"
            : attempt.StructureFailureReason;
}
