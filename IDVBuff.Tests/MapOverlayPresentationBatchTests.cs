using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapOverlayPresentationBatchTests
{
    [Fact]
    public void Apply_BatchesStatusMapAndMiniMapChangesIntoOnePresentation()
    {
        var overlay = new RecordingOverlay();

        MapOverlayPresentationBatch.Apply(overlay, () =>
        {
            overlay.ClearStatus();
            overlay.ClearMap();
            overlay.RefreshPersistentMiniMap();
            overlay.Show();
        });

        Assert.Equal(1, overlay.ClearStatusCount);
        Assert.Equal(1, overlay.ClearMapCount);
        Assert.Equal(1, overlay.RefreshMiniMapCount);
        Assert.Equal(1, overlay.ShowCount);
        Assert.Equal(1, overlay.PresentCount);
    }

    [Fact]
    public void Apply_DoesNotPropagateFinalPresentationFailure()
    {
        var overlay = new RecordingOverlay { ThrowWhenPresenting = true };

        var exception = Record.Exception(() =>
            MapOverlayPresentationBatch.Apply(
                overlay,
                overlay.RefreshPersistentMiniMap));

        Assert.Null(exception);
        Assert.Equal(1, overlay.PresentCount);
    }

    private sealed class RecordingOverlay : IOverlayWindow
    {
        private int _deferDepth;
        private bool _presentPending;

        public int ClearStatusCount { get; private set; }
        public int ClearMapCount { get; private set; }
        public int RefreshMiniMapCount { get; private set; }
        public int ShowCount { get; private set; }
        public int PresentCount { get; private set; }
        public bool ThrowWhenPresenting { get; init; }

        public bool IsVisible => true;
        public bool HasMap => true;

        public IDisposable DeferPresent()
        {
            _deferDepth++;
            return new PresentLease(this);
        }

        public void ClearStatus()
        {
            ClearStatusCount++;
            RequestPresent();
        }

        public void ClearMap()
        {
            ClearMapCount++;
            RequestPresent();
        }

        public void RefreshPersistentMiniMap()
        {
            RefreshMiniMapCount++;
            RequestPresent();
        }

        public void Show()
        {
            ShowCount++;
            RequestPresent();
        }

        private void RequestPresent()
        {
            if (_deferDepth > 0)
                _presentPending = true;
            else
                PresentCount++;
        }

        private void EndPresentDeferral()
        {
            _deferDepth--;
            if (_deferDepth != 0 || !_presentPending)
                return;
            _presentPending = false;
            PresentCount++;
            if (ThrowWhenPresenting)
                throw new InvalidOperationException("presentation failed");
        }

        public void UpdateMap(object recognition, object gameBounds,
            IntPtr gameWindowHandle, bool showStatusPreference,
            object? viewportBounds = null, bool preservePlayer = false) { }
        public void UpdateStatus(object status, object gameBounds,
            IntPtr gameWindowHandle, bool showStatusPreference,
            bool showImmediately = true) { }
        public void UpdatePlayer(object? player) { }
        public void Hide() { }
        public void Toggle() { }
        public void Clear() { }
        public void ClearSession() { }
        public void LockBackground(object recognition, object viewportBounds,
            object gameBounds, IntPtr gameWindowHandle,
            bool showStatusPreference, bool preservePlayer = false) { }
        public void SetPersistentMiniMapState(string imagePath,
            object transform, object gameBounds, IntPtr gameWindowHandle,
            double miniMapScale, object? anchors = null,
            object? annotations = null, string? floorLabel = null) { }
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

        private sealed class PresentLease(RecordingOverlay owner) : IDisposable
        {
            private RecordingOverlay? _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.EndPresentDeferral();
            }
        }
    }
}
