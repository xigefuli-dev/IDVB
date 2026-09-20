using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;

public sealed partial class SurveyCoordinator
{
    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> RenameProjectAsync(
        SurveyProjectRenameRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _projects.RenameAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (_activeProjectId == request.ProjectId)
                SetStatus(CreateStatus(snapshot, Status.RuntimeState, "测绘项目已重命名。"));
            return SurveyOperationResult<SurveyProjectSnapshot>.Success(snapshot);
        }
        catch (ArgumentException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.InvalidState, exception.Message);
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (SurveyProjectNotFoundException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.ProjectNotFound, exception.Message);
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

    public async Task<SurveyOperationResult<bool>> DeleteProjectAsync(
        SurveyProjectDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeProjectId == request.ProjectId)
            {
                return Failure<bool>(
                    SurveyErrorCode.InvalidState,
                    "正在进行对局测绘的项目不能删除。请先结束当前对局。");
            }

            await _projects.DeleteAsync(request, cancellationToken).ConfigureAwait(false);
            if (ArmedResumeProjectId == request.ProjectId)
                ArmedResumeProjectId = null;
            return SurveyOperationResult<bool>.Success(true);
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<bool>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (SurveyProjectNotFoundException exception)
        {
            return Failure<bool>(SurveyErrorCode.ProjectNotFound, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<bool>(SurveyErrorCode.StorageUnavailable, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> UpdateMetadataAsync(
        SurveyProjectMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _projects.UpdateMetadataAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (_activeProjectId == request.ProjectId)
                SetStatus(CreateStatus(snapshot, Status.RuntimeState, "项目属性已保存。"));
            return SurveyOperationResult<SurveyProjectSnapshot>.Success(snapshot);
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.InvalidState, exception.Message);
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
}
