using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapObservationPresentationTests
{
    [Fact]
    public void ChangedPixelsAndRepeatedIncompleteComparisonsKeepDisplayedMapUntilReplacement()
    {
        var display = new MapObservationPresentation();
        var overlay = new RecordingOverlay();
        using var frame = Frame();
        using var cache = new ScanObservationFrameCache();
        var first = Recognition("1f");
        display.Publish(overlay, first, frame, true);
        cache.Remember(frame, "catalog-1", ScanPerformanceMode.Balanced);

        for (var i = 1; i <= 20; i++)
        {
            frame.Image.Set(5, 5, new Vec3b(0, 0, (byte)i));
            Assert.False(cache.Matches(frame, "catalog-1", ScanPerformanceMode.Balanced));
            using (MapObservationPresentation.SuspendForCapture(overlay, () => true))
            {
                Assert.False(overlay.IsVisible);
                Assert.Same(first, overlay.Map);
            }
            // Capture ended: the map must already be visible during the next
            // stability wait and during recognition, before any result exists.
            Assert.True(overlay.IsVisible);
            display.Publish(overlay, null, frame, true);
            Assert.Same(first, overlay.Map);
            Assert.Equal(frame.ViewportBounds, overlay.Region);
        }

        var second = Recognition("1f");
        display.Publish(overlay, second, frame, true);
        Assert.Same(second, overlay.Map);
        Assert.Equal(0, overlay.ClearMapCount);
        Assert.Equal(new[] { first, second }, overlay.PresentedMaps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureFailureOrPassTimeoutRestoresExistingDisplay(bool timeout)
    {
        var display = new MapObservationPresentation();
        var overlay = new RecordingOverlay();
        using var frame = Frame();
        var preview = Recognition("1f");
        display.Publish(overlay, preview, frame, true);

        Assert.ThrowsAny<Exception>((Action)(() =>
        {
            using var capture = MapObservationPresentation.SuspendForCapture(overlay, () => true);
            Assert.False(overlay.IsVisible);
            if (timeout) throw new OperationCanceledException("pass deadline");
            throw new InvalidOperationException("capture failed");
        }));

        Assert.True(overlay.IsVisible);
        Assert.Same(preview, overlay.Map);
        Assert.Equal(frame.ViewportBounds, overlay.Region);
        Assert.Equal(0, overlay.ClearMapCount);
    }

    [Fact]
    public void CloseOrSupersededGenerationCannotResurrectDisplayAfterCapture()
    {
        var display = new MapObservationPresentation();
        var overlay = new RecordingOverlay();
        using var frame = Frame();
        display.Publish(overlay, Recognition("1f"), frame, true);
        var current = true;
        using (MapObservationPresentation.SuspendForCapture(overlay, () => current))
        {
            current = false;
            display.Reset();
            overlay.ClearMap();
            overlay.SetObservationRegion(null);
        }

        Assert.False(overlay.IsVisible);
        Assert.Null(overlay.Map);
        display.Publish(overlay, null, frame, true);
        Assert.Null(overlay.Map);
        Assert.Null(overlay.Region);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("client")]
    [InlineData("viewport")]
    [InlineData("floor")]
    public void ChangedTargetRevokesOldScreenCoordinates(string changed)
    {
        var display = new MapObservationPresentation();
        var overlay = new RecordingOverlay();
        using var frame = Frame();
        display.Publish(overlay, Recognition("1f"), frame, true);

        Assert.False(display.RetainForTarget(overlay,
            changed == "client" ? frame.ClientBounds with { X = 99 } : frame.ClientBounds,
            changed == "viewport" ? frame.ViewportBounds with { Width = 99 } : frame.ViewportBounds,
            changed == "window" ? new IntPtr(2) : frame.WindowHandle,
            changed == "floor" ? "2f" : null));
        Assert.Null(overlay.Map);
        Assert.Null(overlay.Region);
        Assert.Equal(1, overlay.ClearMapCount);

        display.Publish(overlay, null, frame, true);
        Assert.Null(overlay.Map);
    }

    [Fact]
    public void AlreadyHiddenOverlayAndDisposedCaptureLeaseDoNotCauseExtraShows()
    {
        var overlay = new RecordingOverlay();
        overlay.Hide();
        using (MapObservationPresentation.SuspendForCapture(overlay, () => true)) { }
        Assert.False(overlay.IsVisible);
        Assert.Equal(0, overlay.ShowCount);

        overlay.Show();
        var capture = MapObservationPresentation.SuspendForCapture(overlay, () => true);
        capture.Dispose();
        capture.Dispose();
        Assert.Equal(2, overlay.ShowCount);
    }

    [Fact]
    public void CaptureExcludedOverlayStaysVisibleWithoutRepainting()
    {
        var overlay = new RecordingOverlay { IsCaptureExclusionEnabled = true };
        using (MapObservationPresentation.SuspendForCapture(overlay, () => true))
            Assert.True(overlay.IsVisible);
        Assert.Equal(0, overlay.ShowCount);
    }

    private static CapturedGameFrame Frame() => new(
        new Mat(20, 20, MatType.CV_8UC3, Scalar.Black),
        new MapScreenRect(100, 100, 800, 600), new MapScreenRect(200, 200, 400, 300), new IntPtr(1));

    private static RuntimeMapRecognition Recognition(string floor) => new()
    {
        Map = new MapRecord { Id = Guid.NewGuid() }, FloorImagePath = "preview.png",
        Result = new MapRecognitionResult { Floor = floor, OverlayTransform = new() { ScaleX = 1, ScaleY = 1 } }
    };

    private sealed class RecordingOverlay : IOverlayWindow
    {
        public object? Map { get; private set; }
        public object? Region { get; private set; }
        public int ClearMapCount { get; private set; }
        public int ShowCount { get; private set; }
        public List<object?> PresentedMaps { get; } = [];
        public bool IsVisible { get; private set; } = true;
        public bool HasMap => Map is not null;
        public bool IsCaptureExclusionEnabled { get; init; }
        public void UpdateMap(object recognition, object gameBounds, IntPtr gameWindowHandle,
            bool showStatusPreference, object? viewportBounds = null, bool preservePlayer = false)
        {
            Map = recognition;
            PresentedMaps.Add(Map);
        }
        public void ClearMap() { Map = null; ClearMapCount++; PresentedMaps.Add(null); }
        public void Show() { IsVisible = true; ShowCount++; }
        public void Hide() => IsVisible = false;
        public void SetObservationRegion(object? viewportBounds) => Region = viewportBounds;
        public void UpdateStatus(object status, object gameBounds, IntPtr gameWindowHandle,
            bool showStatusPreference, bool showImmediately = true) { }
        public void ClearStatus() { }
        public void UpdatePlayer(object? player) { }
        public void Toggle() => IsVisible = !IsVisible;
        public void Clear() => ClearMap();
        public void ClearSession() { }
        public void LockBackground(object recognition, object viewportBounds, object gameBounds,
            IntPtr gameWindowHandle, bool showStatusPreference, bool preservePlayer = false) { }
        public void SetPersistentMiniMapState(string imagePath, object transform, object gameBounds,
            IntPtr gameWindowHandle, double miniMapScale, object? anchors = null,
            object? annotations = null, string? floorLabel = null,
            bool supportsVectorRoutes = false) { }
        public void ClearPersistentMiniMap() { }
        public void SetStatusVisible(bool visible) { }
        public void SetReverseAlternateDisplay(bool enabled) { }
        public void SetAllowExtend(bool allow) { }
        public void SetMapOpacity(double opacity) { }
        public void SetShowGateMarkers(bool show) { }
        public void SetShowAuxiliaryAnchors(bool show) { }
        public void SetShowTextAnnotations(bool show) { }
        public void SetShowBoxAnnotations(bool show) { }
        public void SetShowLineAnnotations(bool show) { }
        public void SetShowGateMarkersOnMiniMap(bool show) { }
        public void SetShowAuxiliaryAnchorsOnMiniMap(bool show) { }
        public void SetShowTextAnnotationsOnMiniMap(bool show) { }
        public void SetShowBoxAnnotationsOnMiniMap(bool show) { }
        public void SetShowLineAnnotationsOnMiniMap(bool show) { }
        public void SetShowFloorOnMiniMap(bool show) { }
        public void SetStatusOpacity(double opacity) { }
        public void SetStatusOffsetX(double offsetX) { }
        public void SetStatusOffsetY(double offsetY) { }
        public void SetMiniMapOpacity(double opacity) { }
        public void SetMiniMapOffsetX(double offsetX) { }
        public void SetMiniMapOffsetY(double offsetY) { }
        public void SetMiniMapScale(double scale) { }
        public void Dispose() { }
    }
}
