namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private int _realtimeTransformReferenceWidth;
    private int _realtimeTransformReferenceHeight;
    private int _realtimeTransformOrientationDegrees;
    private readonly LatestRealtimeTransformBuffer _hiddenRealtimeTransform = new();
    private long _lastRealtimePositionSample;

    private void PublishRealtimeMapTransform(
        OrbTrackingContext context,
        double scale,
        double tx,
        double ty,
        long timestamp,
        double confidence,
        RealtimeTransformSource source = RealtimeTransformSource.VisualFrame)
    {
        var state = new RealtimeTransformState(
            scale,
            tx,
            ty,
            timestamp,
            confidence,
            context.Generation,
            source);
        if (_alignmentResultHidden)
        {
            _hiddenRealtimeTransform.Hold(state);
            if (_alignmentResultHidden)
                return;
            _hiddenRealtimeTransform.DiscardIfLatest(state);
        }
        _realtimeMapTransformPublisher.Publish(state);
    }

    private bool ApplyRealtimeMapTransform(RealtimeTransformState state)
    {
        if (_disposed
            || state.Generation != Volatile.Read(ref _orbTrackingGeneration)
            || !_gameMapToggleState.IsOpen
            || _realtimeTransformReferenceWidth <= 0
            || _realtimeTransformReferenceHeight <= 0)
        {
            return false;
        }
        if (_alignmentResultHidden)
        {
            _hiddenRealtimeTransform.Hold(state);
            if (_alignmentResultHidden)
                return false;
            _hiddenRealtimeTransform.DiscardIfLatest(state);
        }

        var transform = MapCanonicalTransformMath.BuildOverlayTransform(
            state.Scale,
            state.Scale,
            state.Tx,
            state.Ty,
            _realtimeTransformReferenceWidth,
            _realtimeTransformReferenceHeight,
            residualPixels: 0d,
            orientationDegrees: _realtimeTransformOrientationDegrees,
            alignmentMode: MapOverlayAlignmentMode.Uniform);
        _overlay.UpdateMapTransform(transform, preservePlayer: true);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastRealtimePositionSample, now).TotalMilliseconds >= 200)
        {
            _lastRealtimePositionSample = now;
            _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                "实时贴图位置消费采样",
                details: new()
                {
                    ["generation"] = state.Generation, ["source"] = state.Source.ToString(),
                    ["tx"] = state.Tx, ["ty"] = state.Ty, ["scale"] = state.Scale,
                    ["sourceTicks"] = state.Timestamp, ["overlayVisible"] = _overlay.IsVisible,
                    ["endpoint"] = "overlay-callback-return-not-screen-presentation"
                });
        }
        return true;
    }

    private void ApplyLatestHiddenRealtimeTransform()
    {
        if (_hiddenRealtimeTransform.TryTake(out var latest))
            ApplyRealtimeMapTransform(latest);
    }

    private void ClearHiddenRealtimeTransform() =>
        _hiddenRealtimeTransform.Clear();
}
