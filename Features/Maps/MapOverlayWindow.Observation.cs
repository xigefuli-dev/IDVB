namespace IDVBuff.Features.Maps;

public sealed partial class MapOverlayWindow
{
    private MapScreenRect? _observationRegion;
    private bool HasContent => HasMap || HasStatus || _persistentMiniMap is not null || _observationRegion is not null;

    public void SetObservationRegion(MapScreenRect? viewportBounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _observationRegion = viewportBounds is { IsValid: true } ? viewportBounds : null;
        RefreshVisibleContent();
    }

    private MapScreenRect? GetObservationRegion() => _showMainContent && _observationRegion is { } region
        ? new MapScreenRect(region.X - _gameBounds.X, region.Y - _gameBounds.Y, region.Width, region.Height)
        : null;
}
