namespace IDVBuff.Features.Maps;

public enum MapCandidateDecisionKind
{
    SelectKnownMap,
    StartSurvey,
    Cancel
}

public readonly record struct MapCandidateDecision(
    MapCandidateDecisionKind Kind,
    int? CandidateIndex = null)
{
    public static MapCandidateDecision SelectKnownMap(int candidateIndex) =>
        new(MapCandidateDecisionKind.SelectKnownMap, candidateIndex);

    public static MapCandidateDecision StartSurvey() =>
        new(MapCandidateDecisionKind.StartSurvey);

    public static MapCandidateDecision Cancel() =>
        new(MapCandidateDecisionKind.Cancel);
}

/// <summary>
/// Selects one candidate while a scan is still active. GUI callers can leave
/// this unset and use the native candidate window; automation callers inject
/// an implementation so the scan does not block waiting for UI input.
/// </summary>
public interface IMapCandidateSelector
{
    Task<MapCandidateDecision> SelectAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> candidates,
        string reason,
        CancellationToken cancellationToken);
}
