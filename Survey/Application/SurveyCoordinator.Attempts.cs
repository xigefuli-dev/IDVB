using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;

public sealed partial class SurveyCoordinator
{
    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> RecordCaptureFailureAsync(
        SurveyCaptureFailureRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _projects.RecordCaptureFailureAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (_activeProjectId == request.ProjectId)
            {
                SetStatus(CreateStatus(snapshot, SurveyRuntimeState.WaitingForNextOpen, request.Message) with
                {
                    LastErrorCode = request.ErrorCode
                });
            }
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
}
