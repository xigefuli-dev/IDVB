using IdentityVisionBridge.PluginSdk;

namespace IdvbPluginTemplate;

public sealed class Plugin : IIdvbPlugin, IPluginCommandHandler
{
    private IIdvbPluginContext? _context;

    public ValueTask InitializeAsync(IIdvbPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask<PluginCommandResult> ExecuteAsync(string commandId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginCommandResult.Success($"Executed {commandId}."));
}
