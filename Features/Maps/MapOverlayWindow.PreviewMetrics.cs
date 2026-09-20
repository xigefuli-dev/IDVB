namespace IDVBuff.Features.Maps;

public sealed partial class MapOverlayWindow
{
    public double? CurrentMiniMapWidth => _persistentMiniMap?.Width;
    public double? CurrentMiniMapHeight => _persistentMiniMap?.Height;
}
