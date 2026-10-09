namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private sealed record AutomaticIdentityReadinessCandidate(
        AutomaticIdentityJob Job,
        MapAutomaticIdentityAttempt Attempt,
        RuntimeMapRecognition Identity,
        MapViewportColorSignature Signature,
        string FloorKey);

    private AutomaticIdentityReadinessCandidate?
        TryGetAcceptedAutomaticIdentityReadinessCandidate(
            MapMatchSnapshot match,
            string? requestedFloor,
            FloorIndicatorTemplateRegistry.Group? identityFloorGroup)
    {
        // A current-frame floor check is required before this job-local
        // reference can be used. If the class has no shared indicator layout,
        // retain the existing stable-capture path.
        if (identityFloorGroup is null
            || Volatile.Read(ref _automaticIdentityJob) is not { } job
            || job.Match != match
            || job.Generation != Volatile.Read(ref _automaticIdentityInvalidation)
            || !job.Task.IsCompletedSuccessfully
            || !job.CanUseCatalog(_recognition.CatalogRevision))
            return null;

        var attempt = job.Task.Result;
        var identity = attempt.Recognition;
        var floor = identity?.Result.Floor;
        var signature = job.ReadinessSignature;
        var currentRevision = _recognition.CatalogRevision;
        if (!attempt.Accepted
            || signature is null
            || identity is null
            || string.IsNullOrWhiteSpace(floor)
            || !string.Equals(job.Floor, floor, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(requestedFloor)
                && !string.Equals(requestedFloor, floor, StringComparison.OrdinalIgnoreCase))
            || attempt.CatalogRevision != currentRevision
            || attempt.CatalogRevision != _mapRepository.GetCatalogRevision()
            // Low-structure channels deliberately keep their existing
            // consecutive-structure readiness requirement.
            || MapAlignmentChannelRegistry.Resolve(identity.Map, floor).Channel
                == MapAlignmentChannel.LowStructure)
            return null;

        return new AutomaticIdentityReadinessCandidate(
            job, attempt, identity, signature, floor);
    }

    private async Task<CapturedGameFrame?>
        CaptureAcceptedJobReadyAutomaticIdentityFrameAsync(
            AutomaticIdentityReadinessCandidate candidate,
            FloorIndicatorTemplateRegistry.Group identityFloorGroup,
            CancellationToken cancellationToken,
            Func<bool> shouldContinue)
    {
        // Reuse the stable capture wrapper for viewport preparation, resolution
        // updates and diagnostics. Its existing ready helper crops this frame
        // before signing so it matches the job's frozen source dimensions.
        try
        {
            return await CaptureStableViewportAsync(
                "自动识别引用就绪",
                cancellationToken,
                relaxForLockedMap: true,
                shouldContinue: shouldContinue,
                useAutomaticViewport: true,
                identityFloorGroup: identityFloorGroup,
                readinessReferenceSignature: candidate.Signature,
                cropAutomaticMapContentBeforeReadiness: true,
                readinessMaximumAttempts: 1,
                requiredDetectedFloor: candidate.FloorKey).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _lastStableCaptureFailureReason = "就绪参考采集已结束。";
            return null;
        }
        catch (Exception exception)
        {
            // This optional handoff must not turn a usable accepted identity
            // into a capture failure. The caller checks operation liveness
            // before choosing the established stable-capture fallback.
            _lastStableCaptureFailureReason =
                $"就绪参考采集失败：{exception.Message}";
            return null;
        }
    }

    private bool IsAcceptedAutomaticIdentityReadinessCandidateCurrent(
        AutomaticIdentityReadinessCandidate candidate,
        MapMatchSnapshot match,
        string? requestedFloor,
        CapturedGameFrame currentFrame)
    {
        return IsAutomaticIdentityReadinessReferenceCurrent(
                candidate, match, requestedFloor)
            && string.Equals(currentFrame.DetectedFloorKey, candidate.FloorKey,
                StringComparison.OrdinalIgnoreCase);
    }

    private CapturedGameFrame? TryCreateAcceptedJobReadinessFrame(
        AutomaticIdentityReadinessCandidate candidate,
        MapMatchSnapshot match,
        string? requestedFloor,
        CapturedGameFrame currentFrame)
    {
        if (!IsAcceptedAutomaticIdentityReadinessCandidateCurrent(
                candidate, match, requestedFloor, currentFrame))
            return null;

        var probeFrame = new CapturedGameFrame(
            currentFrame.Image.Clone(),
            currentFrame.ClientBounds,
            currentFrame.ViewportBounds,
            currentFrame.WindowHandle)
        {
            CaptureBackend = currentFrame.CaptureBackend,
            CaptureSystemRelativeTicks = currentFrame.CaptureSystemRelativeTicks,
            CaptureReadbackMilliseconds = currentFrame.CaptureReadbackMilliseconds,
            CaptureDroppedFrames = currentFrame.CaptureDroppedFrames,
            DetectedFloorKey = currentFrame.DetectedFloorKey,
            UiExclusionRegions = currentFrame.UiExclusionRegions
        };
        CapturedGameFrame? ownedProbe = probeFrame;
        CapturedGameFrame? cropped = null;
        try
        {
            probeFrame.RetainDiagnosticSource(currentFrame);
            var croppedFrame = CropAutomaticMapContent(probeFrame);
            ownedProbe = null;
            cropped = croppedFrame;

            // Cropping and signature extraction take time. Revalidate at the
            // comparison point so a cleared job or changed catalog cannot
            // authorize this current frame through a stale reference.
            if (!IsAcceptedAutomaticIdentityReadinessCandidateCurrent(
                    candidate, match, requestedFloor, currentFrame))
                return null;

            var signature = MapViewportPresenceDetector.CreateSignature(croppedFrame.Image);
            if (!IsAcceptedAutomaticIdentityReadinessCandidateCurrent(
                    candidate, match, requestedFloor, currentFrame)
                || !MapViewportPresenceDetector.EvaluateReady(
                    signature, candidate.Signature).IsPresent)
                return null;

            var readyFrame = croppedFrame;
            cropped = null;
            return readyFrame;
        }
        catch (Exception)
        {
            // A failed optional comparison leaves the original tracker path
            // untouched; only this independent probe frame is discarded.
            return null;
        }
        finally
        {
            ownedProbe?.Dispose();
            cropped?.Dispose();
        }
    }

    private bool IsAutomaticIdentityReadinessReferenceCurrent(
        AutomaticIdentityReadinessCandidate candidate,
        MapMatchSnapshot match,
        string? requestedFloor)
    {
        return ReferenceEquals(_automaticIdentityJob, candidate.Job)
            && candidate.Job.Task.IsCompletedSuccessfully
            && ReferenceEquals(candidate.Job.Task.Result, candidate.Attempt)
            && candidate.Job.Match == match
            && candidate.Job.Generation == Volatile.Read(ref _automaticIdentityInvalidation)
            && IsCurrentMatchOperation(match)
            && candidate.Job.CanUseCatalog(_recognition.CatalogRevision)
            && candidate.Attempt.Accepted
            && candidate.Attempt.CatalogRevision == _recognition.CatalogRevision
            && candidate.Attempt.CatalogRevision == _mapRepository.GetCatalogRevision()
            && ReferenceEquals(candidate.Attempt.Recognition, candidate.Identity)
            && string.Equals(candidate.Job.Floor, candidate.FloorKey,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Identity.Result.Floor, candidate.FloorKey,
                StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(requestedFloor)
                || string.Equals(requestedFloor, candidate.FloorKey,
                    StringComparison.OrdinalIgnoreCase));
    }
}
