using System.Diagnostics;
using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.IdvLogin;

public sealed partial class IdvLoginPlugin : IPluginSettingsProvider
{
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    public string InstallationPath { get; private set; } = "";
    public Func<Task>? WaitForHostReady { get; init; }
    public string ConnectionStatus { get; private set; } = "";
    public bool HasValidPath => ResolveStartInfo(InstallationPath) is not null;
    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginTextSetting
        {
            Key = "installation-path", DisplayName = "idv-login 安装文件夹",
            Description = "选择已安装的 idv-login 文件夹后启用插件。",
            PlaceholderText = @"D:\ProgramData\IDV-Login", MaxLength = 1024
        }
    ];
    public object? GetSettingValue(string key) => key == "installation-path" ? InstallationPath : null;
    public void SetSettingValue(string key, object? value)
    {
        if (key == "installation-path") InstallationPath = (value as string ?? "").Trim().Trim('"');
    }

    public static ProcessStartInfo? ResolveStartInfo(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
            if (!Directory.Exists(path) && !File.Exists(path)) return null;
            var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
            var python = Path.Combine(root, "python-embed", "python.exe");
            var main = Path.Combine(root, "src", "main.pyc");
            if (!File.Exists(main)) main = Path.Combine(root, "src", "main.py");
            ProcessStartInfo start;
            if (File.Exists(python) && File.Exists(main))
            {
                start = new(python);
                var adapter = Path.Combine(AppContext.BaseDirectory, "IdvLoginAdapter", "adapter_bootstrap.py");
                if (!File.Exists(adapter)) return null;
                start.ArgumentList.Add(adapter);
                start.ArgumentList.Add(root);
            }
            else if (File.Exists(path) && string.Equals(Path.GetFileName(path), "idv-login.exe", StringComparison.OrdinalIgnoreCase))
                start = new(path);
            else return null;
            start.WorkingDirectory = root;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            return start;
        }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
    }

    private async Task StartAfterHostReadyAsync(IdvLoginClient client, CancellationToken token)
    {
        var entered = false;
        try
        {
            if (WaitForHostReady is not null) await WaitForHostReady().WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!HasValidPath) return;
            await _startupGate.WaitAsync(token);
            entered = true;
            ConnectionStatus = "正在连接 idv-login…";
            var ready = await client.IsReadyAsync(token);
            if (ready)
            {
                var oldAdapter = false;
                try { oldAdapter = await client.IsOlderAdapterAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* No verified adapter means no permission to terminate this listener. */ }
                if (oldAdapter)
                {
                    ConnectionStatus = "正在重启旧版 idv-login 适配层…";
                    try
                    {
                        await LoginProcessReplacement.StopVerifiedListenerAsync(ResolveStartInfo(InstallationPath)!, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch
                    {
                        if (!token.IsCancellationRequested) ConnectionStatus = "账号可用，但无法安全重启旧进程；请手动退出 idv-login 后重新启用插件";
                        return;
                    }
                    ready = false;
                }
            }
            if (!ready)
            {
                token.ThrowIfCancellationRequested();
                using var process = Process.Start(ResolveStartInfo(InstallationPath)!);
                if (process is null) throw new InvalidOperationException();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                while (!await client.IsReadyAsync(timeout.Token))
                    await Task.Delay(1000, timeout.Token);
            }
            await client.UseLongLivedRecordingAsync(token);
            var notice = await client.GetAdapterNoticeAsync(token);
            if (!token.IsCancellationRequested) ConnectionStatus = notice;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (!token.IsCancellationRequested)
                ConnectionStatus = "idv-login 未就绪，请检查路径、启动权限或运行状态";
        }
        finally { if (entered) _startupGate.Release(); }
    }

    public async Task StopIfRunningAsync(CancellationToken token = default)
    {
        try
        {
            var startInfo = ResolveStartInfo(InstallationPath);
            if (startInfo is null) return;

            if (LoginProcessReplacement.TryFindListener() is null)
                return;

            var stopped = false;
            if (_client is not null)
            {
                try
                {
                    stopped = await _client.TryStopAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { stopped = false; }
            }
            else
            {
                try
                {
                    using var tempClient = new IdvLoginClient();
                    stopped = await tempClient.TryStopAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { stopped = false; }
            }

            if (stopped)
            {
                using var waitTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                waitTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                while (LoginProcessReplacement.TryFindListener() is not null)
                {
                    try { await Task.Delay(150, waitTimeout.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            if (LoginProcessReplacement.TryFindListener() is not null)
            {
                await LoginProcessReplacement.TryStopVerifiedListenerAsync(startInfo, token);
            }

            ConnectionStatus = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            // 静默非阻塞：停止信号不应阻断官服启动流程
        }
    }

    public async Task EnsureRunningAsync(CancellationToken token = default)
    {
        var client = _client ?? throw new InvalidOperationException("请先启用账号登录插件。");
        if (!HasValidPath) return;
        if (!await client.IsReadyAsync(token))
        {
            await StartAfterHostReadyAsync(client, token);
        }
    }
}
