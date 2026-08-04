namespace IDVBuff.Features.Maps;

/// <summary>Coordinate conversion used when the non-destructive recognition crop changes.</summary>
public static class MapRecognitionCoordinates
{
    public static void ApplyRecognitionRegion(
        FloorRecognitionProfile profile,
        NormalizedRectangle newRegion)
    {
        if (!newRegion.IsValid)
            throw new ArgumentException("Recognition region must be valid.", nameof(newRegion));

        var oldRegion = profile.GetEffectiveRecognitionRegion();
        foreach (var anchor in profile.Anchors.Where(anchor => anchor.Bounds?.IsValid is true))
        {
            var sourceBounds = ToSourceRectangle(anchor.Bounds!, oldRegion);
            anchor.Bounds = Contains(newRegion, sourceBounds)
                ? ToRegionRelativeRectangle(sourceBounds, newRegion)
                : null;
        }

        profile.RecognitionRegion = newRegion.Clone();
    }

    public static NormalizedRectangle ToSourceRectangle(
        NormalizedRectangle regionRelative,
        NormalizedRectangle region) => new()
    {
        X = region.X + (regionRelative.X * region.Width),
        Y = region.Y + (regionRelative.Y * region.Height),
        Width = regionRelative.Width * region.Width,
        Height = regionRelative.Height * region.Height
    };

    private static NormalizedRectangle ToRegionRelativeRectangle(
        NormalizedRectangle sourceRelative,
        NormalizedRectangle region) => new()
    {
        X = Math.Clamp((sourceRelative.X - region.X) / region.Width, 0d, 1d),
        Y = Math.Clamp((sourceRelative.Y - region.Y) / region.Height, 0d, 1d),
        Width = Math.Clamp(sourceRelative.Width / region.Width, 0d, 1d),
        Height = Math.Clamp(sourceRelative.Height / region.Height, 0d, 1d)
    };

    private static bool Contains(NormalizedRectangle outer, NormalizedRectangle inner)
    {
        const double epsilon = 0.000001d;
        return inner.X >= outer.X - epsilon
            && inner.Y >= outer.Y - epsilon
            && inner.X + inner.Width <= outer.X + outer.Width + epsilon
            && inner.Y + inner.Height <= outer.Y + outer.Height + epsilon;
    }
}
