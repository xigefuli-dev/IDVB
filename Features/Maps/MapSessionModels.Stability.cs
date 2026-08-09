namespace IDVBuff.Features.Maps;

public sealed class MapCandidateStabilityTracker
{
    private MapSimilarityTransform? _candidate;
    private int _count;
    private readonly List<MapSimilarityTransform> _history = [];

    public int Count => _count;
    public IReadOnlyList<MapSimilarityTransform> History =>
        _history.ToArray();

    /// <summary>
    /// Observes a candidate transform using the configured position tolerance.
    /// </summary>
    public bool Observe(MapSimilarityTransform candidate) =>
        Observe(candidate, MapSessionRules.PositionTolerancePixels);

    public bool Observe(MapSimilarityTransform candidate, double tolerancePixels)
    {
        if (!candidate.IsValid)
        {
            Reset();
            return false;
        }

        if (_candidate is null
            || Math.Abs(_candidate.TranslationX - candidate.TranslationX) > tolerancePixels
            || Math.Abs(_candidate.TranslationY - candidate.TranslationY) > tolerancePixels
            || Math.Abs((_candidate.Scale / candidate.Scale) - 1d) > MapSessionRules.ScaleToleranceRatio
            || Math.Abs(_candidate.RotationDegrees - candidate.RotationDegrees) > MapSessionRules.RotationToleranceDegrees)
        {
            _candidate = candidate;
            _count = 1;
            _history.Clear();
        }
        else
        {
            _candidate = candidate;
            _count++;
        }
        _history.Add(candidate);
        if (_history.Count > MapSessionRules.MaxHistoryEntries)
            _history.RemoveAt(0);
        return _count >= MapSessionRules.MediumConfidenceConfirmationFrames;
    }

    public void Reset()
    {
        _candidate = null;
        _count = 0;
        _history.Clear();
    }
}

/// <summary>
/// Debounces passive floor observations before they are allowed to invalidate
/// a trusted alignment. A missing or matching observation breaks the streak.
/// </summary>
public sealed class MapFloorChangeTracker
{
    private string? _candidateFloor;

    public int Count { get; private set; }
    public string? CandidateFloor => _candidateFloor;

    public bool Observe(
        string? lockedFloor,
        string? observedFloor,
        int requiredFrames = 3)
    {
        if (string.IsNullOrWhiteSpace(lockedFloor)
            || string.IsNullOrWhiteSpace(observedFloor)
            || string.Equals(
                lockedFloor,
                observedFloor,
                StringComparison.Ordinal))
        {
            Reset();
            return false;
        }

        if (!string.Equals(
                _candidateFloor,
                observedFloor,
                StringComparison.Ordinal))
        {
            _candidateFloor = observedFloor;
            Count = 1;
        }
        else
        {
            Count++;
        }
        return Count >= Math.Max(1, requiredFrames);
    }

    public void Reset()
    {
        _candidateFloor = null;
        Count = 0;
    }
}
