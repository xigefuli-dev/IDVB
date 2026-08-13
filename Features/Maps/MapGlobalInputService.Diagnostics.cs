namespace IDVBuff.Features.Maps;

public sealed partial class MapGlobalInputService
{
    private void DispatchInput(
        MapInputInvokedEventArgs invoked,
        string device,
        string binding,
        string actionName,
        Action handler)
    {
        LogInputDispatch(MapLogLevel.Info, "input-matched", invoked,
            device, binding, actionName);

        try
        {
            if (_dispatcher.TryEnqueue(() => handler()))
            {
                LogInputDispatch(MapLogLevel.Info, "dispatch-accepted", invoked,
                    device, binding, actionName);
                return;
            }

            LogInputDispatch(MapLogLevel.Error, "dispatch-rejected", invoked,
                device, binding, actionName);
        }
        catch (Exception exception)
        {
            LogInputDispatch(MapLogLevel.Error, "dispatch-rejected", invoked,
                device, binding, actionName, exception);
        }
    }

    private static void LogInputDispatch(
        MapLogLevel level,
        string outcome,
        MapInputInvokedEventArgs invoked,
        string device,
        string binding,
        string actionName,
        Exception? exception = null)
    {
        try
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.System,
                level,
                $"Global input: {actionName} · {outcome}",
                details: new()
                {
                    ["outcome"] = outcome,
                    ["action"] = actionName,
                    ["device"] = device,
                    ["binding"] = binding,
                    ["inputTimestamp"] = invoked.Timestamp,
                    ["exceptionType"] = exception?.GetType().FullName,
                    ["exception"] = exception?.ToString()
                });
        }
        catch
        {
        }
    }
}
