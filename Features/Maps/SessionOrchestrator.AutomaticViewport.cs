using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private static CapturedGameFrame CropAutomaticMapContent(CapturedGameFrame captured)
    {
        // Zoom and fog can enlarge or disconnect the visible buildings.
        // Keep the map canvas; exclude client-anchored controls in feature
        // evidence rather than selecting a single bright component.
        var canvas = MapFrameUiExclusion.WithAutomaticCanvasContext(captured);
        try
        {
            // This is only a calculation envelope. Every proposed wall from
            // every disconnected building contributes; no component area or
            // maximum map-size rule chooses a winner. UI has already been
            // removed in evidence space, and empty/fog pixels prove nothing.
            using var points = canvas.GetOrCreateVpsg3Observation().ProposalEdges.FindNonZero();
            if (points.Empty()) return canvas;
            var walls = Cv2.BoundingRect(points);
            var padding = Math.Max(8, (int)Math.Round(canvas.ClientBounds.Width * .03));
            var crop = new Rect(walls.X - padding, walls.Y - padding,
                walls.Width + 2 * padding, walls.Height + 2 * padding)
                & new Rect(0, 0, canvas.Image.Width, canvas.Image.Height);
            if (crop == new Rect(0, 0, canvas.Image.Width, canvas.Image.Height)) return canvas;
            var result = new CapturedGameFrame(new Mat(canvas.Image, crop), canvas.ClientBounds,
                new MapScreenRect(canvas.ViewportBounds.X + crop.X,
                    canvas.ViewportBounds.Y + crop.Y, crop.Width, crop.Height), canvas.WindowHandle)
            {
                CaptureBackend = canvas.CaptureBackend,
                CaptureSystemRelativeTicks = canvas.CaptureSystemRelativeTicks,
                DetectedFloorKey = canvas.DetectedFloorKey,
                CaptureReadbackMilliseconds = canvas.CaptureReadbackMilliseconds,
                CaptureDroppedFrames = canvas.CaptureDroppedFrames,
                UiExclusionRegions = canvas.UiExclusionRegions
            };
            result.RetainDiagnosticSource(canvas);
            canvas.Dispose();
            return result;
        }
        catch { canvas.Dispose(); throw; }
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
                captured.ClientBounds, FloorIndicatorCaptureRegion.Above(viewport, group));
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
                    group, indicator, FloorIndicatorCaptureRegion.TemplateScale(group, captured.ClientBounds));
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
            extracted.RetainDiagnosticSource(captured);
            return extracted;
        }
        finally
        {
            captured.Dispose();
        }
    }
}
