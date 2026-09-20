using System.Threading.Channels;
using IDVBuff.PluginContracts;
using IdentityVisionBridge.PluginPackaging;
using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.PluginSdk;
using LegacyScreenshotService = IDVBuff.PluginContracts.IPluginScreenshotService;
using SdkScreenshotResult = IdentityVisionBridge.PluginSdk.PluginScreenshotResult;

namespace IDVBuff.Features.Plugins.V2;

public sealed class ThirdPartyPluginCapabilitySource : IPluginCapabilitySource
{
    private readonly ThirdPartyHostEventHub _events;
    private readonly IPluginInputService _input;
    private readonly LegacyScreenshotService _screenshots;
    private readonly PluginNotificationCenter _notifications;
    private readonly Action<string, Exception>? _reportFault;

    public ThirdPartyPluginCapabilitySource(
        ThirdPartyHostEventHub events,
        IPluginInputService input,
        LegacyScreenshotService screenshots,
        PluginNotificationCenter notifications,
        Action<string, Exception>? reportFault = null)
    {
        _events = events;
        _input = input;
        _screenshots = screenshots;
        _notifications = notifications;
        _reportFault = reportFault;
    }

    public ValueTask<IReadOnlyDictionary<Type, IPluginCapability>> CreateAsync(
        IdvpManifest manifest,
        string dataDirectory,
        PluginSettingsService settings,
        IReadOnlySet<string> grantedCapabilities,
        CancellationToken pluginLifetime,
        CancellationToken cancellationToken)
    {
        var capabilities = new Dictionary<Type, IPluginCapability>();
        if (grantedCapabilities.Contains(PluginCapabilityIds.HostEventsRead))
            capabilities[typeof(IHostEventsCapability)] = _events.CreateCapability(
                manifest.Id, pluginLifetime, _reportFault);
        if (grantedCapabilities.Contains(PluginCapabilityIds.InputBindings))
            capabilities[typeof(IInputBindingsCapability)] = new InputBindingsCapability(
                manifest.Id, manifest.Settings, settings, _input, pluginLifetime, _reportFault);
        if (grantedCapabilities.Contains(PluginCapabilityIds.CaptureScreenshot))
            capabilities[typeof(IScreenshotCapability)] = new ScreenshotCapability(_screenshots);
        if (grantedCapabilities.Contains(PluginCapabilityIds.StoragePrivate))
            capabilities[typeof(IPluginStorageCapability)] = new StorageCapability(dataDirectory);
        if (grantedCapabilities.Contains(PluginCapabilityIds.NotificationsPost))
            capabilities[typeof(IPluginNotificationsCapability)] = new NotificationsCapability(
                manifest.Id, _notifications);
        return ValueTask.FromResult<IReadOnlyDictionary<Type, IPluginCapability>>(capabilities);
    }

    private sealed class StorageCapability : IPluginStorageCapability
    {
        public StorageCapability(string rootDirectory)
        {
            Directory.CreateDirectory(rootDirectory);
            RootDirectory = Path.GetFullPath(rootDirectory);
        }

        public string RootDirectory { get; }
    }

    private sealed class ScreenshotCapability(LegacyScreenshotService screenshots) : IScreenshotCapability
    {
        public async ValueTask<SdkScreenshotResult> CaptureAsync(CancellationToken cancellationToken)
        {
            var result = await screenshots.CaptureAsync(TimeSpan.Zero, cancellationToken);
            return result.Succeeded
                ? new SdkScreenshotResult
                {
                    Succeeded = true,
                    PngBytes = result.Screenshot!.ImageBytes
                }
                : new SdkScreenshotResult
                {
                    Succeeded = false,
                    ErrorCode = "capture_failed",
                    UserMessage = result.FailureReason
                };
        }
    }

    private sealed class NotificationsCapability(
        string pluginId,
        PluginNotificationCenter center) : IPluginNotificationsCapability
    {
        private readonly object _sync = new();
        private readonly Queue<DateTimeOffset> _recent = new();

        public ValueTask PostAsync(PluginNotification notification, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(notification);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(notification.Title) || notification.Title.Length > 128 ||
                string.IsNullOrWhiteSpace(notification.Message) || notification.Message.Length > 1000)
            {
                throw new ArgumentException("Plugin notification text is empty or too long.", nameof(notification));
            }

            lock (_sync)
            {
                var threshold = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
                while (_recent.TryPeek(out var postedAt) && postedAt < threshold)
                    _recent.Dequeue();
                if (_recent.Count >= 5)
                    throw new InvalidOperationException("Plugin notification rate limit exceeded.");
                _recent.Enqueue(DateTimeOffset.UtcNow);
            }

            center.Post(pluginId, notification);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InputBindingsCapability :
        IInputBindingsCapability,
        IRevocablePluginCapability
    {
        private readonly string _pluginId;
        private readonly IPluginInputService _input;
        private readonly IReadOnlySet<string> _bindingKeys;
        private readonly object _sync = new();
        private readonly Dictionary<string, List<Func<PluginInputEvent, CancellationToken, ValueTask>>> _handlers =
            new(StringComparer.Ordinal);
        private readonly Channel<PluginInputEvent> _events = Channel.CreateBounded<PluginInputEvent>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        private readonly CancellationTokenSource _lifetime;
        private readonly IDisposable _settingsSubscription;
        private readonly Task _worker;
        private readonly Action<string, Exception>? _reportFault;
        private int _revoked;

        public InputBindingsCapability(
            string pluginId,
            IReadOnlyList<IdvpSettingDefinition> definitions,
            PluginSettingsService settings,
            IPluginInputService input,
            CancellationToken pluginLifetime,
            Action<string, Exception>? reportFault)
        {
            _pluginId = pluginId;
            _input = input;
            _reportFault = reportFault;
            _bindingKeys = definitions.Where(static definition => definition.Type == "keyBinding")
                .Select(static definition => definition.Key)
                .ToHashSet(StringComparer.Ordinal);
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(pluginLifetime);
            ApplyBindings(settings.Current);
            _settingsSubscription = settings.Subscribe(change => ApplyBindings(change.Snapshot));
            _input.BindingInvoked += OnBindingInvoked;
            _worker = Task.Run(DispatchAsync);
        }

        public IDisposable Subscribe(
            string bindingId,
            Func<PluginInputEvent, CancellationToken, ValueTask> handler)
        {
            if (!_bindingKeys.Contains(bindingId))
                throw new ArgumentException("The input binding is not declared in the plugin manifest.", nameof(bindingId));
            ArgumentNullException.ThrowIfNull(handler);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_revoked != 0, this);
                if (!_handlers.TryGetValue(bindingId, out var handlers))
                {
                    handlers = [];
                    _handlers.Add(bindingId, handlers);
                }
                handlers.Add(handler);
            }

            return new Subscription(() =>
            {
                lock (_sync)
                {
                    if (_handlers.TryGetValue(bindingId, out var handlers))
                        handlers.Remove(handler);
                }
            });
        }

        public async ValueTask RevokeAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _revoked, 1) != 0)
                return;
            _input.BindingInvoked -= OnBindingInvoked;
            _settingsSubscription.Dispose();
            _input.ClearBindings(_pluginId);
            lock (_sync)
                _handlers.Clear();
            _lifetime.Cancel();
            _events.Writer.TryComplete();
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                _lifetime.Dispose();
            }
        }

        private void ApplyBindings(PluginSettingsSnapshot snapshot)
        {
            foreach (var key in _bindingKeys)
            {
                var text = snapshot.GetString(key, "none");
                if (PluginInputBinding.TryParse(text, out var binding))
                    _input.SetBinding(_pluginId, key, binding);
            }
        }

        private void OnBindingInvoked(object? sender, PluginInputEventArgs args)
        {
            if (_revoked != 0 || args.PluginId != _pluginId)
                return;
            _events.Writer.TryWrite(new PluginInputEvent
            {
                BindingId = args.BindingKey,
                Transition = args.IsDown ? PluginInputTransition.Pressed : PluginInputTransition.Released,
                OccurredAt = DateTimeOffset.UtcNow
            });
        }

        private async Task DispatchAsync()
        {
            try
            {
                await foreach (var inputEvent in _events.Reader.ReadAllAsync(_lifetime.Token))
                {
                    Func<PluginInputEvent, CancellationToken, ValueTask>[] handlers;
                    lock (_sync)
                        handlers = _handlers.TryGetValue(inputEvent.BindingId, out var registered)
                            ? registered.ToArray()
                            : [];
                    foreach (var handler in handlers)
                    {
                        try
                        {
                            await handler(inputEvent, _lifetime.Token);
                        }
                        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception exception)
                        {
                            _reportFault?.Invoke(_pluginId, exception);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
