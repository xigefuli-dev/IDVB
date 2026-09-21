using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SideEntranceScanPipeline
{
    private IReadOnlyList<SideEntranceScanCandidate> RunSingleGateScan(
        Mat capturedFrame,
        IReadOnlyList<(MapRecord map, string floorKey, Mat featureTemplate)> candidates,
        int topK, GateDetection? detectedGate, MapScreenRect? viewportBounds,
        bool maskDetectedGate, int? gateIndexForDiagnostics, Action<double>? progress = null)
        => detectedGate is null ? [] : RunScan(capturedFrame, candidates,
            new[] { detectedGate }, topK, viewportBounds, progress);
}
