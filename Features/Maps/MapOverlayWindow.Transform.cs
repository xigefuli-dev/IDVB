namespace IDVBuff.Features.Maps;

public sealed partial class MapOverlayWindow
{
    public void UpdateMapTransform(
        MapOverlayTransform transform,
        bool preservePlayer = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_map is null)
            return;
        var overlayWidth = transform.ReferenceWidth * transform.ScaleX;
        var overlayHeight = transform.ReferenceHeight * transform.ScaleY;
        if (!double.IsFinite(overlayWidth)
            || !double.IsFinite(overlayHeight)
            || !double.IsFinite(transform.OffsetX)
            || !double.IsFinite(transform.OffsetY)
            || overlayWidth <= 0
            || overlayHeight <= 0)
        {
            return;
        }

        _map = _map with
        {
            Left = ToFiniteSingle(transform.OffsetX - _gameBounds.X),
            Top = ToFiniteSingle(transform.OffsetY - _gameBounds.Y),
            Width = ToFiniteSingle(overlayWidth),
            Height = ToFiniteSingle(overlayHeight)
        };
        InvalidateLockedBackground();
        if (!preservePlayer)
            _player = null;
        if (IsVisible)
            Present();
    }

    public bool TryUpdateMapTransformOnly(
        RuntimeMapRecognition recognition,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle,
        MapScreenRect? viewportBounds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_map is null
            || recognition.Result.OverlayTransform is not { } transform
            || !File.Exists(recognition.FloorImagePath)
            || recognition.Map.Id != _mapId
            || !string.Equals(
                recognition.Result.Floor,
                _mapFloorKey,
                StringComparison.Ordinal)
            || !string.Equals(
                recognition.FloorImagePath,
                _mapImagePath,
                StringComparison.Ordinal)
            || recognition.Map.UpdatedAt != _mapUpdatedAt
            || transform.ReferenceWidth != _mapReferenceWidth
            || transform.ReferenceHeight != _mapReferenceHeight
            || gameBounds != _gameBounds
            || gameWindowHandle != _gameWindowHandle
            || !gameBounds.IsValid
            || gameWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        var expectedClip = viewportBounds is { IsValid: true } viewport
            ? new MapScreenRect(
                viewport.X - gameBounds.X,
                viewport.Y - gameBounds.Y,
                viewport.Width,
                viewport.Height)
            : new MapScreenRect(0d, 0d, gameBounds.Width, gameBounds.Height);

        var overlayWidth = transform.ReferenceWidth * transform.ScaleX;
        var overlayHeight = transform.ReferenceHeight * transform.ScaleY;
        if (!double.IsFinite(overlayWidth)
            || !double.IsFinite(overlayHeight)
            || !double.IsFinite(transform.OffsetX)
            || !double.IsFinite(transform.OffsetY)
            || overlayWidth <= 0
            || overlayHeight <= 0)
        {
            return false;
        }

        var newLeft = ToFiniteSingle(transform.OffsetX - gameBounds.X);
        var newTop = ToFiniteSingle(transform.OffsetY - gameBounds.Y);
        var newWidth = ToFiniteSingle(overlayWidth);
        var newHeight = ToFiniteSingle(overlayHeight);

        if (Math.Abs(_map.Left - newLeft) < 0.1f
            && Math.Abs(_map.Top - newTop) < 0.1f
            && Math.Abs(_map.Width - newWidth) < 0.1f
            && Math.Abs(_map.Height - newHeight) < 0.1f
            && _map.ClipBounds == expectedClip)
        {
            return true;
        }

        _map = _map with
        {
            Left = newLeft,
            Top = newTop,
            Width = newWidth,
            Height = newHeight,
            ClipBounds = expectedClip
        };
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
        return true;
    }
}
