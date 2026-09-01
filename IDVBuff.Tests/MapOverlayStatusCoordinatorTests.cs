using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapOverlayStatusCoordinatorTests
{
    [Fact]
    public void DefaultLifetimeIsThreeSeconds() =>
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            MapOverlayStatusCoordinator.DefaultTransientLifetime);

    [Fact]
    public async Task ReplacementRestartsTimerAndOldTimerCannotClearNewStatus()
    {
        var overlay = new RecordingOverlay();
        using var coordinator = new MapOverlayStatusCoordinator(
            overlay,
            action => action(),
            transientLifetime: TimeSpan.FromMilliseconds(100));
        var bounds = new MapScreenRect(0, 0, 1920, 1080);
        coordinator.Show(Status("first"), bounds, new IntPtr(1), true, true);
        await Task.Delay(60);
        coordinator.Show(Status("second"), bounds, new IntPtr(1), true, true);
        await Task.Delay(60);

        Assert.Equal("second", overlay.Status?.Title);
        await Task.Delay(70);
        Assert.Null(overlay.Status);
        Assert.Equal(1, overlay.ClearStatusCount);
    }

    [Fact]
    public async Task ClearCancelsPendingTransientExpiration()
    {
        var overlay = new RecordingOverlay();
        using var coordinator = new MapOverlayStatusCoordinator(
            overlay,
            action => action(),
            transientLifetime: TimeSpan.FromMilliseconds(30));
        var bounds = new MapScreenRect(0, 0, 1920, 1080);

        coordinator.Show(
            new MapOverlayStatus(MapOverlayStatusLevel.Success, "done", "", ""),
            bounds,
            new IntPtr(1),
            true,
            transient: true);
        coordinator.Clear();
        await Task.Delay(70);

        Assert.Null(overlay.Status);
        Assert.Equal(1, overlay.ClearStatusCount);
    }

    [Fact]
    public async Task KeptStatusRemainsUntilItIsOverwritten()
    {
        var overlay = new RecordingOverlay();
        using var coordinator = new MapOverlayStatusCoordinator(
            overlay,
            action => action(),
            transientLifetime: TimeSpan.FromMilliseconds(30));
        var bounds = new MapScreenRect(0, 0, 1920, 1080);

        coordinator.Show(Status("first"), bounds, new IntPtr(1), true, true);
        coordinator.KeepCurrent();
        await Task.Delay(70);
        coordinator.Show(Status("second"), bounds, new IntPtr(1), true, false);

        Assert.Equal("second", overlay.Status?.Title);
        Assert.Equal(0, overlay.ClearStatusCount);
    }

    [Fact]
    public void OverlayFailuresAreFailOpen()
    {
        var overlay = new RecordingOverlay
        {
            ThrowOnUpdateStatus = true,
            ThrowOnClearStatus = true
        };
        using var coordinator = new MapOverlayStatusCoordinator(
            overlay,
            action => action());
        var bounds = new MapScreenRect(0, 0, 1920, 1080);

        var showException = Record.Exception(() => coordinator.Show(
            Status("broken"),
            bounds,
            new IntPtr(1),
            true,
            transient: false));
        var clearException = Record.Exception(coordinator.Clear);

        Assert.Null(showException);
        Assert.Null(clearException);
    }

    [Fact]
    public void LaterStatusUpdateRetriesAfterOverlayFailure()
    {
        var overlay = new RecordingOverlay
        {
            RemainingUpdateFailures = 1
        };
        using var coordinator = new MapOverlayStatusCoordinator(
            overlay,
            action => action());
        var bounds = new MapScreenRect(0, 0, 1920, 1080);

        coordinator.Show(Status("first"), bounds, new IntPtr(1), true, false);
        coordinator.Show(Status("second"), bounds, new IntPtr(1), true, false);

        Assert.Equal("second", overlay.Status?.Title);
    }

    [Fact]
    public void SurveyCleanupReleasesGateBeforeFailingUiCallbacks()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var failures = new List<string>();

        SurveyCaptureCleanup.Complete(
            gate,
            () => throw new InvalidOperationException("overlay"),
            () => throw new InvalidOperationException("state"),
            (operation, _) => failures.Add(operation));

        Assert.True(gate.Wait(0));
        Assert.Contains("overlay-restore", failures);
        Assert.Contains("state-changed", failures);
    }

    [Fact]
    public void AlignmentTextUsesFinalEvidenceInsteadOfGateFailure()
    {
        var recognition = new RuntimeMapRecognition
        {
            Result = new MapRecognitionResult
            {
                Source = MapRecognitionSource.StructureMatching,
                EvidenceKind = MapAlignmentEvidenceKind.Structure
            }
        };

        Assert.Equal("无门结构对齐", MapAlignmentStatusText.Describe(recognition));
        recognition = new RuntimeMapRecognition
        {
            Result = new MapRecognitionResult
            {
                UsedCachedScale = true,
                EvidenceKind = MapAlignmentEvidenceKind.Structure
            }
        };
        Assert.Equal("缓存缩放＋位置对齐", MapAlignmentStatusText.Describe(recognition));
    }

    [Fact]
    public void AlignmentTextDistinguishesGateAndAuxiliaryRoutes()
    {
        Assert.Equal("双门对齐", Describe(
            MapRecognitionSource.Automatic,
            MapAlignmentEvidenceKind.DualGate));
        Assert.Equal("单门/侧门特征对齐", Describe(
            MapRecognitionSource.SideEntranceSelection,
            MapAlignmentEvidenceKind.None));
        Assert.Equal("楼层/辅助特征对齐", Describe(
            MapRecognitionSource.AuxiliaryAnchorTracking,
            MapAlignmentEvidenceKind.AuxiliaryConsensus));
    }

    [Fact]
    public void ScaleSearchPolicyMakesCachedFixedScaleExplicit()
    {
        var fixedRequest = new MapStructureRegistrationRequest
        {
            ScaleSearchPolicy = MapScaleSearchPolicy.Fixed
        };
        var searchRequest = new MapStructureRegistrationRequest
        {
            ScaleSearchPolicy = MapScaleSearchPolicy.Search
        };

        Assert.False(fixedRequest.AllowScaleSearch);
        Assert.True(searchRequest.AllowScaleSearch);
    }

    private static string Describe(
        MapRecognitionSource source,
        MapAlignmentEvidenceKind evidence) =>
        MapAlignmentStatusText.Describe(new RuntimeMapRecognition
        {
            Result = new MapRecognitionResult
            {
                Source = source,
                EvidenceKind = evidence
            }
        });

    private static MapOverlayStatus Status(string title) =>
        new(MapOverlayStatusLevel.Failure, title, "message");

    private sealed class RecordingOverlay : IOverlayWindow
    {
        public bool ThrowOnUpdateStatus { get; init; }
        public bool ThrowOnClearStatus { get; init; }
        public int RemainingUpdateFailures { get; set; }
        public bool IsVisible => Status is not null;
        public bool HasMap => false;
        public MapOverlayStatus? Status { get; private set; }
        public int ClearStatusCount { get; private set; }
        public void UpdateStatus(object status, object gameBounds, IntPtr handle,
            bool showStatusPreference, bool showImmediately = true)
        {
            if (ThrowOnUpdateStatus || RemainingUpdateFailures-- > 0)
                throw new InvalidOperationException("update failed");
            Status = (MapOverlayStatus)status;
        }
        public void ClearStatus()
        {
            if (ThrowOnClearStatus)
                throw new InvalidOperationException("clear failed");
            Status = null;
            ClearStatusCount++;
        }
        public void UpdateMap(object recognition, object bounds, IntPtr handle,
            bool show, object? viewport = null, bool preservePlayer = false) { }
        public void UpdatePlayer(object? player) { }
        public void Show() { }
        public void Hide() { }
        public void Toggle() { }
        public void Clear() => Status = null;
        public void ClearMap() { }
        public void ClearSession() { }
        public void LockBackground(object recognition, object viewport, object bounds,
            IntPtr handle, bool show, bool preservePlayer = false) { }
        public void SetPersistentMiniMapState(string path, object transform, object bounds,
            IntPtr handle, double scale, object? anchors = null,
            object? annotations = null, string? floorLabel = null) { }
        public void ClearPersistentMiniMap() { }
        public void SetStatusVisible(bool value) { }
        public void SetReverseAlternateDisplay(bool value) { }
        public void SetAllowExtend(bool value) { }
        public void SetMapOpacity(double value) { }
        public void SetShowGateMarkers(bool value) { }
        public void SetShowAuxiliaryAnchors(bool value) { }
        public void SetShowTextAnnotations(bool value) { }
        public void SetShowBoxAnnotations(bool value) { }
        public void SetShowLineAnnotations(bool value) { }
        public void SetShowGateMarkersOnMiniMap(bool value) { }
        public void SetShowAuxiliaryAnchorsOnMiniMap(bool value) { }
        public void SetShowTextAnnotationsOnMiniMap(bool value) { }
        public void SetShowBoxAnnotationsOnMiniMap(bool value) { }
        public void SetShowLineAnnotationsOnMiniMap(bool value) { }
        public void SetShowFloorOnMiniMap(bool value) { }
        public void SetStatusOpacity(double value) { }
        public void SetStatusOffsetX(double value) { }
        public void SetStatusOffsetY(double value) { }
        public void SetMiniMapOpacity(double value) { }
        public void SetMiniMapOffsetX(double value) { }
        public void SetMiniMapOffsetY(double value) { }
        public void SetMiniMapScale(double value) { }
        public void Dispose() { }
    }
}
