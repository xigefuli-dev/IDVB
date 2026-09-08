using System.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private sealed record MapOpenViewportCaptureResult(
        CapturedGameFrame? Frame,
        double StableViewportWaitMilliseconds,
        string StableViewportMode,
        bool StableViewportFallback,
        MapRecognitionAttempt? PrecomputedVpsg3Attempt);

    private async Task<MapOpenViewportCaptureResult> CaptureMapOpenViewportAsync(
        MapGameToggleTransition toggle,
        RuntimeMapRecognition locked,
        string floorKey,
        MapStructureRegistrationTuning initialPrewarmTuning,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var channel = MapAlignmentChannelRegistry.Resolve(locked.Map, floorKey);
        var stableViewport = ActiveOperationTrace?.StartTopLevel(
            "stable_viewport", MapOperationWaitKind.Capture,
            mapId: locked.Map.Id.ToString("D"), floorKey: floorKey);
        CapturedGameFrame? frame;
        try
        {
            frame = await CaptureStableViewportAsync(
                "仅对齐", cancellationToken, relaxForLockedMap: true,
                shouldContinue: () => _gameMapToggleState.IsCurrent(toggle),
                lowStructureReadiness: channel.Channel == MapAlignmentChannel.LowStructure,
                lowStructureReadinessFrameCount: initialPrewarmTuning.LowStructureReadinessFrameCount,
                prepareNativeStructure: initialPrewarmTuning.UsePrebuiltStructureLine,
                prepareVpsg3Structure: _recognition.IsVpsg3Ready(locked.Map, floorKey));
        }
        finally
        {
            stableViewport?.Complete();
            timer.Stop();
        }
        var nativeFrame = frame?.CaptureBackend == "wgc-frame-arrived";
        return new MapOpenViewportCaptureResult(
            frame, timer.Elapsed.TotalMilliseconds,
            nativeFrame ? "frame-arrived-readiness" : "readiness-gdi",
            !nativeFrame, null);
    }
}
