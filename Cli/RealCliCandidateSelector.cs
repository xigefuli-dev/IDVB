using IDVBuff.Features.Maps;

namespace IDVBuff.Cli;

/// <summary>
/// RealCLI's implementation of the in-scan candidate-selection contract.
/// Positions are one-based because that is what the CLI displays to users.
/// </summary>
internal sealed class RealCliCandidateSelector(
    int? requestedPosition,
    Guid? requestedMapId = null)
    : IMapCandidateSelector
{
    public Task<MapCandidateDecision> SelectAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> candidates,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count == 0)
            return Task.FromResult(MapCandidateDecision.Cancel());

        if (requestedMapId is { } mapId)
        {
            var index = candidates.ToList().FindIndex(candidate =>
                candidate.Recognition.Map.Id == mapId);
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestedMapId),
                    $"--map-id {mapId:D} 不在当前 Class 候选中。");
            }
            return Task.FromResult(MapCandidateDecision.SelectKnownMap(index));
        }

        if (requestedPosition is { } position)
        {
            if (position < 1 || position > candidates.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestedPosition),
                    $"--candidate 必须在 1 到 {candidates.Count} 之间。");
            }
            return Task.FromResult(MapCandidateDecision.SelectKnownMap(position - 1));
        }

        var hasVerifiedCandidate = candidates.Any(candidate =>
            !candidate.IsReferenceOnly);
        var bestIndex = -1;
        for (var index = 0; index < candidates.Count; index++)
        {
            if (hasVerifiedCandidate && candidates[index].IsReferenceOnly)
                continue;

            if (bestIndex < 0
                || candidates[index].PreferredOrder
                    < candidates[bestIndex].PreferredOrder
                || (candidates[index].PreferredOrder
                        == candidates[bestIndex].PreferredOrder
                    && candidates[index].RawConfidence
                        > candidates[bestIndex].RawConfidence))
            {
                bestIndex = index;
            }
        }
        return Task.FromResult(MapCandidateDecision.SelectKnownMap(bestIndex));
    }
}
