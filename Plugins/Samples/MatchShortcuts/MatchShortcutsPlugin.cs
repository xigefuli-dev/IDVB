using IdentityVisionBridge.PluginSdk;

namespace IDVB.Sample.MatchShortcuts;

public sealed class MatchShortcutsPlugin : IIdvbPlugin, IPluginCommandHandler
{
    public const string DifficultClass = "S0 厄运之女 · 困难";
    public const string BossClass = "S0 厄运之女 · 困难（总裁）";
    private IIdvbPluginContext? _context;
    private IHostMatchCapability? _matches;
    private IPluginNotificationsCapability? _notifications;
    private readonly List<IDisposable> _subscriptions = [];
    private int _switching;

    public ValueTask InitializeAsync(IIdvbPluginContext context, CancellationToken token)
    {
        _context = context;
        if (!context.TryGetCapability<IHostMatchCapability>(out _matches)
            || !context.TryGetCapability<IInputBindingsCapability>(out var input))
            throw new InvalidOperationException("需要批准对局控制和快捷键权限。");
        context.TryGetCapability(out _notifications);
        _subscriptions.Add(input!.Subscribe("difficult", (evt, ct) => OnInputAsync(evt, DifficultClass, ct)));
        _subscriptions.Add(input.Subscribe("boss", (evt, ct) => OnInputAsync(evt, BossClass, ct)));
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken token)
    {
        _context!.Logger.Log(PluginLogLevel.Information, "对局快捷键已注册：Alt + F + C / Alt + F + B。");
        return ValueTask.CompletedTask;
    }
    public ValueTask StopAsync(CancellationToken token) { Unsubscribe(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Unsubscribe(); return ValueTask.CompletedTask; }
    private void Unsubscribe() { foreach (var item in _subscriptions) item.Dispose(); _subscriptions.Clear(); }

    private async ValueTask OnInputAsync(PluginInputEvent evt, string mapClass, CancellationToken token)
    {
        if (evt.Transition == PluginInputTransition.Pressed) await SwitchAsync(mapClass, token);
    }

    public async ValueTask<PluginCommandResult> ExecuteAsync(string id, CancellationToken token)
    {
        var mapClass = id switch { "start-difficult" => DifficultClass, "start-boss" => BossClass, _ => null };
        if (mapClass is null) return PluginCommandResult.Failure("未知命令。");
        var result = await SwitchAsync(mapClass, token);
        return result.State == HostOperationState.Applied
            ? PluginCommandResult.Success(result.Message) : PluginCommandResult.Failure(result.Message);
    }

    private async Task<HostOperationResult> SwitchAsync(string mapClass, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _switching, 1) != 0)
            return new("", HostOperationState.Busy, "正在切换对局。");
        try
        {
            var result = await _matches!.SwitchAndBeginAsync(new() { MapClass = mapClass, EndCurrentMatch = true }, token);
            while (result.State == HostOperationState.Accepted)
            {
                await Task.Delay(100, token);
                result = await _matches.GetOperationAsync(result.OperationId, token)
                    ?? new(result.OperationId, HostOperationState.Failed, "对局操作记录已不可用。");
            }
            _context!.Logger.Log(result.State == HostOperationState.Applied ? PluginLogLevel.Information : PluginLogLevel.Warning,
                $"{result.OperationId} · {result.State} · {result.Message}");
            if (_notifications is not null)
                await _notifications.PostAsync(new()
                {
                    Title = "对局快捷切换", Message = result.Message,
                    Severity = result.State == HostOperationState.Applied ? PluginNotificationSeverity.Success : PluginNotificationSeverity.Warning
                }, token);
            return result;
        }
        finally { Volatile.Write(ref _switching, 0); }
    }
}
