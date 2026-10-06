using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapFrameUiExclusion
{
    // Call before deriving any features. The capture pixels stay unchanged;
    // a new frame owns the context computed for this capture's current client.
    internal static CapturedGameFrame WithAutomaticCanvasContext(CapturedGameFrame captured)
    {
        var result = new CapturedGameFrame(new Mat(captured.Image,
            new Rect(0, 0, captured.Image.Width, captured.Image.Height)),
            captured.ClientBounds, captured.ViewportBounds, captured.WindowHandle)
        {
            CaptureBackend = captured.CaptureBackend,
            CaptureSystemRelativeTicks = captured.CaptureSystemRelativeTicks,
            DetectedFloorKey = captured.DetectedFloorKey,
            CaptureReadbackMilliseconds = captured.CaptureReadbackMilliseconds,
            CaptureDroppedFrames = captured.CaptureDroppedFrames,
            UiExclusionRegions = AutomaticCanvas(captured.ClientBounds)
        };
        captured.Dispose();
        return result;
    }

    // Standard full-map UI: anchored to the client, never to the changing
    // visible building. Width supplies the UI scale; bottom controls retain
    // their bottom anchor when the client aspect ratio changes.
    internal static IReadOnlyList<MapScreenRect> AutomaticCanvas(MapScreenRect client)
    {
        var w = client.Width;
        var bottom = client.Y + client.Height;
        return Array.AsReadOnly(new[]
        {
            new MapScreenRect(client.X, client.Y, w * .18, client.Height),
            new MapScreenRect(client.X + w * .185, bottom - w * .21, w * .125, w * .18),
            new MapScreenRect(client.X + w * .18, client.Y, w * .24, w * .075),
            new MapScreenRect(client.X + w * .75, client.Y, w * .14, w * .07),
            new MapScreenRect(client.X + w * .91, client.Y, w * .09, client.Height),
            new MapScreenRect(client.X + w * .52, bottom - w * .065, w * .16, w * .065),
            new MapScreenRect(client.X + w * .82, bottom - w * .09, w * .18, w * .09)
        });
    }

    internal static Rect Local(MapScreenRect region, MapScreenRect viewport, Size size)
    {
        var sx = size.Width / viewport.Width;
        var sy = size.Height / viewport.Height;
        var left = (int)Math.Floor((region.X - viewport.X) * sx);
        var top = (int)Math.Floor((region.Y - viewport.Y) * sy);
        var right = (int)Math.Ceiling((region.X + region.Width - viewport.X) * sx);
        var bottom = (int)Math.Ceiling((region.Y + region.Height - viewport.Y) * sy);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top))
            & new Rect(0, 0, size.Width, size.Height);
    }

    internal static void Fill(Mat target, MapScreenRect viewport,
        IReadOnlyList<MapScreenRect> regions, Scalar value, int padding = 0)
    {
        foreach (var region in regions)
        {
            var local = Local(region, viewport, target.Size());
            if (local.Width > 0 && local.Height > 0)
            {
                local = new Rect(local.X - padding, local.Y - padding,
                    local.Width + padding * 2, local.Height + padding * 2)
                    & new Rect(0, 0, target.Width, target.Height);
                Cv2.Rectangle(target, local, value, -1);
            }
        }
    }

    // Gate response coordinates describe the template's top-left. Exclude
    // every placement touching UI before peak selection or early exit.
    internal static void ExcludeGateScores(Mat scores, Size template,
        MapScreenRect viewport, double physicalPixelsPerImagePixel,
        IReadOnlyList<MapScreenRect>? regions)
    {
        if (regions is null) return;
        var p = physicalPixelsPerImagePixel;
        foreach (var region in regions)
        {
            var left = (int)Math.Floor((region.X - viewport.X) / p) - template.Width + 1;
            var top = (int)Math.Floor((region.Y - viewport.Y) / p) - template.Height + 1;
            var right = (int)Math.Ceiling((region.X + region.Width - viewport.X) / p);
            var bottom = (int)Math.Ceiling((region.Y + region.Height - viewport.Y) / p);
            var rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top))
                & new Rect(0, 0, scores.Width, scores.Height);
            if (rect.Width > 0 && rect.Height > 0)
                Cv2.Rectangle(scores, rect, Scalar.All(-1), -1);
        }
    }
}
