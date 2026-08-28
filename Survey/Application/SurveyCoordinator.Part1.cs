using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;
public sealed partial class SurveyCoordinator : ISurveyCoordinator
{

    private static SurveyStatusSnapshot CreateStatus(
        SurveyProjectSnapshot snapshot,
        SurveyRuntimeState runtimeState,
        string? message,
        bool isSaving = false,
        DateTimeOffset? lastCaptureAt = null)
    {
        var activeLayers = snapshot.Layers.Where(layer => !layer.IsDeleted).ToArray();
        var observationsById = snapshot.Observations.ToDictionary(item => item.ObservationId);
        return new SurveyStatusSnapshot(
            snapshot.Project.ProjectId,
            snapshot.Project.Name,
            snapshot.Project.State,
            runtimeState,
            snapshot.Project.ActiveFloorKey,
            snapshot.Observations.Count,
            activeLayers.Count(layer =>
                observationsById.TryGetValue(layer.ObservationId, out var observation)
                && observation.State == SurveyObservationState.Registered),
            activeLayers.Count(layer =>
                observationsById.TryGetValue(layer.ObservationId, out var observation)
                && observation.State == SurveyObservationState.Unregistered),
            snapshot.Layers.Count(layer => layer.IsDeleted),
            0,
            lastCaptureAt,
            snapshot.Project.Revision,
            isSaving,
            SurveyErrorCode.None,
            message,
            null);
    }

    private void SetStatus(SurveyStatusSnapshot status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _gate.Dispose();
        StatusChanged = null;
        return ValueTask.CompletedTask;
    }
}
