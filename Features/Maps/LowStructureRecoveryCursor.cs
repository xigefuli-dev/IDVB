namespace IDVBuff.Features.Maps;

/// <summary>
/// Bounded recovery cursor. The caller scopes its identity to one exact map,
/// floor, match, resolution and configuration so closing/reopening the map can
/// continue the unfinished bounded grid without leaking evidence across
/// matches or floors.
/// </summary>
internal sealed class LowStructureRecoveryCursor
{
    private readonly object _gate = new();
    private string? _operationKey;
    private readonly HashSet<double> _searched = [];

    internal void Reset()
    {
        lock (_gate)
        {
            _operationKey = null;
            _searched.Clear();
        }
    }

    internal IReadOnlyList<double> TakeBatch(
        string operationKey,
        IReadOnlyList<double> fullGrid,
        int maximumScales)
    {
        lock (_gate)
        {
            if (!string.Equals(_operationKey, operationKey, StringComparison.Ordinal))
            {
                _operationKey = operationKey;
                _searched.Clear();
            }
            var batch = fullGrid
                .Where(scale => double.IsFinite(scale) && !_searched.Contains(scale))
                .Take(Math.Clamp(maximumScales, 1, 3))
                .ToArray();
            foreach (var scale in batch)
                _searched.Add(scale);
            return batch;
        }
    }

    internal void MarkSearched(string operationKey, IEnumerable<double> scales)
    {
        lock (_gate)
        {
            if (!string.Equals(_operationKey, operationKey, StringComparison.Ordinal))
            {
                _operationKey = operationKey;
                _searched.Clear();
            }
            foreach (var scale in scales.Where(double.IsFinite))
                _searched.Add(scale);
        }
    }
}
