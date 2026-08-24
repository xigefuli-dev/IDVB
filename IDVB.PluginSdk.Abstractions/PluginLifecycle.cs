namespace IdentityVisionBridge.PluginSdk;

public interface IIdvbPlugin : IAsyncDisposable
{
    ValueTask InitializeAsync(IIdvbPluginContext context, CancellationToken cancellationToken);

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IPluginCommandHandler
{
    ValueTask<PluginCommandResult> ExecuteAsync(string commandId, CancellationToken cancellationToken);
}

public sealed record PluginCommandResult
{
    public required PluginCommandStatus Status { get; init; }

    public string? Message { get; init; }

    public static PluginCommandResult Success(string? message = null) =>
        new() { Status = PluginCommandStatus.Success, Message = message };

    public static PluginCommandResult Failure(string message) =>
        new() { Status = PluginCommandStatus.Failure, Message = message };

    public static PluginCommandResult Cancelled(string? message = null) =>
        new() { Status = PluginCommandStatus.Cancelled, Message = message };
}

public enum PluginCommandStatus
{
    Success,
    Failure,
    Cancelled
}
