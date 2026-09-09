using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private Task<CapturedGameFrame?> CaptureBackgroundAlignmentFrameAsync(
        MapRecord map, CancellationToken cancellationToken, Func<bool> shouldContinue)
    {
        var group = FloorIndicatorTemplateRegistry.Resolve(
            MapFloorRules.GetOrderedFloors(map).Select(f => f.Key));
        return CaptureStableViewportAsync("后台扫描消费对齐", cancellationToken,
            relaxForLockedMap: group is not null, shouldContinue: shouldContinue,
            autoFloor: group is null ? null : new AutoFloorCapture(map, group));
    }

    private sealed class AutoFloorCapture(MapRecord map, FloorIndicatorTemplateRegistry.Group group)
    {
        public MapRecord Map { get; } = map;
        public FloorIndicatorTemplateRegistry.Group Group { get; } = group;
        public string? FloorKey { get; private set; }
        public double Score { get; private set; }
        public double Margin { get; private set; }
        public double TotalMilliseconds { get; private set; }

        public NormalizedRectangle Expand(NormalizedRectangle viewport) =>
            FloorIndicatorCaptureRegion.IncludeMap(viewport);

        public CapturedGameFrame Extract(CapturedGameFrame captured, NormalizedRectangle viewport)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var viewportBounds = DwrGameWindowCaptureService.GetViewportBounds(captured.ClientBounds, viewport);
                Rect Local(MapScreenRect bounds) => new(
                    (int)Math.Round(bounds.X - captured.ViewportBounds.X),
                    (int)Math.Round(bounds.Y - captured.ViewportBounds.Y),
                    (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
                var mapRect = Local(viewportBounds);
                var extent = new Rect(0, 0, captured.Image.Width, captured.Image.Height);
                if ((mapRect & extent) != mapRect)
                    throw new InvalidDataException("Map viewport is outside the capture.");
                FloorKey = null;
                Score = Margin = 0;
                try
                {
                    var region = FloorIndicatorCaptureRegion.Above(viewport);
                    if (region.Height > 0)
                    {
                        var bounds = DwrGameWindowCaptureService.GetViewportBounds(captured.ClientBounds, region);
                        var rect = Local(bounds);
                        if ((rect & extent) == rect)
                        {
                            using var indicator = new Mat(captured.Image, rect);
                            FloorKey = FloorIndicatorTemplateRegistry.Recognize(Group, indicator,
                                out var score, out var margin, captured.ClientBounds.Width / Group.ReferenceClientWidth);
                            Score = score;
                            Margin = margin;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Optional classification must never discard a valid map viewport.
                    System.Diagnostics.Debug.WriteLine(exception);
                }
                // Mat ROI retains the backing buffer; no second map-sized copy.
                return new CapturedGameFrame(new Mat(captured.Image, mapRect), captured.ClientBounds,
                    viewportBounds, captured.WindowHandle)
                {
                    CaptureBackend = captured.CaptureBackend,
                    CaptureSystemRelativeTicks = captured.CaptureSystemRelativeTicks,
                    DetectedFloorKey = FloorKey
                };
            }
            finally { captured.Dispose(); TotalMilliseconds += timer.Elapsed.TotalMilliseconds; }
        }
    }
}
