namespace IDVBuff.Features.Maps;

internal static class FloorIndicatorCaptureRegion
{
    // The header is a separate logical ROI in the same captured frame.
    public static NormalizedRectangle Above(NormalizedRectangle viewport) => new()
    { X = 0, Y = 0, Width = 1, Height = Math.Clamp(viewport.Y, 0, 1) };

    public static NormalizedRectangle IncludeMap(NormalizedRectangle viewport) => new()
    { X = 0, Y = 0, Width = 1, Height = Math.Clamp(viewport.Y + viewport.Height, 0, 1) };
}
