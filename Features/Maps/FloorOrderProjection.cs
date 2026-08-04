namespace IDVBuff.Features.Maps;

/// <summary>Creates the row-major order shown while a floor card is being dragged.</summary>
internal static class FloorOrderProjection
{
    public static IReadOnlyList<T> MoveToInsertion<T>(
        IReadOnlyList<T> entries,
        T entry,
        int insertionIndex)
    {
        var currentIndex = -1;
        for (var index = 0; index < entries.Count; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(entries[index], entry))
                continue;
            currentIndex = index;
            break;
        }

        if (currentIndex < 0)
            return entries.ToArray();

        var projected = entries.ToList();
        projected.RemoveAt(currentIndex);
        projected.Insert(Math.Clamp(insertionIndex, 0, projected.Count), entry);
        return projected;
    }
}
