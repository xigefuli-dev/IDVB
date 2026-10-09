namespace IDVBuff.Features.Maps;

/// <summary>
/// Supplies the device input read by map dragging. When no source is supplied,
/// SessionOrchestrator uses its existing Win32 foreground, button and cursor reads.
/// </summary>
public interface IMapDragInputSource
{
    IntPtr GetForegroundWindow();
    bool IsLeftButtonDown();
    bool TryGetCursorPosition(out int x, out int y);
}
