namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void RunInputAction(string actionName, Action action)
    {
        using var input = new MapInputOperationContext();
        LogInputHandlerOutcome(actionName, "handler-started");
        try
        {
            action();
            input.Outcome = "returned";
            input.Reason = "handler-returned-without-business-verdict";
            LogInputHandlerOutcome(actionName, "handler-returned");
        }
        catch (Exception exception)
        {
            input.Outcome = "failed";
            input.Reason = exception.GetType().FullName ?? exception.GetType().Name;
            LogInputHandlerOutcome(actionName, "handler-failed", exception);
        }
    }

    private void StartInputOperation(
        string actionName,
        Func<Task> operation)
        => _ = ObserveInputOperationAsync(actionName, operation);

    private async Task ObserveInputOperationAsync(
        string actionName,
        Func<Task> operation)
    {
        using var input = new MapInputOperationContext();
        LogInputHandlerOutcome(actionName, "handler-started");
        try
        {
            await operation();
            if (input.Outcome == "pending")
            {
                input.Outcome = "returned";
                input.Reason = "handler-returned-without-business-verdict";
            }
            LogInputHandlerOutcome(actionName, "handler-returned");
        }
        catch (Exception exception)
        {
            input.Outcome = exception is OperationCanceledException ? "cancelled" : "failed";
            input.Reason = exception.GetType().FullName ?? exception.GetType().Name;
            LogInputHandlerOutcome(actionName, "handler-failed", exception);
        }
    }

    private void LogInputHandlerOutcome(
        string actionName,
        string outcome,
        Exception? exception = null)
    {
        try
        {
            _logCollector.Append(
                MapLogCategory.System,
                exception is null ? MapLogLevel.Info : MapLogLevel.Error,
                $"Input handler: {actionName} · {outcome}",
                details: new()
                {
                    ["outcome"] = outcome,
                    ["action"] = actionName,
                    ["inputOperationId"] = MapInputOperationContext.Current?.Id,
                    ["scanId"] = MapInputOperationContext.Current?.ScanId,
                    ["businessOutcome"] = MapInputOperationContext.Current?.Outcome,
                    ["businessReason"] = MapInputOperationContext.Current?.Reason,
                    ["exceptionType"] = exception?.GetType().FullName,
                    ["exception"] = exception?.ToString()
                });
        }
        catch
        {
        }
    }

}
