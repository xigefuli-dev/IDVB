using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;

public sealed partial class SurveyCoordinator
{
    public async Task SetRuntimeStateAsync(
        Guid projectId,
        SurveyRuntimeState state,
        string? message,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeProjectId != projectId)
                return;
            SetStatus(Status with
            {
                RuntimeState = state,
                LastMessage = message,
                LastErrorCode = SurveyErrorCode.None,
                IsSaving = state == SurveyRuntimeState.Committing
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> SetProjectStateAsync(
        SurveyProjectStateRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _projects.SetProjectStateAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (_activeProjectId == request.ProjectId)
                SetStatus(CreateStatus(snapshot, Status.RuntimeState, "测绘项目状态已更新。"));
            return SurveyOperationResult<SurveyProjectSnapshot>.Success(snapshot);
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<SurveyProjectSnapshot>(SurveyErrorCode.StorageUnavailable, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> EndAsync(
        SurveyEndRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeProjectId != request.ProjectId || _activeMatchId != request.MatchId)
            {
                return Failure<SurveyProjectSnapshot>(
                    SurveyErrorCode.InvalidState,
                    "结束请求不属于当前测绘对局。");
            }

            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            SetStatus(CreateStatus(current, SurveyRuntimeState.Ending, "正在结束测绘对局…", true));
            var nextState = current.Layers.Any(layer => !layer.IsDeleted)
                ? SurveyProjectState.NeedsReview
                : SurveyProjectState.Draft;
            var snapshot = await _projects.SetProjectStateAsync(
                new SurveyProjectStateRequest(
                    request.CommandId,
                    request.ProjectId,
                    request.ExpectedRevision,
                    nextState),
                cancellationToken).ConfigureAwait(false);
            _activeProjectId = null;
            _activeMatchId = null;
            _activeOperationEpoch = 0;
            SetStatus(CreateStatus(snapshot, SurveyRuntimeState.Inactive, "测绘项目已保存。"));
            return SurveyOperationResult<SurveyProjectSnapshot>.Success(snapshot);
        }
        catch (Exception exception)
        {
            return Fault<SurveyProjectSnapshot>(SurveyErrorCode.StorageUnavailable, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<SurveyProjectSnapshot?> GetProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        _projects.GetAsync(projectId, cancellationToken);

    public Task<Stream> OpenAssetAsync(
        Guid projectId,
        SurveyAssetReference asset,
        CancellationToken cancellationToken = default) =>
        _assets.OpenReadAsync(projectId, asset, cancellationToken);

    public Task<IReadOnlyList<SurveyProjectSummary>> ListProjectsAsync(
        CancellationToken cancellationToken = default) =>
        _projects.ListAsync(cancellationToken);
}
