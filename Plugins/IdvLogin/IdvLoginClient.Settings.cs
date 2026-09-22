using System.Net.Http.Json;

namespace IDVBuff.Plugins.IdvLogin;

public sealed partial class IdvLoginClient
{
    public async Task<bool> IsOlderAdapterAsync(CancellationToken token)
    {
        var status = await GetAsync("idvb/status", token);
        return Succeeded(status) && status.TryGetProperty("adapter_version", out var version) &&
            version.TryGetInt32(out var number) && number is > 0 and < 4;
    }
    public async Task<string> GetAdapterNoticeAsync(CancellationToken token)
    {
        try
        {
            var status = await GetAsync("idvb/status", token);
            if (!Succeeded(status)) return "账号可用，适配扩展状态未确认";
            var version = status.GetProperty("adapter_version").GetInt32();
            if (version < 4)
                return $"账号可用；旧版适配层 v{version} 仍在运行，退出 idv-login 后重新启用插件以修复弹页";
            if (!status.TryGetProperty("login_mode", out var mode) || mode.ValueKind != System.Text.Json.JsonValueKind.True)
                return "账号可读取，官服／渠道服切换暂不可用";
            if (!status.GetProperty("account_source").GetBoolean())
                return "账号可用，渠道来源功能暂不可用";
            if (!status.GetProperty("suppress_auto_accounts").GetBoolean())
                return "账号可用，管理页弹出抑制功能暂不可用";
            return "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return "账号可用，未检测到适配扩展；请退出 idv-login 后重新启用插件"; }
    }
    public async Task SetLoginModeAsync(bool channel, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync("idvb/login-mode", new { mode = channel ? "channel" : "official" }, token);
        response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!Succeeded(json.RootElement)) throw new LoginLaunchException("未能切换官服／渠道服登录模式");
    }
    public async Task<bool> IsReadyAsync(CancellationToken token)
    {
        try { return Succeeded(await GetAsync("health", token)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public async Task UseLongLivedRecordingAsync(CancellationToken token)
    {
        // Upstream documents native-save as ~3 days versus a month or more for
        // recorded channel credentials. No API can override server token expiry.
        foreach (var (endpoint, enabled) in new[] { ("scan-record-setting", true), ("native-save-setting", false) })
        {
            using var response = await _http.PostAsJsonAsync(endpoint, new { enabled }, token);
            response.EnsureSuccessStatusCode();
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!Succeeded(json.RootElement)) throw new InvalidOperationException("无法设置账号保存方式");
        }
    }
}
