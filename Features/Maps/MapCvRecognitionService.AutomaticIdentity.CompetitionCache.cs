using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private static bool AutomaticIdentityCompetitionBudgetExhausted() =>
        MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0;

    private sealed class AutomaticIdentityCompetitionFloorCache(
        Vpsg3PreparedFloor floor,
        byte[] exactEdgeBitmap)
    {
        public Vpsg3PreparedFloor Floor { get; } = floor;
        public byte[] ExactEdgeBitmap { get; } = exactEdgeBitmap;
    }

    private sealed class AutomaticIdentityCompetitionPoseCache(
        AutomaticIdentityCompetitionFloorCache floorCache,
        MapOverlayTransform transform,
        Point[] observedReferencePoints,
        int[] neighborhoodOffsets,
        int[] neighborhoodWallIndices)
    {
        private Point[]? _observedReferencePoints = observedReferencePoints;

        public AutomaticIdentityCompetitionFloorCache FloorCache { get; } = floorCache;
        public Vpsg3PreparedFloor Floor => FloorCache.Floor;
        public MapOverlayTransform Transform { get; } = transform;
        public double InverseScaleX { get; } = 1.0 / transform.ScaleX;
        public double InverseScaleY { get; } = 1.0 / transform.ScaleY;
        public Point[] ObservedReferencePoints => _observedReferencePoints
            ?? throw new InvalidOperationException("Observed reference points were already released.");
        public int[] NeighborhoodOffsets { get; } = neighborhoodOffsets;
        public int[] NeighborhoodWallIndices { get; } = neighborhoodWallIndices;

        public void ReleaseObservedReferencePoints() => _observedReferencePoints = null;
    }

    private static byte[]? BuildAutomaticIdentityExactEdgeBitmap(
        Vpsg3PreparedFloor floor, CancellationToken cancellationToken)
    {
        var width = floor.ReferenceWidth;
        var exactEdgeBitmap = new byte[checked(width * floor.ReferenceHeight)];
        var edgeIndex = 0;
        foreach (var wall in floor.ReferenceEdgePoints.Span)
        {
            if ((edgeIndex++ & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AutomaticIdentityCompetitionBudgetExhausted())
                    return null;
            }
            exactEdgeBitmap[wall.Y * width + wall.X] = 1;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return AutomaticIdentityCompetitionBudgetExhausted() ? null : exactEdgeBitmap;
    }

    private static AutomaticIdentityCompetitionPoseCache? BuildAutomaticIdentityPoseCache(
        Vpsg3LiveObservation observation,
        IReadOnlyList<Point> points,
        AutomaticIdentityCompetitionFloorCache floorCache,
        MapOverlayTransform transform,
        CancellationToken cancellationToken)
    {
        var floor = floorCache.Floor;
        var width = floor.ReferenceWidth;
        var height = floor.ReferenceHeight;
        var inverseScaleX = 1.0 / transform.ScaleX;
        var inverseScaleY = 1.0 / transform.ScaleY;
        var observedReferencePoints = new Point[points.Count];
        var neighborhoodOffsets = new int[points.Count + 1];
        var neighborhoodWallIndices = new List<int>();
        var neighborhoodChecks = 0;
        for (var index = 0; index < points.Count; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AutomaticIdentityCompetitionBudgetExhausted())
                    return null;
            }
            var point = points[index];
            var x = (int)Math.Round((observation.ViewportBounds.X + point.X
                - transform.OffsetX) * inverseScaleX);
            var y = (int)Math.Round((observation.ViewportBounds.Y + point.Y
                - transform.OffsetY) * inverseScaleY);
            observedReferencePoints[index] = new(x, y);
            neighborhoodOffsets[index] = neighborhoodWallIndices.Count;

            // Cache only exact source-edge cells which a current observed point
            // can read from the original inclusive +/-2 K5 neighborhood.
            for (var sy = Math.Max(0, y - 2); sy <= Math.Min(height - 1, y + 2); sy++)
            for (var sx = Math.Max(0, x - 2); sx <= Math.Min(width - 1, x + 2); sx++)
            {
                if ((neighborhoodChecks++ & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AutomaticIdentityCompetitionBudgetExhausted())
                        return null;
                }
                var wallIndex = sy * width + sx;
                if (floorCache.ExactEdgeBitmap[wallIndex] != 0)
                    neighborhoodWallIndices.Add(wallIndex);
            }
        }
        neighborhoodOffsets[points.Count] = neighborhoodWallIndices.Count;
        cancellationToken.ThrowIfCancellationRequested();
        if (AutomaticIdentityCompetitionBudgetExhausted())
            return null;
        return new(floorCache, transform, observedReferencePoints,
            neighborhoodOffsets, neighborhoodWallIndices.ToArray());
    }
}
