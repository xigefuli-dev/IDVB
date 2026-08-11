using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;

public sealed partial class SurveyCoordinator
{
    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> ReorderLayersAsync(
        SurveyLayerOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _projects.GetAsync(request.ProjectId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            if (current.Project.State == SurveyProjectState.Archived)
            {
                return Failure<SurveyProjectSnapshot>(
                    SurveyErrorCode.ProjectArchived,
                    "Archived survey projects are read-only.");
            }
            var snapshot = await _projects.ReorderLayersAsync(request, cancellationToken)
                .ConfigureAwait(false);
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
