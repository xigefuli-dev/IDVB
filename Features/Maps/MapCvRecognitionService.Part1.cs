using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;
/// <summary>Application-lifetime primary-floor gate detector and geometry recognizer.</summary>
public sealed partial class MapCvRecognitionService : IDisposable
{
    public bool RequiresSingleFeatureScan(string? mapClass) => _maps
        .Where(map => string.IsNullOrWhiteSpace(mapClass)
            || string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase))
        .Any(map => !MapScanFloorRules.IsPrimaryFloor(
            map,
            MapScanFloorRules.ResolveScanFloorKey(map)));

    public MapRecognitionAttempt AlignSelected(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapAlignmentSession? session,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null,
        MapReferencePoint? playerPrior = null,
        MapViewportOrigin? predictedViewportOrigin = null,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions = null,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory = null,
        AlignmentSearchContext? alignmentSearchContext = null,
        double nativeScaleChangeRatio = MapSessionRules.NativeScaleChangeRatio,
        string? mapClass = null) =>
        MapCvAlignmentService.AlignSelectedCore(
            this,
            frame,
            selectedMapId,
            session,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior,
            predictedViewportOrigin,
            liveIgnoreRegions,
            candidateHistory,
            alignmentSearchContext,
            nativeScaleChangeRatio,
            mapClass,
            SelectedAlignmentRoute.Default);

    private static MapGeometryFingerprint RebindFingerprint(
        MapGeometryFingerprint source,
        MapRecord map) => new()
    {
        Map = map,
        FloorKey = source.FloorKey,
        MainPoint = source.MainPoint,
        SidePoint = source.SidePoint,
        MainReferenceBounds = source.MainReferenceBounds,
        SideReferenceBounds = source.SideReferenceBounds,
        ReferenceWidth = source.ReferenceWidth,
        ReferenceHeight = source.ReferenceHeight,
        RecognitionImagePath = source.RecognitionImagePath,
        OverlayImagePath = source.OverlayImagePath,
        ReferenceGateIconWidth = source.ReferenceGateIconWidth,
        ReferenceGateIconHeight = source.ReferenceGateIconHeight
    };

    public void Dispose()
    {
        using var resourceLease = _catalogResourceGate.Enter();
        if (_disposed)
            return;
        _disposed = true;
        lock (_floorPrewarmGate)
        {
            _matchCts.Cancel();
            _matchCts.Dispose();
            _floorPrewarmTasks.Clear();
        }
        _gateDetector.Dispose();
        _deepScanGateDetector?.Dispose();
        _structureCache.Dispose();
        MapStructurePreprocessor.ClearReferenceCache();
        DisposeVpsg3();
        _auxiliaryTemplateCache.Dispose();
        // Keep the queue alive for refreshes already waiting; they recheck _disposed.
        foreach (var mat in _sideEntranceFeatureCache.Values)
            mat.Dispose();
        _sideEntranceFeatureCache = [];
    }

}
