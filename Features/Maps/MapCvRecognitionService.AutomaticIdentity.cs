using System.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    /// <summary>Proposes and strictly validates map-floor identity without session or UI writes.</summary>
    public MapAutomaticIdentityAttempt RecognizeAutomaticIdentity(
        CapturedGameFrame frame,
        string? mapClass,
        string? floorKey,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning validationTuning,
        CancellationToken cancellationToken,
        MapMatchVariantIdentity? confirmedGroup = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(validationTuning);

        var timer = Stopwatch.StartNew();
        var normalizedRecognitionTuning =
            MapCvRecognitionHelpers.NormalizedCopy(tuning);
        normalizedRecognitionTuning.ForceBestRecognitionResult = false;

        var strictValidationTuning = validationTuning.Clone();
        strictValidationTuning.Mode = MapStructureRegistrationMode.ScanVerification;
        strictValidationTuning.EnableDebugOutput = false;
        strictValidationTuning.ReusePreviousAlignmentResult = false;
        strictValidationTuning.Normalize();

        var requestedFloorKey = MapScanFloorRules.NormalizeFloorIdentity(floorKey);
        var snapshotRevision = _catalogRevision;
        var mapSnapshot = _maps
            .Where(map => string.IsNullOrWhiteSpace(mapClass)
                || string.Equals(map.Class, mapClass,
                    StringComparison.OrdinalIgnoreCase))
            .Where(map => confirmedGroup is null || confirmedGroup.MapIds.Contains(map.Id))
            .Select(map => map.Clone())
            .OrderBy(map => map.SequenceNumber)
            .ThenBy(map => map.Id)
            .ToArray();

        var floorInputs = BuildAutomaticIdentityFloorInputs(
            mapSnapshot,
            requestedFloorKey);

        var work = floorInputs.Select(input =>
            new AutomaticIdentityFloorWork(input)).ToArray();
        var run = new AutomaticIdentityRun(
            this,
            frame,
            mapClass,
            requestedFloorKey,
            snapshotRevision,
            work,
            timer,
            cancellationToken);
        MapAutomaticIdentityAttempt Finish(
            MapAutomaticIdentityStatus status,
            int comparedCount,
            string failureReason,
            MapRecognitionAttempt? acceptedAlignment = null,
            MapVariantGroup? confirmedVariantGroup = null,
            Guid? confirmedVariantMemberId = null) =>
            run.Finish(status, comparedCount, failureReason, acceptedAlignment, confirmedVariantGroup,
                confirmedVariantMemberId);

        MapCatalogSnapshot identityCatalog;
        try
        {
            identityCatalog = _repository.GetCatalogSnapshotAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            return Finish(MapAutomaticIdentityStatus.ResourcesPending, 0,
                $"无法读取相似地图组：{exception.GetType().Name}: {exception.Message}");
        }

        if (confirmedGroup is not null && !IsConfirmedAutomaticGroupCurrent(
                confirmedGroup, mapClass, snapshotRevision, mapSnapshot, identityCatalog))
            return Finish(MapAutomaticIdentityStatus.ResourcesPending, 0,
                "已确认相似组的地图资源已变化，等待同步后重新判断。");

        var inputValidation = ValidateAutomaticIdentityPool(
            work,
            run,
            snapshotRevision,
            cancellationToken);
        if (inputValidation is not null)
            return inputValidation;

        var leases = new List<Vpsg3FloorIndexLease>(work.Length);
        try
        {
            var readinessAttempt = PrepareAutomaticIdentityFloorLeases(
                work,
                run,
                cancellationToken,
                leases);
            if (readinessAttempt is not null)
                return readinessAttempt;

            if (!TryExtractAutomaticIdentityObservation(
                    frame,
                    work,
                    run,
                    cancellationToken,
                    out var observation,
                    out var observationFailure))
                return observationFailure!;

            var geometryInput = ExtractAutomaticLocalGeometry(frame, observation,
                normalizedRecognitionTuning);

            var comparedCount = 0;
            var resourceFailure = false;
            var timedOut = false;
            foreach (var item in work)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.Cancelled;
                    item.FailureReason = "地图楼层竞争期间操作已取消。";
                    return Finish(MapAutomaticIdentityStatus.Cancelled,
                        comparedCount, "自动识别已取消。");
                }

                if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.TimedOut;
                    item.FailureReason = "地图开图身份识别超过时间预算。";
                    return Finish(MapAutomaticIdentityStatus.TimedOut,
                        comparedCount, "自动识别超过时间预算，请下次开图重试。");
                }

                var lease = item.Lease!;
                Vpsg3BootstrapResult proposal;
                try
                {
                    proposal = Vpsg3FastBootstrapSolver.TrySolve(
                        observation,
                        lease.Floor,
                        knownScaleSeed: null);
                    item.VpsgAttempted = true;
                    item.VpsgAccepted = proposal.IsAccepted;
                    item.VpsgConfidence = proposal.Confidence;
                    item.VpsgApertureMargin = proposal.ApertureMargin;
                    item.VpsgFailureReason = proposal.FallbackReason;
                    item.ProposalScale = proposal.Scale;
                    item.ProposalOffsetX = proposal.OffsetX;
                    item.ProposalOffsetY = proposal.OffsetY;
                    item.VpsgVerificationScore =
                        proposal.BestCandidate.WeightedScore;
                    item.VpsgSpatialScore =
                        proposal.BestCandidate.Spatial.GlobalScore;
                    comparedCount++;
                }
                catch (Exception exception)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        item.Status = MapAutomaticIdentityCandidateStatus.Cancelled;
                        item.FailureReason = "VPSG3 比较期间操作已取消。";
                        return Finish(MapAutomaticIdentityStatus.Cancelled,
                            comparedCount, "自动识别已取消。");
                    }
                    item.Status = MapAutomaticIdentityCandidateStatus.ResourcesPending;
                    item.BlocksAcceptance = true;
                    item.FailureReason =
                        $"VPSG3 比较失败：{exception.GetType().Name}: {exception.Message}";
                    return Finish(MapAutomaticIdentityStatus.ResourcesPending,
                        comparedCount, "候选楼层比较未完整完成。");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.Cancelled;
                    item.FailureReason = "VPSG3 比较完成后操作已取消。";
                    return Finish(MapAutomaticIdentityStatus.Cancelled,
                        comparedCount, "自动识别已取消。");
                }

                if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.TimedOut;
                    item.FailureReason = "地图开图身份识别超过时间预算。";
                    return Finish(MapAutomaticIdentityStatus.TimedOut,
                        comparedCount, "自动识别超过时间预算，请下次开图重试。");
                }

                var vpsgConfig = Vpsg3TuningConfig.Default;
                var best = proposal.BestCandidate;
                var hasFinitePose = double.IsFinite(proposal.Scale)
                    && proposal.Scale >= vpsgConfig.MinSupportedScale
                    && proposal.Scale <= vpsgConfig.MaxSupportedScale
                    && double.IsFinite(proposal.OffsetX)
                    && double.IsFinite(proposal.OffsetY);
                var hasCompletedPoseComparison = proposal.ScaleResult.Success
                    && proposal.ScaleResult.PeakRatio >= vpsgConfig.PeakRatioThreshold
                    && hasFinitePose
                    && double.IsFinite(best.Spatial.GlobalScore)
                    && best.Spatial.TotalValidPoints > 0;
                var physicalSupport = proposal.ScaleResult.Success
                    && hasFinitePose
                    && best.Spatial.IsSpatiallyConsistent
                    && double.IsFinite(best.WeightedScore)
                    && best.WeightedScore >= vpsgConfig.MinVerificationScore;
                item.HasHighPhysicalSupport = physicalSupport;
                item.HasCrediblePoseProposal = proposal.IsAccepted
                    || physicalSupport;
                var localUniquenessRejected = !proposal.IsAccepted
                    && (proposal.FallbackReason.Contains(
                            "ApertureMarginBelowThreshold",
                            StringComparison.OrdinalIgnoreCase)
                        || proposal.FallbackReason.Contains(
                            "NoDistinctRefinedRunnerUp",
                            StringComparison.OrdinalIgnoreCase));

                if (!item.HasCrediblePoseProposal)
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.InsufficientEvidence;
                    // Completed low-support comparisons fail candidate admission.
                    item.BlocksAcceptance = !hasCompletedPoseComparison;
                    item.FailureReason = string.IsNullOrWhiteSpace(proposal.FallbackReason)
                        ? "VPSG3 未产生通过现有验证门槛的位姿提案。"
                        : proposal.FallbackReason;
                    continue;
                }

                MapRecognitionAttempt strictAttempt;
                MapRecognitionAttempt? runnerUpAttempt = null;
                try
                {
                    item.StrictValidationAttempted = true;
                    strictAttempt = ValidateAutomaticIdentityPose(frame, item,
                        proposal.Scale, proposal.OffsetX, proposal.OffsetY,
                        proposal.Confidence, normalizedRecognitionTuning,
                        strictValidationTuning);
                    item.AlignmentAttempt = strictAttempt;
                    item.StrictRejectionReason =
                        strictAttempt.StructureResult?.RejectionReason;
                    item.StrictValidationAccepted =
                        strictAttempt.StructureAccepted
                        && strictAttempt.Recognition is not null
                        && strictAttempt.Recognition.Result.OverlayTransform is not null
                        && !strictAttempt.Recognition.Result.WasForcedBestResult;
                    if (hasCompletedPoseComparison && localUniquenessRejected
                        && MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is not <= 0
                        && proposal.HasDistinctRunnerUp
                        && proposal.RunnerUpCandidate is { } runnerUp)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        item.RunnerUpValidationAttempted = true;
                        runnerUpAttempt = ValidateAutomaticIdentityPose(frame, item,
                            runnerUp.Scale, runnerUp.OffsetX, runnerUp.OffsetY,
                            proposal.Confidence, normalizedRecognitionTuning,
                            strictValidationTuning);
                        item.RunnerUpAlignmentAttempt = runnerUpAttempt;
                        LogAutomaticIdentityRunnerUp(item, runnerUpAttempt);
                    }
                }
                catch (Exception exception)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        item.Status = MapAutomaticIdentityCandidateStatus.Cancelled;
                        item.FailureReason = "严格校验期间操作已取消。";
                        return Finish(MapAutomaticIdentityStatus.Cancelled,
                            comparedCount, "自动识别已取消。");
                    }
                    item.Status = MapAutomaticIdentityCandidateStatus.ResourcesPending;
                    item.BlocksAcceptance = true;
                    item.FailureReason =
                        $"严格结构校验失败：{exception.GetType().Name}: {exception.Message}";
                    return Finish(MapAutomaticIdentityStatus.ResourcesPending,
                        comparedCount, "候选楼层严格校验未完整完成。");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.Cancelled;
                    item.FailureReason = "严格结构校验完成后操作已取消。";
                    return Finish(MapAutomaticIdentityStatus.Cancelled,
                        comparedCount, "自动识别已取消。");
                }

                item.NativePoseComparisonComplete = hasCompletedPoseComparison
                    && (!localUniquenessRejected || runnerUpAttempt is not null)
                    && IsCompletedAutomaticIdentityPoseValidation(strictAttempt)
                    && (runnerUpAttempt is null
                        || IsCompletedAutomaticIdentityPoseValidation(runnerUpAttempt));

                if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.TimedOut;
                    item.BlocksAcceptance = true;
                    item.FailureReason = "严格结构校验完成时地图开图预算已耗尽。";
                    timedOut = true;
                    continue;
                }

                if (item.StrictValidationAccepted)
                {
                    if (proposal.IsAccepted)
                    {
                        item.Status = MapAutomaticIdentityCandidateStatus.StrictlyVerified;
                        item.FailureReason = string.Empty;
                    }
                    else
                    {
                        // Strict fit cannot override rejected local pose uniqueness.
                        item.Status = localUniquenessRejected
                            ? MapAutomaticIdentityCandidateStatus.LocalPoseAmbiguous
                            : MapAutomaticIdentityCandidateStatus.HighSupportUnresolved;
                        item.BlocksAcceptance = true;
                        item.FailureReason = string.IsNullOrWhiteSpace(
                                proposal.FallbackReason)
                            ? "VPSG3 有较强结构支持，但拒绝了局部位姿唯一性验证。"
                            : proposal.FallbackReason;
                    }
                }
                else
                {
                    var rejection = strictAttempt.StructureResult?.RejectionReason
                        ?? MapStructureRejectionReason.InvalidInput;
                    item.FailureReason = string.IsNullOrWhiteSpace(
                            strictAttempt.StructureFailureReason)
                        ? strictAttempt.FailureReason
                        : strictAttempt.StructureFailureReason;

                    if (rejection == MapStructureRejectionReason.TimeBudgetExceeded
                        || !string.IsNullOrWhiteSpace(strictAttempt.StructureResult?
                            .LowStructureBudgetTerminationReason)
                        || runnerUpAttempt?.StructureResult?.RejectionReason
                            == MapStructureRejectionReason.TimeBudgetExceeded
                        || !string.IsNullOrWhiteSpace(runnerUpAttempt?.StructureResult?
                            .LowStructureBudgetTerminationReason)
                        || MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                    {
                        item.Status = MapAutomaticIdentityCandidateStatus.TimedOut;
                        item.BlocksAcceptance = true;
                        item.FailureReason = string.IsNullOrWhiteSpace(item.FailureReason)
                            ? "严格结构校验超过时间预算。"
                            : item.FailureReason;
                        timedOut = true;
                    }
                    else
                    {
                        var disposition = rejection.ToDisposition(
                            strictAttempt.StructureAccepted);
                        if (rejection == MapStructureRejectionReason.None)
                            disposition = MapStructureEvidenceDisposition.SystemError;
                        if (hasCompletedPoseComparison
                            && IsCompletedAutomaticIdentityQualityRejection(strictAttempt)
                            && (proposal.IsAccepted || (localUniquenessRejected
                                && runnerUpAttempt is not null
                                && IsCompletedAutomaticIdentityQualityRejection(runnerUpAttempt))))
                        {
                            item.Status = MapAutomaticIdentityCandidateStatus.StrictValidationRejected;
                        }
                        else if (localUniquenessRejected)
                        {
                            item.Status = MapAutomaticIdentityCandidateStatus.LocalPoseAmbiguous;
                            item.BlocksAcceptance = true;
                        }
                        else if (!proposal.IsAccepted && physicalSupport)
                        {
                            item.Status = MapAutomaticIdentityCandidateStatus.HighSupportUnresolved;
                            item.BlocksAcceptance = true;
                        }
                        else if (disposition == MapStructureEvidenceDisposition.SystemError)
                        {
                            item.Status = MapAutomaticIdentityCandidateStatus.ResourcesPending;
                            item.BlocksAcceptance = true;
                            resourceFailure = true;
                        }
                        else if ((physicalSupport || proposal.IsAccepted)
                            && disposition == MapStructureEvidenceDisposition.Inconclusive)
                        {
                            item.Status = MapAutomaticIdentityCandidateStatus.HighSupportUnresolved;
                            item.BlocksAcceptance = true;
                        }
                        else
                        {
                            item.Status = MapAutomaticIdentityCandidateStatus.StrictValidationRejected;
                        }
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.Cancelled;
                    item.FailureReason = "地图楼层竞争期间操作已取消。";
                    return Finish(MapAutomaticIdentityStatus.Cancelled,
                        comparedCount, "自动识别已取消。");
                }
            }

            List<AutomaticIdentityPoseEvidence> nativeEvidence;
            List<AutomaticIdentityPoseEvidence> survivingNative;
            HashSet<AutomaticIdentityFloorWork> nativeUnknown;
            try
            {
                using (var evidenceSpan = MapOperationTraceAmbient.StartChild(
                    "automatic_native_pose_evidence", MapOperationWaitKind.Compute))
                {
                    nativeEvidence = CollectNativeAutomaticPoseEvidence(observation, work, out nativeUnknown);
                    evidenceSpan.Complete(terminalReason: $"poses={nativeEvidence.Count}");
                }
                survivingNative = RemoveDominatedAutomaticPoseIdentities(observation, nativeEvidence,
                    strictValidationTuning.MinimumSpanPixels, cancellationToken, geometryInput);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Finish(MapAutomaticIdentityStatus.Cancelled,
                    comparedCount, "自动识别已取消。");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Finish(MapAutomaticIdentityStatus.Cancelled,
                    comparedCount, "自动识别已取消。");
            }

            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
            {
                return Finish(MapAutomaticIdentityStatus.TimedOut,
                    comparedCount,
                    "地图开图身份识别超过时间预算，请下次开图重试。");
            }

            MapCatalogRevision currentCatalogRevision;
            try
            {
                currentCatalogRevision = _repository.GetCatalogRevision();
            }
            catch (Exception exception)
            {
                return Finish(MapAutomaticIdentityStatus.ResourcesPending,
                    comparedCount,
                    $"无法确认地图目录版本：{exception.GetType().Name}: {exception.Message}");
            }

            if (snapshotRevision != _catalogRevision
                || snapshotRevision != currentCatalogRevision)
            {
                return Finish(MapAutomaticIdentityStatus.ResourcesPending,
                    comparedCount,
                    "自动识别期间地图目录发生变化，结果已作废。");
            }

            if (resourceFailure)
            {
                return Finish(MapAutomaticIdentityStatus.ResourcesPending,
                    comparedCount,
                    "至少一个高支持候选的严格校验未能完成，身份池不完整。");
            }

            if (timedOut)
            {
                return Finish(MapAutomaticIdentityStatus.TimedOut,
                    comparedCount,
                    "至少一个候选楼层的严格校验超过时间预算。");
            }

            var nativeUnresolved = nativeUnknown.Union(work.Where(item =>
                item.BlocksAcceptance && !item.NativePoseComparisonComplete)).ToArray();
            var nativeSelection = SelectConfiguredAutomaticIdentityPose(observation,
                survivingNative, nativeUnresolved, identityCatalog);
            if (nativeSelection is { Pose: var nativeAccepted })
            {
                foreach (var item in work)
                {
                    if (item.NativePoseComparisonComplete) item.BlocksAcceptance = false;
                    if ((item.AlignmentAttempt?.StructureAccepted == true
                        || item.RunnerUpAlignmentAttempt?.StructureAccepted == true)
                        && !survivingNative.Any(pose => pose.Owner == item))
                    {
                        item.Status = MapAutomaticIdentityCandidateStatus.DominatedByObservedStructure;
                        item.FailureReason = "所有已验证位姿均被同帧室内、入口或跨身份墙段证据排除。";
                    }
                }
                nativeAccepted.Owner.AlignmentAttempt = nativeAccepted.Attempt;
                nativeAccepted.Owner.Status = MapAutomaticIdentityCandidateStatus.StrictlyVerified;
                return Finish(MapAutomaticIdentityStatus.Accepted,
                    comparedCount,
                    string.Empty,
                    nativeAccepted.Attempt,
                    nativeSelection.ConfiguredGroup,
                    nativeSelection.ConfirmedMemberId);
            }
            // Multiple native positives are not a reason to skip independent
            // geometry. Its finite proposal pool may reject partial-room fits
            // or recover a scale that periodic walls cannot distinguish. Keep
            // every known native primary/runner and every unfinished contender.
            return RecognizeAutomaticLocalGeometry(frame, observation,
                work, run, snapshotRevision, normalizedRecognitionTuning, strictValidationTuning,
                cancellationToken, geometryInput, nativeEvidence, nativeUnknown, identityCatalog);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }
}
