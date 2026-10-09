using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private static bool IsCompletedAutomaticIdentityPoseValidation(MapRecognitionAttempt attempt) =>
        (attempt.StructureAccepted && attempt.Recognition?.Result.OverlayTransform is not null
            && !attempt.Recognition.Result.WasForcedBestResult
            && string.IsNullOrWhiteSpace(attempt.StructureResult?.LowStructureBudgetTerminationReason))
        || IsCompletedAutomaticIdentityQualityRejection(attempt);

    private static bool IsCompletedAutomaticIdentityQualityRejection(
        MapRecognitionAttempt attempt)
    {
        var result = attempt.StructureResult;
        if (!attempt.StructureAttempted || result is null || result.Accepted
            || !string.IsNullOrWhiteSpace(result.LowStructureBudgetTerminationReason)
            || result.ReferenceWidth <= 0 || result.ReferenceHeight <= 0
            || result.QueryEdgePixels <= 0
            || result.QueryBoundsWidth <= 0 || result.QueryBoundsHeight <= 0)
            return false;

        // Candidate admission differs from invalidating an existing trusted lock.
        // These measured quality/size failures cannot supply a usable identity
        // this frame. Missing work, ambiguity and incomplete scale search still
        // block the pool; no native score relaxation can promote a failed fit.
        return result.RejectionReason is MapStructureRejectionReason.QueryLargerThanReference
            or MapStructureRejectionReason.WeakAbsoluteScore
            or MapStructureRejectionReason.InconsistentStructure;
    }

    private sealed class AutomaticIdentityRun(
        MapCvRecognitionService service,
        CapturedGameFrame frame,
        string? mapClass,
        string? requestedFloorKey,
        MapCatalogRevision snapshotRevision,
        AutomaticIdentityFloorWork[] work,
        Stopwatch timer,
        CancellationToken cancellationToken)
    {
        private MapScanDiagnostics _diagnostics =
            MapCvRecognitionDiagnostics.CreateDiagnostics(
                service.ReadyMapCount,
                service.TotalMapCount);

        public MapAutomaticIdentityAttempt Finish(
            MapAutomaticIdentityStatus status,
            int comparedCount,
            string failureReason,
            MapRecognitionAttempt? acceptedAlignment = null,
            MapVariantGroup? confirmedVariantGroup = null,
            Guid? confirmedVariantMemberId = null)
        {
            // Every native and geometry terminal route reaches this boundary.
            // Optional drawing remains inside the original wall-time budget.
            var flushedDiagnostics = MapDiagnosticModeCapture.FlushDeferredFitness();
            if (flushedDiagnostics && status == MapAutomaticIdentityStatus.Accepted)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    status = MapAutomaticIdentityStatus.Cancelled;
                    failureReason = "自动识别已取消。";
                    acceptedAlignment = null;
                    confirmedVariantGroup = null;
                }
                else if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                {
                    status = MapAutomaticIdentityStatus.TimedOut;
                    failureReason = "自动识别超过时间预算，请下次开图重试。";
                    acceptedAlignment = null;
                    confirmedVariantGroup = null;
                }
            }
            timer.Stop();
            _diagnostics.TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
            if (acceptedAlignment?.Recognition is { } recognition)
            {
                // Preserve strict alignment metrics used by adaptive/session
                // consumers while reporting the whole-pool wall time.
                _diagnostics = acceptedAlignment.Diagnostics;
                _diagnostics.TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
                _diagnostics.ReadyMapCount = service.ReadyMapCount;
                _diagnostics.TotalMapCount = service.TotalMapCount;
                _diagnostics.ScanCandidateCount = work.Length;
                _diagnostics.ScanVerificationCandidateCount = comparedCount;
                _diagnostics.StructureAccepted = true;
                _diagnostics.IdentityConfidence =
                    recognition.Result.IdentityConfidence;
                _diagnostics.LocalizationConfidence =
                    recognition.Result.LocalizationConfidence;
                _diagnostics.StructureFailureReason = string.Empty;
            }

            _diagnostics.TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
            _diagnostics.ReadyMapCount = service.ReadyMapCount;
            _diagnostics.TotalMapCount = service.TotalMapCount;
            _diagnostics.ScanCandidateCount = work.Length;
            _diagnostics.ScanVerificationCandidateCount = comparedCount;
            _diagnostics.ScanVpsgAttemptCount = work.Count(item =>
                item.VpsgAttempted);
            _diagnostics.ScanFormalStructureAttemptCount = work.Sum(item => item.StrictExecutionCount);
            _diagnostics.ScanFormalStructureCompletedCount = work.Sum(item => item.StrictExecutedResults.Count);
            _diagnostics.ScanFormalStructureAcceptedCount = work.Sum(item =>
                item.StrictExecutedResults.Count(result => result.StructureAccepted));
            _diagnostics.ScanTotalVerificationMilliseconds =
                timer.Elapsed.TotalMilliseconds;
            _diagnostics.StructureAttempted = work.Any(item =>
                item.StrictValidationAttempted);
            _diagnostics.StructureAccepted = status == MapAutomaticIdentityStatus.Accepted;
            _diagnostics.StructureFailureReason = failureReason;

            // Group identity does not replace the verified member or transform.
            // Only the group selected from the same catalog snapshot is passed
            // by the caller; failed attempts never publish confirmation metadata.
            var confirmed = status == MapAutomaticIdentityStatus.Accepted
                && acceptedAlignment?.StructureAccepted == true
                && acceptedAlignment.Recognition is { } acceptedRecognition
                && confirmedVariantGroup is { Id: var groupId }
                && groupId != Guid.Empty
                && confirmedVariantGroup.MapIds.Contains(acceptedRecognition.Map.Id)
                    ? confirmedVariantGroup : null;
            Guid[] confirmedMembers = [];
            if (confirmed is not null)
            {
                var mapOrder = work.Select(item => item.Map).DistinctBy(map => map.Id)
                    .ToDictionary(map => map.Id, map => map.SequenceNumber);
                confirmedMembers = confirmed.MapIds.Distinct()
                    .OrderBy(id => mapOrder.TryGetValue(id, out var sequence) ? sequence : int.MaxValue)
                    .ThenBy(id => id).ToArray();
            }

            return new MapAutomaticIdentityAttempt
            {
                Status = status,
                AlignmentAttempt = acceptedAlignment,
                ConfirmedVariantGroupId = confirmed?.Id,
                ConfirmedVariantMemberId = confirmed is not null
                    && acceptedAlignment?.Recognition?.Map.Id == confirmedVariantMemberId
                    && confirmed.MapIds.Contains(confirmedVariantMemberId ?? Guid.Empty)
                        ? confirmedVariantMemberId : null,
                ConfirmedVariantMapIds = confirmedMembers,
                CatalogRevision = snapshotRevision,
                CaptureSystemRelativeTicks = frame.CaptureSystemRelativeTicks,
                MapClass = mapClass,
                RequestedFloorKey = requestedFloorKey,
                CandidateCount = work.Length,
                ComparedCount = comparedCount,
                FailureReason = failureReason,
                Diagnostics = _diagnostics,
                CandidateDiagnostics = work
                    .Select(item => item.ToDiagnostic())
                    .ToArray()
            };
        }
    }
}
