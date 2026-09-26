namespace IDVBuff.Features.Maps;

internal static class FloorIndicatorCaptureRegion
{
    // Game UI fits the reference canvas. Width alone enlarges the template on
    // ultrawide/short windows; height alone enlarges it on 16:10 windows.
    public static double TemplateScale(FloorIndicatorTemplateRegistry.Group group,
        MapScreenRect clientBounds) => Math.Min(
            clientBounds.Width / group.ReferenceClientWidth,
            clientBounds.Height / (group.PixelHeight / group.Height));

    // Map calibration must not truncate the independent floor-indicator UI.
    // The registered header extent also works when a new resolution falls
    // back to a full-client map viewport (Y = 0).
    public static NormalizedRectangle Above(NormalizedRectangle viewport,
        FloorIndicatorTemplateRegistry.Group? group = null) => new()
    {
        X = 0, Y = 0, Width = 1,
        Height = Math.Clamp(Math.Max(viewport.Y,
            group is null ? 0 : group.Y + group.Height), 0, 1)
    };

    public static NormalizedRectangle IncludeMap(NormalizedRectangle viewport,
        FloorIndicatorTemplateRegistry.Group? group = null) => new()
    {
        X = 0, Y = 0, Width = 1,
        Height = Math.Clamp(Math.Max(viewport.Y + viewport.Height,
            Above(viewport, group).Height), 0, 1)
    };
}
