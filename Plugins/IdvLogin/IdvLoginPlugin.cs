using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.IdvLogin;

[Plugin(PluginId, DisplayName = "idv-login 转接器", Version = "1.0.0",
    Description = "连接本机独立运行的 idv-login，选择账号登录；局外也可使用。",
    StrictMatchLifecycle = false)]
public sealed partial class IdvLoginPlugin : PluginBase
{
    public const string PluginId = "idv-login";
    private CancellationTokenSource? _lifetime;
    private IdvLoginClient? _client;
    public override string Id => PluginId;
    public override string DisplayName => "idv-login 转接器";
    public CancellationToken Lifetime => _lifetime?.Token ?? new CancellationToken(true);
    public LoginImportState? ImportState { get; private set; }
    private Task? _import;
    private Task? _startup;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private DateTimeOffset _nextAdapterCheck;

    public override void OnEnable()
    {
        _lifetime = new CancellationTokenSource();
        _client = new IdvLoginClient();
        _nextAdapterCheck = DateTimeOffset.MinValue;
        ConnectionStatus = "";
        if (WaitForHostReady is not null) _startup = StartAfterHostReadyAsync(_client, Lifetime);
    }

    public async Task<IReadOnlyList<LoginAccount>> GetAccountsAsync(CancellationToken cancellation)
    {
        var client = _client ?? throw new InvalidOperationException("请先启用账号登录插件。");
        if (_startup is { IsCompleted: false } startup) await startup.WaitAsync(cancellation);
        var accounts = await client.GetAccountsAsync(cancellation);
        if (DateTimeOffset.UtcNow >= _nextAdapterCheck)
        {
            var notice = await client.GetAdapterNoticeAsync(cancellation);
            if (ReferenceEquals(client, _client) && !cancellation.IsCancellationRequested)
            {
                ConnectionStatus = notice;
                _nextAdapterCheck = DateTimeOffset.UtcNow.AddSeconds(5);
            }
        }
        return accounts;
    }

    public Task<LoginSelectionResult> SelectAccountAsync(string id, Action<LoginSelectionResult> progress,
        CancellationToken cancellation) =>
        (_client ?? throw new InvalidOperationException("请先启用账号登录插件。"))
            .SelectAccountWhenReadyAsync(id, progress, cancellation);

    public async Task LaunchWithAccountAsync(string id, CancellationToken cancellation)
    {
        if (!await _launchGate.WaitAsync(0, cancellation))
            throw new LoginLaunchException("游戏正在启动，请稍候");
        try
        {
            await (_client ?? throw new LoginLaunchException("请先启用账号登录插件"))
                .LaunchWithAccountAsync(id, cancellation);
        }
        finally { _launchGate.Release(); }
    }

    public Task<IReadOnlyList<LoginChannel>> GetChannelsAsync(CancellationToken cancellation) =>
        (_client ?? throw new InvalidOperationException("请先启用账号登录插件。")).GetChannelsAsync(cancellation);

    public void StartImport(LoginChannel channel)
    {
        if (_import is { IsCompleted: false }) return;
        var client = _client ?? throw new InvalidOperationException("请先启用账号登录插件。");
        var token = Lifetime;
        Report(new(channel.Id, "loading"));
        _import = RunAsync();
        async Task RunAsync()
        {
            try { await client.ImportAsync(channel, Report, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (OperationCanceledException) { Report(new(channel.Id, "timeout", Completed: true)); }
            catch { if (!token.IsCancellationRequested) Report(new(channel.Id, "failed", Completed: true)); }
        }
        void Report(LoginImportState state)
        {
            if (token.IsCancellationRequested) return;
            ImportState = state;
        }
    }

    public void ClearCompletedImport()
    {
        if (_import is not { IsCompleted: false }) ImportState = null;
    }

    public override void OnDisable()
    {
        _lifetime?.Cancel();
        _client?.Dispose();
        _client = null;
        _lifetime?.Dispose();
        _lifetime = null;
        ImportState = null;
        _import = null;
    }

    public override void OnUnload() => OnDisable();
}
