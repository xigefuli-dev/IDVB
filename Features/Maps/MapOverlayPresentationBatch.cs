using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

internal static class MapOverlayPresentationBatch
{
    public static void Apply(IOverlayWindow overlay, Action update)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(update);

        var presentation = overlay.DeferPresent();
        try
        {
            update();
        }
        finally
        {
            try
            {
                presentation.Dispose();
            }
            catch
            {
                // Input-triggered state changes must not be blocked when the
                // native overlay is already shutting down.
            }
        }
    }
}
