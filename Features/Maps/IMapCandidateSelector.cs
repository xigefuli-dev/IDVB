namespace IDVBuff.Features.Maps;

/// <summary>
/// Selects one candidate while a scan is still active. GUI callers can leave
/// this unset and use the native candidate window; automation callers inject
/// an implementation so the scan does not block waiting for UI input.
/// </summary>
public interface IMapCandidateSelector
{
    Task<int?> SelectAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> candidates,
        string reason,
        CancellationToken cancellationToken);
}
