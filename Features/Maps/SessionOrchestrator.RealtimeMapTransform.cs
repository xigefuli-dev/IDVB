namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private int _realtimeTransformReferenceWidth;
    private int _realtimeTransformReferenceHeight;
    private int _realtimeTransformOrientationDegrees;

    private void PublishRealtimeMapTransform(
        OrbTrackingContext context,
        double scale,
        double tx,
        double ty,
        long timestamp,
        double confidence)
    {
        _realtimeMapTransformPublisher.Publish(new RealtimeTransformState(
            scale,
            tx,
            ty,
            timestamp,
            confidence,
            context.Generation));
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
        return true;
    }
}
