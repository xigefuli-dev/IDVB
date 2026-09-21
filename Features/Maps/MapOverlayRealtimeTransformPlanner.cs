namespace IDVBuff.Features.Maps;

/// <summary>
/// Allocation-free classification for the retained map layer hot path.
/// Translation preserves the baked bitmap; only a physical pixel-size change
/// requires a new layer upload.
/// </summary>
internal static class MapOverlayRealtimeTransformPlanner
{
    internal static bool RequiresBitmapRebuild(
        float currentWidth,
        float currentHeight,
        float nextWidth,
        float nextHeight) =>
        RoundToPixel(currentWidth) != RoundToPixel(nextWidth)
        || RoundToPixel(currentHeight) != RoundToPixel(nextHeight);

    internal static int RoundToPixel(float value) =>
        Math.Max(1, (int)Math.Round(value));
}
