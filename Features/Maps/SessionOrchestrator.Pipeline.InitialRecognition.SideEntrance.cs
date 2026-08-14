// IDVB Remaster — Session Orchestrator 识别管线

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void RunInitialSideEntranceRecognition(
        CapturedGameFrame frame,
        InitialRecognitionPipelineState result)
    {
        ref var recognition = ref result.Recognition;
        ref var failureReason = ref result.FailureReason;
        ref var pendingChoices = ref result.PendingChoices;
        ref var pendingChoicesReason = ref result.PendingChoicesReason;
        ref var pendingSideEntranceSeed = ref result.PendingSideEntranceSeed;
        ref var pendingSideEntranceIdentity = ref result.PendingSideEntranceIdentity;
        ref var pendingSideEntranceScan = ref result.PendingSideEntranceScan;
        var repairCacheKeys = result.RepairCacheKeys;
        ref var scanSucceeded = ref result.ScanSucceeded;

        // ── 侧门扫描链路：单门特征匹配识别地图 + 侧门对齐 ──
        // 侧门场景通常只有 1 扇门可见，双门几何排名（RankGeometry 硬性
        // 要求 ≥2 门）必然失败。改用侧门特征模板匹配识别地图身份，
        // 生成对齐种子后走 SideEntrance 对齐（单门 + 结构配准）。
        MapRecognitionAttempt sideAttempt;
        MapAlignmentSession? seed = null;
        var sideMapId = Guid.Empty;
        var displayName = string.Empty;
        var sideTimings = new Dictionary<string, double>();
        try
        {
            var sideSw = Stopwatch.StartNew();
            var sideScan = _recognition.RunSideEntranceScan(
                frame,
                _settings!.RecognitionTuning,
                topK: 5,
                mapClass: _matchSession.Snapshot.MapClass,
                // 进度由门检测、每张地图的粗搜及精化完成数实时驱动。
                progress: value => _scanProgressOverlay.Report(
                    0.38d + value * 0.38d,
                    "正在扫描地图特征..."));
            pendingSideEntranceScan = sideScan;
            _lastDiagnostics = new MapScanDiagnostics
            {
                ReadyMapCount = _recognition.ReadyMapCount,
                TotalMapCount = _recognition.TotalMapCount,
                SideEntranceReadyMapCount = sideScan.ReadyMapCount,
                SideEntranceEligibleMapCount = sideScan.EligibleMapCount,
                SideEntranceRejectedCandidateCount =
                    sideScan.RejectedCandidateCount
            };
            var candidates = sideScan.Candidates;
            sideTimings["side_entrance_scan"] = sideSw.Elapsed.TotalMilliseconds;
            sideTimings["gate_detection"] = sideScan.GateDetection.ElapsedMilliseconds;
            _lastScanPhaseTimings = sideTimings;
            if (sideScan.GateDetection.Gates.Count == 0)
            {
                failureReason =
                    "识别失败：侧门扫描要求当前地图暴露一个门特征，但未检测到门";
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Warning,
                    failureReason);
                return;
            }
            if (candidates.Count == 0)
            {
                failureReason =
                    $"识别失败：已检测到门，但{sideScan.FailureReason}";
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Warning,
                    failureReason);
                return;
            }

            // The scan is triggered while the native game map is
            // already open. Synchronize that fact before the next
            // physical close/reopen key pair; otherwise the first
            // key after scanning is interpreted against the stale
            // pre-scan toggle state.
            scanSucceeded = true;

            var sideAlignmentTuning = CreateInitialAlignmentRecognitionTuning();
            if (sideAlignmentTuning.GateTemplateThreshold
                > GateTemplateRules.FallbackPairThreshold)
            {
                sideAlignmentTuning.GateTemplateThreshold =
                    GateTemplateRules.FallbackPairThreshold;
            }
            var sideStructureTuning =
                MapScaleSeedResolver.CreateStrictInitialIdentityValidationTuning(
                    CreateInitialAlignmentStructureTuning());
            var reliable = new List<(SideEntranceScanCandidate Candidate,
                MapAlignmentSession Seed, MapRecognitionAttempt Attempt)>();
            var verificationCandidates = SideEntranceCandidateEvidence
                .SelectVerificationCandidates(candidates);

            var verifiedCount = 0;
            foreach (var candidate in verificationCandidates)
            {
                if (!_recognition.TryCreateSideEntranceAlignmentSeed(
                        candidate,
                        frame.ViewportBounds,
                        out var candidateSeed,
                        out var seedReason))
                {
                    candidate.RejectionReason =
                        SideEntranceRejectionReason.InvalidFeatureData;
                    candidate.RejectionDetail = seedReason;
                    continue;
                }

                var attempt = AlignSideEntranceWithScaleFallback(
                    frame,
                    candidate,
                    candidateSeed,
                    sideAlignmentTuning,
                    sideStructureTuning,
                    out candidateSeed);
                if (SideEntranceCandidateEvidence.ApplyStructureAttempt(
                        candidate,
                        attempt))
                {
                    reliable.Add((candidate, candidateSeed, attempt));
                }

                RecordResearchAttemptForMap(
                    candidate.Map,
                    candidate.FloorKey,
                    frame,
                    attempt,
                    "side-entrance-candidate-verification");

                // 结构验证是扫描中最慢的一段（逐候选做 VPSG/结构配准），
                // 侧门扫描回调在 76% 处结束；这里逐候选实时推进，避免进度条停滞。
                verifiedCount++;
                _scanProgressOverlay.Report(
                    0.76d + 0.12d * verifiedCount / verificationCandidates.Count,
                    "正在验证地图结构...");
            }

            var orderedReliable = SideEntranceCandidateEvidence.OrderVerified(
                    reliable,
                    item => item.Candidate)
                .ToArray();
            var choices = new List<MapRecognitionChoice>();
            for (var index = 0; index < orderedReliable.Length; index++)
            {
                var item = orderedReliable[index];
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

            var referenceCandidates = candidates
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
                        EvidenceLabel =
                            $"仅供参考（未通过结构验证） · "
                            + $"模板相似度 {candidate.MatchScore:P0} · "
                            + candidate.RejectionDetail
                    });
                }
            }

            // Ambiguity is a valid empty-recognition outcome. Never promote
            // the highest template maximum merely to fill the chooser.
            if (reliable.Count != 1
                || _settings.RecognitionTuning.ForceCandidateSelection)
            {
                pendingChoices = choices;
                pendingChoicesReason = reliable.Count == 0
                    ? $"0 个已验证结果；以下 {referenceCandidates.Length} 项仅供参考，点击后仍会执行严格结构复核。"
                    : $"{reliable.Count} 个已验证结果；已验证结果优先，另有 {referenceCandidates.Length} 项仅供参考。";
                failureReason = reliable.Count == 0
                    ? $"侧门扫描无可靠候选（侧门就绪 {sideScan.ReadyMapCount}/{sideScan.EligibleMapCount}）。"
                    : null;
                return;
            }

            var selected = orderedReliable[0];
            var best = selected.Candidate;
            sideAttempt = selected.Attempt;
            seed = selected.Seed;
            displayName = best.Map.DisplayName;
            sideMapId = best.Map.Id;
            pendingSideEntranceSeed = seed;
            pendingSideEntranceIdentity = sideAttempt.Recognition;
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"侧门可靠候选 · map={best.Map.SequenceNumber}#{best.FloorKey} · "
                + $"template={best.MatchScore:P0} · structure={best.StructureScore:P0} · "
                + $"identity={best.IdentityConfidence:P0}");
        }
        catch (Exception alignEx)
        {
            RecordResearchAttemptForMap(
                _recognition.TryGetMap(sideMapId), seed?.FloorKey, frame,
                new MapRecognitionAttempt { FailureReason = alignEx.Message },
                "side-entrance");
            failureReason = $"侧门对齐异常：{alignEx.Message}";
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Error,
                failureReason,
                details: new()
                {
                    ["exceptionType"] = alignEx.GetType().FullName,
                    ["stackTrace"] = alignEx.ToString()
                });
            return;
        }

        _lastDiagnostics = sideAttempt.Diagnostics;
        _lastDiagnostics.SideEntranceReadyMapCount =
            pendingSideEntranceScan?.ReadyMapCount ?? 0;
        _lastDiagnostics.SideEntranceEligibleMapCount =
            pendingSideEntranceScan?.EligibleMapCount ?? 0;
        _lastDiagnostics.SideEntranceRejectedCandidateCount =
            pendingSideEntranceScan?.RejectedCandidateCount ?? 0;
        _lastScanPhaseTimings = sideTimings;
        RecordResearchAttemptForMap(
            sideAttempt.Recognition?.Map
                ?? _recognition.TryGetMap(sideMapId),
            seed?.FloorKey, frame, sideAttempt, "side-entrance");

        _logCollector.Append(
            MapLogCategory.Session,
            sideAttempt.Recognition is null ? MapLogLevel.Warning : MapLogLevel.Info,
            $"侧门对齐完成 · success={sideAttempt.Recognition is not null} · "
            + $"reason={sideAttempt.FailureReason ?? "<none>"}",
            details: new()
            {
                ["mapId"] = sideMapId,
                ["confidence"] = sideAttempt.Recognition?.Result.Confidence,
                ["failureReason"] = sideAttempt.FailureReason
            });

        if (sideAttempt.Recognition is { } sideRec)
        {
            recognition = sideRec;
            _lastRecognition = sideRec;
            // 用侧门扫描种子（而非 null）作为 previous，保留
            // SideEntranceScanPriorConfidence，使后续仅对齐调用
            // 能正确识别侧门路由（AllowScaleSearch = true）。
            _lastAlignmentSession = UpdateAlignmentSession(
                seed,
                sideRec);
            RememberPrimaryFloorSession(sideRec, _lastAlignmentSession);
            _statusMessage =
                $"侧门对齐成功：{displayName} · 置信度 {sideRec.Result.Confidence:P0}";
        }
        else if (sideAttempt.Choices.Count > 0)
        {
            pendingChoices = sideAttempt.Choices;
            pendingChoicesReason =
                sideAttempt.FailureReason ?? string.Empty;
        }
        else
        {
            failureReason = $"侧门对齐失败：{sideAttempt.FailureReason}";
        }
        return;
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

    private void StageCrossResolutionValidatedScale(
        CapturedGameFrame frame,
        SideEntranceScanCandidate candidate,
        MapCacheResolutionSignature targetResolution,
        MapRecognitionAttempt attempt)
    {
        var recognition = attempt.Recognition;
        var transform = recognition?.Result.OverlayTransform;
        if (recognition is null
            || transform is null
            || !TryGetUniformScale(transform, out var finalScale))
        {
            return;
        }

        var key = MapFeatureCacheRules.CreateKey(
            candidate.Map,
            candidate.FloorKey,
            targetResolution);
        StageAutomaticMapCacheEntry(CreateCacheEntry(
            key,
            finalScale,
            MapFeatureCacheSource.CrossResolutionValidated,
            sampleCount: 1,
            confidence: recognition.Result.LocalizationConfidence,
            relativeMad: 0d,
            observedDpi: DwrGameWindowCaptureService.GetWindowDpi(
                frame.WindowHandle),
            candidateMargin: MapFeatureCacheRules.GetCandidateMargin(
                recognition.Result)));
        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            "跨分辨率尺度已通过目标分辨率严格验证并暂存",
            details: new()
            {
                ["outcome"] = "cross-resolution-validated-staged",
                ["mapId"] = candidate.Map.Id,
                ["floor"] = candidate.FloorKey,
                ["targetViewport"] =
                    $"{targetResolution.ViewportWidth}x{targetResolution.ViewportHeight}",
                ["finalScale"] = finalScale,
                ["maximumChamferPixels"] = 3.0d
            });
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
            seed.IsProjected,
            seed.IsProjected ? seed.Scale : 0d);
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
            MapScaleSeedSource.CrossResolution => "cross-resolution",
            MapScaleSeedSource.Vpsg => "vpsg",
            _ => "side-template"
        };

    private static string DescribeAttemptFailure(MapRecognitionAttempt attempt) =>
        string.IsNullOrWhiteSpace(attempt.StructureFailureReason)
            ? attempt.FailureReason ?? "rejected"
            : attempt.StructureFailureReason;

}
