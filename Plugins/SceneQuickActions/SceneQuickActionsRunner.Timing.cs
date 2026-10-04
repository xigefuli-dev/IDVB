namespace IDVBuff.Plugins.SceneQuickActions;

internal sealed partial class SceneQuickActionsRunner
{
    private static void SleepAfterStep(SceneQuickActionsOptions options, int baseMilliseconds,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        NativeInput.Sleep(options.GetDelayAfterStep(baseMilliseconds), token);
        token.ThrowIfCancellationRequested();
    }
}
