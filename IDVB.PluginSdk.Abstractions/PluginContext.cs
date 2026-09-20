namespace IdentityVisionBridge.PluginSdk;

public interface IIdvbPluginContext
{
    PluginIdentity Identity { get; }

    IPluginLogger Logger { get; }

    IPluginSettings Settings { get; }

    IPluginTaskRegistry Tasks { get; }

    bool TryGetCapability<TCapability>(out TCapability? capability)
        where TCapability : class, IPluginCapability;
}

public sealed record PluginIdentity
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Version { get; init; }

    public required string PublisherId { get; init; }
}

public interface IPluginLogger
{
    void Log(PluginLogLevel level, string message, Exception? exception = null);
}

public enum PluginLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public interface IPluginTaskRegistry
{
    PluginTaskHandle Run(string name, Func<CancellationToken, Task> operation);
}

public abstract class PluginTaskHandle : IAsyncDisposable
{
    public abstract string Name { get; }

    public abstract Task Completion { get; }

    public abstract ValueTask DisposeAsync();
}
