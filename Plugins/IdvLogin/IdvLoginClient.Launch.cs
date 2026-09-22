using System.Text.Json;

namespace IDVBuff.Plugins.IdvLogin;

public sealed partial class IdvLoginClient
{
    public async Task LaunchWithAccountAsync(string accountId, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await SetLoginModeAsync(channel: true, cancellation);
        // The upstream auto-login consumer runs when a NEW game QR is created.
        // switch before launch instead consumes a stale QR, or prepares credentials
        // for its fallback game. Do not race that consumer with switch polling.
        var selected = await GetAsync("setDefault?game_id=h55&uuid=" +
            Uri.EscapeDataString(accountId), cancellation);
        if (!Succeeded(selected))
            throw new LoginLaunchException("未能选择账号，请重试");
        var current = await GetAsync("defaultChannel?game_id=h55", cancellation);
        if (!current.TryGetProperty("uuid", out var id) ||
            id.ValueKind != JsonValueKind.String || id.GetString() != accountId)
            throw new LoginLaunchException("账号已变更，请重新选择");

        // Let idv-login own startup arguments and proxy environment. Starting
        // through the real Fever client can silently reuse its official account.
        // This only launches the game; it does not confirm game authentication.
        var launched = await GetAsync("start-game?game_id=h55", cancellation);
        if (!Succeeded(launched))
        {
            var missingPath = launched.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String && error.GetString() == "游戏路径未设置";
            throw new LoginLaunchException(missingPath
                ? "idv-login 尚未识别游戏安装位置"
                : "idv-login 未能启动游戏，请检查其运行状态");
        }
    }

    private static bool Succeeded(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
}

public sealed class LoginLaunchException(string message) : Exception(message);
