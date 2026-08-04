namespace IDVBuff.Features.Maps;

/// <summary>
/// Extended styles owned by the overlay's input behavior. Rendering styles
/// remain owned by WinUI and the compositor.
/// </summary>
internal static class MapOverlayWindowStyles
{
    internal const int GwlExStyle = -20;
    internal const long Transparent = 0x00000020L;
    internal const long ToolWindow = 0x00000080L;
    internal const long Layered = 0x00080000L;
    internal const long NoRedirectionBitmap = 0x00200000L;
    internal const long NoActivate = 0x08000000L;
    internal const long Required = Layered | Transparent | ToolWindow | NoActivate;

    internal static long Create(long currentStyles = 0) =>
        (currentStyles | Required) & ~NoRedirectionBitmap;

    internal static bool AreApplied(long styles) =>
        (styles & Required) == Required
        && (styles & NoRedirectionBitmap) == 0;
}
