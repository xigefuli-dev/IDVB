namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public event Func<bool, Task>? MatchPluginActivationChanged;

}
