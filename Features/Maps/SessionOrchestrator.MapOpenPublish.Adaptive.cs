using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task<AdaptiveAlignmentDecision?> EvaluateMapOpenAdaptiveAsync(
        RuntimeMapRecognition aligned, CapturedGameFrame frame, MapScanDiagnostics? diagnostics,
        MapGameToggleTransition toggle, MapMatchSnapshot operationMatch, MapOpenOperationContext? context,
        Func<bool> canPublish)
    {
        bool IsCurrent() => (context is null || IsMapOpenOperationCurrent(context))
            && IsCurrentMatchOperation(operationMatch) && _gameMapToggleState.IsCurrent(toggle)
            && canPublish();
        try
        {
            return await EvaluateAdaptiveInitialAsync(aligned, frame, diagnostics,
                isCurrent: IsCurrent,
                cancellationToken: context?.CancellationToken ?? CurrentMatchCancellationToken);
        }
        catch (OperationCanceledException) when (!IsCurrent())
        {
            return null;
        }
    }
}
