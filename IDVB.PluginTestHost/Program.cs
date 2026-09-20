using System.Reflection;
using System.Text.Json;
using IdentityVisionBridge.PluginSdk;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: idvb-plugin-testhost <entry.dll> <entry-type>");
    return 2;
}

var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var type = assembly.GetType(args[1], throwOnError: true)!;
if (Activator.CreateInstance(type) is not IIdvbPlugin plugin)
    throw new InvalidOperationException("Entry type does not implement IIdvbPlugin.");

await using (plugin)
{
    var context = new TestPluginContext();
    await plugin.InitializeAsync(context, CancellationToken.None);
    await plugin.StartAsync(CancellationToken.None);
    context.Events.Publish(new MatchStateChangedEvent { State = "Test", Mode = "TestHost" });
    context.Input.Publish("sample", PluginInputTransition.Pressed);
    if (plugin is IPluginCommandHandler commands)
    {
        var result = await commands.ExecuteAsync("test-notification", CancellationToken.None);
        Console.WriteLine($"command: {result.Status} {result.Message}");
    }
    await plugin.StopAsync(CancellationToken.None);
}

return 0;

public sealed class TestPluginContext : IIdvbPluginContext
{
    private readonly Dictionary<Type, IPluginCapability> _capabilities;

    public TestPluginContext(bool screenshotSucceeds = false)
    {
        Events = new TestHostEvents();
        Input = new TestInputBindings();
        Notifications = new TestNotifications();
        _capabilities = new Dictionary<Type, IPluginCapability>
        {
            [typeof(IHostEventsCapability)] = Events,
            [typeof(IInputBindingsCapability)] = Input,
            [typeof(IScreenshotCapability)] = new TestScreenshot(screenshotSucceeds),
            [typeof(IPluginStorageCapability)] = new TestStorage(),
            [typeof(IPluginNotificationsCapability)] = Notifications
        };
    }

    public PluginIdentity Identity { get; } = new()
    {
        Id = "test.plugin",
        DisplayName = "Test Plugin",
        Version = "0.0.0-test",
        PublisherId = "test.publisher"
    };

    public IPluginLogger Logger { get; } = new ConsolePluginLogger();

    public IPluginSettings Settings { get; } = new TestSettings();

    public IPluginTaskRegistry Tasks { get; } = new TestTaskRegistry();

    public TestHostEvents Events { get; }

    public TestInputBindings Input { get; }

    public TestNotifications Notifications { get; }

    public bool TryGetCapability<TCapability>(out TCapability? capability)
        where TCapability : class, IPluginCapability
    {
        capability = _capabilities.GetValueOrDefault(typeof(TCapability)) as TCapability;
        return capability is not null;
    }
}

public sealed class TestHostEvents : IHostEventsCapability
{
    private readonly Dictionary<Type, List<object>> _handlers = [];

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : PluginHostEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
            _handlers[typeof(TEvent)] = handlers = [];
        handlers.Add(handler);
        return new CallbackDisposable(() => handlers.Remove(handler));
    }

    public void Publish<TEvent>(TEvent hostEvent) where TEvent : PluginHostEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers)) return;
        foreach (var handler in handlers.Cast<Func<TEvent, CancellationToken, ValueTask>>().ToArray())
            handler(hostEvent, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }
}

public sealed class TestInputBindings : IInputBindingsCapability
{
    private readonly Dictionary<string, List<Func<PluginInputEvent, CancellationToken, ValueTask>>> _handlers =
        new(StringComparer.Ordinal);

    public IDisposable Subscribe(string bindingId, Func<PluginInputEvent, CancellationToken, ValueTask> handler)
    {
        if (!_handlers.TryGetValue(bindingId, out var handlers))
            _handlers[bindingId] = handlers = [];
        handlers.Add(handler);
        return new CallbackDisposable(() => handlers.Remove(handler));
    }

    public void Publish(string bindingId, PluginInputTransition transition)
    {
        if (!_handlers.TryGetValue(bindingId, out var handlers)) return;
        var input = new PluginInputEvent { BindingId = bindingId, Transition = transition };
        foreach (var handler in handlers.ToArray())
            handler(input, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }
}

public sealed class TestNotifications : IPluginNotificationsCapability
{
    public List<PluginNotification> Posted { get; } = [];

    public ValueTask PostAsync(PluginNotification notification, CancellationToken cancellationToken)
    {
        Posted.Add(notification);
        Console.WriteLine($"notification: {notification.Title}: {notification.Message}");
        return ValueTask.CompletedTask;
    }
}

file sealed class TestScreenshot(bool succeeds) : IScreenshotCapability
{
    public ValueTask<PluginScreenshotResult> CaptureAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PluginScreenshotResult
        {
            Succeeded = succeeds,
            PngBytes = succeeds ? [137, 80, 78, 71] : null,
            ErrorCode = succeeds ? null : "simulated_failure",
            UserMessage = succeeds ? null : "Screenshot failure simulated by test host."
        });
}

file sealed class TestStorage : IPluginStorageCapability
{
    public string RootDirectory { get; } = Path.Combine(Path.GetTempPath(), "idvb-plugin-testhost-data");
}

file sealed class ConsolePluginLogger : IPluginLogger
{
    public void Log(PluginLogLevel level, string message, Exception? exception = null) =>
        Console.WriteLine($"[{level}] {message} {exception?.Message}");
}

file sealed class TestSettings : IPluginSettings
{
    public PluginSettingsSnapshot Current { get; } = new(new Dictionary<string, JsonElement>());

    public IDisposable Subscribe(Action<PluginSettingsChanged> handler) => new CallbackDisposable(() => { });
}

file sealed class TestTaskRegistry : IPluginTaskRegistry
{
    public PluginTaskHandle Run(string name, Func<CancellationToken, Task> operation) =>
        new TestTaskHandle(name, operation);
}

file sealed class TestTaskHandle : PluginTaskHandle
{
    private readonly CancellationTokenSource _cancellation = new();

    public TestTaskHandle(string name, Func<CancellationToken, Task> operation)
    {
        Name = name;
        Completion = Task.Run(() => operation(_cancellation.Token));
    }

    public override string Name { get; }

    public override Task Completion { get; }

    public override async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        try { await Completion; } catch (OperationCanceledException) { }
        _cancellation.Dispose();
    }
}

file sealed class CallbackDisposable(Action callback) : IDisposable
{
    private Action? _callback = callback;

    public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
}
