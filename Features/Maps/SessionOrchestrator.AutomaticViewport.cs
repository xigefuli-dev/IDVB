using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private static CapturedGameFrame CropAutomaticMapContent(CapturedGameFrame captured)
    {
        // Select visible map content, not isolated legends or scene fragments.
        // The old assistant uses a central bright connected component for the
        // same purpose. This creates no identity or pose evidence by itself.
        var searchBounds = DwrGameWindowCaptureService.GetViewportBounds(
            captured.ClientBounds, new NormalizedRectangle
                { X = 0.16, Y = 0.22, Width = 0.72, Height = 0.58 });
        var search = new Rect(
            (int)Math.Round(searchBounds.X - captured.ViewportBounds.X),
            (int)Math.Round(searchBounds.Y - captured.ViewportBounds.Y),
            (int)Math.Round(searchBounds.Width), (int)Math.Round(searchBounds.Height));
        search &= new Rect(0, 0, captured.Image.Width, captured.Image.Height);
        if (search.Width <= 0 || search.Height <= 0)
            return captured;
        using var source = new Mat(captured.Image, search);
        using var gray = new Mat();
        if (source.Channels() == 1) source.CopyTo(gray);
        else Cv2.CvtColor(source, gray, source.Channels() == 4
            ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using var mask = new Mat();
        Cv2.Threshold(gray, mask, 74, 255, ThresholdTypes.Binary);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        var largestArea = 999;
        Rect? content = null;
        for (var label = 1; label < count; label++)
        {
            var area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            var width = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
            var height = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);
            if (area <= largestArea || width > captured.ClientBounds.Width * 0.58
                || height > captured.ClientBounds.Height * 0.62)
                continue;
            largestArea = area;
            content = new Rect(search.X + stats.At<int>(label, (int)ConnectedComponentsTypes.Left),
                search.Y + stats.At<int>(label, (int)ConnectedComponentsTypes.Top), width, height);
        }
        if (content is not { } visible)
            return captured;
        var padding = Math.Max(8, (int)Math.Round(captured.ClientBounds.Width * 0.03));
        var crop = new Rect(visible.X - padding, visible.Y - padding,
            visible.Width + 2 * padding, visible.Height + 2 * padding)
            & new Rect(0, 0, captured.Image.Width, captured.Image.Height);
        var bounds = new MapScreenRect(captured.ViewportBounds.X + crop.X,
            captured.ViewportBounds.Y + crop.Y, crop.Width, crop.Height);
        var result = new CapturedGameFrame(new Mat(captured.Image, crop),
            captured.ClientBounds, bounds, captured.WindowHandle)
        {
            CaptureBackend = captured.CaptureBackend,
            CaptureSystemRelativeTicks = captured.CaptureSystemRelativeTicks,
            DetectedFloorKey = captured.DetectedFloorKey
        };
        captured.Dispose();
        return result;
    }

    // Standard map UI search area, relative to the physical game client.
    // Like the old assistant, capture a broad area rather than a player-drawn
    // box. Native color/structure extraction determines the observed walls.
    private static NormalizedRectangle AutomaticMapViewport() => new()
    {
        X = 0.10, Y = 0.12, Width = 0.78, Height = 0.82
    };

    private async Task<FloorIndicatorTemplateRegistry.Group?>
        ResolveAutomaticIdentityFloorGroupAsync(string? mapClass)
    {
        var maps = (await _mapRepository.GetMapsAsync())
            .Where(map => string.IsNullOrWhiteSpace(mapClass)
                || string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (maps.Length == 0)
            return null;

        FloorIndicatorTemplateRegistry.Group? sharedGroup = null;
        foreach (var map in maps)
        {
            var group = FloorIndicatorTemplateRegistry.Resolve(
                MapFloorRules.GetOrderedFloors(map).Select(floor => floor.Key));
            // A class with mixed indicator layouts cannot use one map's
            // layout to exclude another map. Retain the complete floor pool.
            if (group is null || (sharedGroup is not null && sharedGroup.Key != group.Key))
                return null;
            sharedGroup = group;
        }
        return sharedGroup;
    }

    private static CapturedGameFrame ExtractAutomaticIdentityViewport(
        CapturedGameFrame captured,
        NormalizedRectangle viewport,
        FloorIndicatorTemplateRegistry.Group? group)
    {
        try
        {
            var bounds = DwrGameWindowCaptureService.GetViewportBounds(
                captured.ClientBounds, viewport);
            var header = DwrGameWindowCaptureService.GetViewportBounds(
                captured.ClientBounds, FloorIndicatorCaptureRegion.Above(viewport));
            Rect Local(MapScreenRect value) => new(
                (int)Math.Round(value.X - captured.ViewportBounds.X),
                (int)Math.Round(value.Y - captured.ViewportBounds.Y),
                (int)Math.Round(value.Width), (int)Math.Round(value.Height));
            var extent = new Rect(0, 0, captured.Image.Width, captured.Image.Height);
            var mapRect = Local(bounds);
            var headerRect = Local(header);
            if ((mapRect & extent) != mapRect
                || (group is not null && (headerRect & extent) != headerRect))
                throw new InvalidDataException("Automatic map/header region exceeds captured frame.");

            string? detectedFloor = null;
            if (group is not null)
            {
                using var indicator = new Mat(captured.Image, headerRect);
                var result = FloorIndicatorTemplateRegistry.RecognizeDetailed(
                    group, indicator, captured.ClientBounds.Width / group.ReferenceClientWidth);
                detectedFloor = result.Succeeded ? result.DetectedFloor : null;
                MapLogCollector.Instance.Append(MapLogCategory.FloorRecognition, MapLogLevel.Info,
                    "自动身份识别读取同帧楼层指示器", details: new()
                    {
                        ["group"] = group.Key,
                        ["succeeded"] = result.Succeeded,
                        ["floor"] = result.DetectedFloor,
                        ["score"] = result.BestScore,
                        ["margin"] = result.Margin,
                        ["failureReason"] = result.FailureDescription,
                        ["captureTicks"] = captured.CaptureSystemRelativeTicks
                    });
            }
            var extracted = new CapturedGameFrame(new Mat(captured.Image, mapRect),
                captured.ClientBounds, bounds, captured.WindowHandle)
            {
                CaptureBackend = captured.CaptureBackend,
                CaptureSystemRelativeTicks = captured.CaptureSystemRelativeTicks,
                DetectedFloorKey = detectedFloor
            };
            return extracted;
        }
        finally
        {
            captured.Dispose();
        }
    }
}
