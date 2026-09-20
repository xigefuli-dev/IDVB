using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;

public sealed partial class SurveyCoordinator
{
    private async Task<SurveyObservationCommitResult> ProcessRootObservationAsync(
        SurveyObservationCommitResult committed,
        CancellationToken cancellationToken)
    {
        if (_preprocessor is null)
            return committed;
        try
        {
            var preprocessing = await _preprocessor.ProcessAsync(
                new SurveyPreprocessRequest(committed.Snapshot.Project.ProjectId, committed.Observation),
                cancellationToken).ConfigureAwait(false);
            var snapshot = await _projects.CommitProcessingAsync(
                new SurveyProcessingCommitRequest(
                    Guid.NewGuid(),
                    committed.Snapshot.Project.ProjectId,
                    committed.Observation.ObservationId,
                    committed.Layer.LayerId,
                    committed.Snapshot.Project.Revision,
                    SurveyObservationState.Registered,
                    preprocessing.Quality,
                    preprocessing.IsUsable ? SurveyErrorCode.None : SurveyErrorCode.PreprocessingFailed,
                    preprocessing.RejectionReason,
                    SurveyLayerTransform.Identity,
                    null,
                    preprocessing.StructureAsset,
                    preprocessing.FeatureAsset,
                    preprocessing.DisplayAsset,
                    preprocessing.VisibleMaskAsset),
                cancellationToken).ConfigureAwait(false);
            return new SurveyObservationCommitResult(
                snapshot,
                snapshot.Observations.Single(item => item.ObservationId == committed.Observation.ObservationId),
                snapshot.Layers.Single(item => item.LayerId == committed.Layer.LayerId),
                false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The root layer is already durable and geometrically valid. A
            // derived-asset failure must not remove or demote it.
            return committed;
        }
    }

    private async Task<SurveyObservationCommitResult> ProcessObservationAsync(
        SurveyObservationCommitResult committed,
        CancellationToken cancellationToken)
    {
        if (_preprocessor is null)
            return committed;
        SurveyPreprocessResult? preprocessing = null;
        try
        {
            preprocessing = await _preprocessor.ProcessAsync(
                new SurveyPreprocessRequest(committed.Snapshot.Project.ProjectId, committed.Observation),
                cancellationToken).ConfigureAwait(false);
            if (!preprocessing.IsUsable)
            {
                return await CommitRejectedProcessingAsync(
                    committed,
                    preprocessing.Quality,
                    SurveyErrorCode.PreprocessingFailed,
                    preprocessing.RejectionReason,
                    preprocessing.StructureAsset,
                    preprocessing.FeatureAsset,
                    preprocessing.DisplayAsset,
                    preprocessing.VisibleMaskAsset,
                    cancellationToken).ConfigureAwait(false);
            }
            if (_registrar is null)
            {
                return await CommitRejectedProcessingAsync(
                    committed,
                    preprocessing.Quality,
                    SurveyErrorCode.RegistrationRejected,
                    "当前运行环境没有自动配准器，图层已清洗并保留供手动对齐。",
                    preprocessing.StructureAsset,
                    preprocessing.FeatureAsset,
                    preprocessing.DisplayAsset,
                    preprocessing.VisibleMaskAsset,
                    cancellationToken).ConfigureAwait(false);
            }

            var candidates = SelectRegistrationCandidates(committed);
            SurveyRegistrationResult? bestAccepted = null;
            SurveyMapLayer? bestTargetLayer = null;
            string? lastRejection = null;
            foreach (var candidate in candidates)
            {
                var result = await _registrar.RegisterAsync(
                    new SurveyRegistrationRequest(
                        committed.Observation,
                        candidate.Observation,
                        candidate.Layer),
                    cancellationToken).ConfigureAwait(false);
                if (result.IsAccepted
                    && (bestAccepted is null || result.Confidence > bestAccepted.Confidence))
                {
                    bestAccepted = result;
                    bestTargetLayer = candidate.Layer;
                }
                else if (!result.IsAccepted)
                {
                    lastRejection = result.RejectionReason;
                }
            }

            if (bestAccepted is null || bestTargetLayer is null)
            {
                return await CommitRejectedProcessingAsync(
                    committed,
                    preprocessing.Quality,
                    SurveyErrorCode.RegistrationRejected,
                    lastRejection ?? "没有同楼层的可靠配准候选。",
                    preprocessing.StructureAsset,
                    preprocessing.FeatureAsset,
                    preprocessing.DisplayAsset,
                    preprocessing.VisibleMaskAsset,
                    cancellationToken).ConfigureAwait(false);
            }

            var automatic = Compose(
                bestTargetLayer.AutomaticTransform,
                bestAccepted.RelativeTransform);
            var constraint = new SurveyConstraint(
                Guid.NewGuid(),
                committed.Snapshot.Project.ProjectId,
                committed.Layer.FloorId,
                committed.Layer.LayerId,
                bestTargetLayer.LayerId,
                bestAccepted.RelativeTransform,
                bestAccepted.Confidence,
                bestAccepted.Residual,
                bestAccepted.InlierCount,
                bestAccepted.AlgorithmId,
                bestAccepted.AlgorithmVersion,
                true,
                null);
            var snapshot = await _projects.CommitProcessingAsync(
                new SurveyProcessingCommitRequest(
                    Guid.NewGuid(),
                    committed.Snapshot.Project.ProjectId,
                    committed.Observation.ObservationId,
                    committed.Layer.LayerId,
                    committed.Snapshot.Project.Revision,
                    SurveyObservationState.Registered,
                    preprocessing.Quality,
                    SurveyErrorCode.None,
                    null,
                    automatic,
                    constraint,
                    preprocessing.StructureAsset,
                    preprocessing.FeatureAsset,
                    preprocessing.DisplayAsset,
                    preprocessing.VisibleMaskAsset),
                cancellationToken).ConfigureAwait(false);
            var observation = snapshot.Observations.Single(
                item => item.ObservationId == committed.Observation.ObservationId);
            var layer = snapshot.Layers.Single(item => item.LayerId == committed.Layer.LayerId);
            return new SurveyObservationCommitResult(snapshot, observation, layer, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The raw observation and editable layer are already durable. Algorithm
            // failures are recoverable and must never turn capture into data loss.
            return await CommitRejectedProcessingAsync(
                committed,
                0d,
                SurveyErrorCode.RegistrationRejected,
                exception.Message,
                preprocessing?.StructureAsset,
                preprocessing?.FeatureAsset,
                preprocessing?.DisplayAsset,
                preprocessing?.VisibleMaskAsset,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<RegistrationCandidate> SelectRegistrationCandidates(
        SurveyObservationCommitResult committed)
    {
        var observations = committed.Snapshot.Observations.ToDictionary(item => item.ObservationId);
        return committed.Snapshot.Layers
            .Where(item =>
                item.FloorId == committed.Layer.FloorId
                && item.LayerId != committed.Layer.LayerId
                && !item.IsDeleted
                && observations.TryGetValue(item.ObservationId, out var observation)
                && observation.State == SurveyObservationState.Registered)
            .OrderByDescending(item => observations[item.ObservationId].Capture.CapturedAt)
            .Take(_registrationTuning.CandidateCount)
            .Select(item => new RegistrationCandidate(observations[item.ObservationId], item))
            .ToArray();
    }

    private async Task<SurveyObservationCommitResult> CommitRejectedProcessingAsync(
        SurveyObservationCommitResult committed,
        double quality,
        SurveyErrorCode errorCode,
        string? reason,
        SurveyAssetReference? structureAsset,
        SurveyAssetReference? featureAsset,
        SurveyAssetReference? displayAsset,
        SurveyAssetReference? visibleMaskAsset,
        CancellationToken cancellationToken)
    {
        var snapshot = await _projects.CommitProcessingAsync(
            new SurveyProcessingCommitRequest(
                Guid.NewGuid(),
                committed.Snapshot.Project.ProjectId,
                committed.Observation.ObservationId,
                committed.Layer.LayerId,
                committed.Snapshot.Project.Revision,
                SurveyObservationState.Unregistered,
                Math.Clamp(quality, 0d, 1d),
                errorCode,
                reason,
                committed.Layer.AutomaticTransform,
                null,
                structureAsset,
                featureAsset,
                displayAsset,
                visibleMaskAsset),
            cancellationToken).ConfigureAwait(false);
        return new SurveyObservationCommitResult(
            snapshot,
            snapshot.Observations.Single(item => item.ObservationId == committed.Observation.ObservationId),
            snapshot.Layers.Single(item => item.LayerId == committed.Layer.LayerId),
            false);
    }

    private static SurveyLayerTransform Compose(
        SurveyLayerTransform parent,
        SurveyLayerTransform child)
    {
        var translation = parent.Transform(new SurveyWorldPoint(
            child.TranslationX,
            child.TranslationY));
        return new SurveyLayerTransform(
            translation.X,
            translation.Y,
            parent.RotationDegrees + child.RotationDegrees,
            parent.ScaleX * child.ScaleX,
            parent.ScaleY * child.ScaleY);
    }

    private sealed record RegistrationCandidate(
        SurveyObservation Observation,
        SurveyMapLayer Layer);
}
