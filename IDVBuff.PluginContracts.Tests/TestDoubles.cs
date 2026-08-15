using IDVBuff.PluginContracts;

namespace IDVBuff.PluginContracts.Tests;

/// <summary>
/// 记录生命周期调用顺序的测试插件。
/// </summary>
internal sealed class RecordingPlugin : PluginBase
{
    private readonly string _id;
    private readonly List<string>? _globalCalls;

    public RecordingPlugin(string id, List<string>? globalCalls = null)
    {
        _id = id;
        _globalCalls = globalCalls;
    }

    public override string Id => _id;

    public override string DisplayName => _id;

    public List<string> Calls { get; } = new();

    public Action? OnTickAction { get; set; }

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);
        Calls.Add("load");
        _globalCalls?.Add($"{_id}:load");
    }

    public override void OnEnable()
    {
        Calls.Add("enable");
        _globalCalls?.Add($"{_id}:enable");
    }

    public override void OnStart()
    {
        Calls.Add("start");
        _globalCalls?.Add($"{_id}:start");
    }

    public override void OnTick()
    {
        Calls.Add("tick");
        _globalCalls?.Add($"{_id}:tick");
        OnTickAction?.Invoke();
    }

    public override void OnDisable()
    {
        Calls.Add("disable");
        _globalCalls?.Add($"{_id}:disable");
    }

    public override void OnUnload()
    {
        Calls.Add("unload");
        _globalCalls?.Add($"{_id}:unload");
    }
}

/// <summary>
/// 测试用的上下文工厂，共享同一个 FakeLogger 以便断言异常记录。
/// </summary>
internal sealed class FakeContextFactory : IPluginContextFactory
{
    public FakeLogger Logger { get; } = new();

    public List<IPluginContext> Created { get; } = new();

    public IPluginContext Create(IPlugin plugin)
    {
        var context = new FakeContext(plugin, Logger);
        Created.Add(context);
        return context;
    }
}

internal sealed class FakeContext : IPluginContext
{
    public FakeContext(IPlugin plugin, FakeLogger logger)
    {
        PluginId = plugin.Id;
        PluginDisplayName = plugin.DisplayName;
        Logger = logger;
    }

    public string PluginId { get; }

    public string PluginDisplayName { get; }

    public IPluginLogger Logger { get; }

    public IMessageBus Messages { get; } = new MessageBus();

    public IPluginSynchronizer UI { get; } = new NullSynchronizer();

    public object? GetService(Type serviceType) => null;

    public T? GetService<T>() where T : class => null;
}

internal sealed class FakeLogger : IPluginLogger
{
    public List<string> Errors { get; } = new();

    public void Info(string message, IReadOnlyDictionary<string, object?>? details = null) { }

    public void Warning(string message, IReadOnlyDictionary<string, object?>? details = null) { }

    public void Error(string message, IReadOnlyDictionary<string, object?>? details = null) =>
        Errors.Add(message);
}

internal sealed class NullSynchronizer : IPluginSynchronizer
{
    public bool HasThreadAccess => true;

    public bool TryPost(Action action)
    {
        action();
        return true;
    }

    public bool TryPost<T>(Action<T> action, T state)
    {
        action(state);
        return true;
    }
}
