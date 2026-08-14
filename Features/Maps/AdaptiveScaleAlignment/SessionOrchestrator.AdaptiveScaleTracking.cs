using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task RunAdaptiveStructureTrackingLoopAsync(
        OrbTrackingContext context,
        RuntimeMapRecognition recognition,
        OrbTrackingConfig config,
        CancellationToken cancellationToken)
    {
        var current = recognition;
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && IsOrbTrackingContextCurrent(context))
            {
                var interval = GetAdaptiveStructureProbeInterval(
                    context,
                    Math.Max(250, config.StructureCorrectionIntervalMs));
                await Task.Delay(interval, cancellationToken);
                if (!IsOrbTrackingContextCurrent(context))
                    break;
                if (!_captureSvc.TryCaptureViewport(
                        ResolveMapViewportForCurrentWindow(),
                        out var frameObject,
                        out _)
                    || frameObject is not CapturedGameFrame frame)
                {
                    continue;
                }

                using (frame)
                {
                    var corrected = TryCorrectOrbTrackingWithStructure(
                        context,
                        frame,
                        current,
                        config.MaximumBaselineScaleChangeRatio,
                        context.BaselineScale);
                    if (corrected is null)
                    {
                        NotifyAdaptiveStructureFailure(context);
                        continue;
                    }
                    var decision = EvaluateAdaptiveStructure(
                        context,
                        frame,
                        corrected,
                        Interlocked.Increment(ref _adaptiveFrameId));
                    current = decision.Recognition;
                    if (current.Result.OverlayTransform is not null)
                    {
                        EnqueueOrbTrackingCommit(
                            context,
                            current,
                            config.MaximumBaselineScaleChangeRatio);
                        if (decision.BecameReliable)
                            PublishAdaptiveReliableStatus(context, frame, current);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                "Adaptive structure tracking stopped after an unexpected failure.",
                details: new()
                {
                    ["generation"] = context.Generation,
                    ["exception"] = exception.ToString()
                });
        }
        finally
        {
            lock (_orbTrackingGate)
            {
                if (_orbTrackingGeneration == context.Generation)
                {
                    _orbTrackingCancellation?.Dispose();
                    _orbTrackingCancellation = null;
                    _orbTrackingTask = null;
                }
            }
        }
    }
}
