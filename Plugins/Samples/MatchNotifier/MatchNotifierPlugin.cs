using IdentityVisionBridge.PluginSdk;

namespace IDVB.Sample.MatchNotifier;

public sealed class MatchNotifierPlugin : IIdvbPlugin, IPluginCommandHandler
{
    private IIdvbPluginContext? _context;
    private IPluginNotificationsCapability? _notifications;
    private IDisposable? _matchSubscription;
    private IDisposable? _settingsSubscription;
    private string _prefix = "Match";

    public ValueTask InitializeAsync(IIdvbPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _prefix = context.Settings.Current.GetString("prefix", "Match") ?? "Match";
        _settingsSubscription = context.Settings.Subscribe(change =>
            _prefix = change.Snapshot.GetString("prefix", "Match") ?? "Match");
        context.TryGetCapability(out _notifications);
        if (context.TryGetCapability<IHostEventsCapability>(out var events))
            _matchSubscription = events!.Subscribe<MatchStateChangedEvent>(OnMatchChangedAsync);
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        _context?.Logger.Log(PluginLogLevel.Information, "Match Notifier started.");
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _matchSubscription?.Dispose();
        _matchSubscription = null;
        _settingsSubscription?.Dispose();
        _settingsSubscription = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _matchSubscription?.Dispose();
        _settingsSubscription?.Dispose();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PluginCommandResult> ExecuteAsync(
        string commandId,
        CancellationToken cancellationToken)
    {
        if (commandId != "test-notification")
            return PluginCommandResult.Failure("Unknown command.");
        if (_notifications is null)
            return PluginCommandResult.Failure("Notification capability was not granted.");
        await _notifications.PostAsync(
            new PluginNotification
            {
                Title = "Match Notifier",
                Message = "The sample command is working.",
                Severity = PluginNotificationSeverity.Success
            },
            cancellationToken);
        return PluginCommandResult.Success("Test notification posted.");
    }

    private async ValueTask OnMatchChangedAsync(
        MatchStateChangedEvent match,
        CancellationToken cancellationToken)
    {
        if (_notifications is null || !_context!.Settings.Current.GetBoolean("notify", true))
            return;
        await _notifications.PostAsync(
            new PluginNotification
            {
                Title = _prefix,
                Message = $"Match state: {match.State}",
                Severity = PluginNotificationSeverity.Information
            },
            cancellationToken);
    }
}
