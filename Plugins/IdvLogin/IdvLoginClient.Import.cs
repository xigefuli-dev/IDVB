using System.Text.Json;

namespace IDVBuff.Plugins.IdvLogin;

public sealed record LoginChannel(string Id, string Name)
{
    public bool SupportsInlineQr => Id is "myapp" or "bilibili_sdk" or "huawei";
    public override string ToString() => Name;
}

public sealed record LoginImportState(string Channel, string Status, string QrBase64 = "",
    bool Completed = false, bool Success = false)
{
    public string UserMessage => Status switch
    {
        "ready" or "waiting" => Channel switch
        {
            "huawei" => "请用手机浏览器扫码（非游戏扫一扫）",
            "myapp" => "请用微信扫一扫",
            "bilibili_sdk" => "请用哔哩哔哩扫一扫",
            _ => "请使用对应渠道扫码"
        },
        "scanned" or "confirmed" => "已扫码，请在手机上确认登录",
        "verified" => "验证成功，正在保存账号…",
        "success" => "账号已添加",
        "expired" => "二维码已过期，请返回重试",
        "cancelled" => "已取消，请返回重试",
        "authorization" => "请在授权窗口完成验证",
        "failed" => "添加未完成，请返回重试",
        "timeout" => "等待超时，请返回重试",
        _ => "正在生成二维码…"
    };
}

public sealed partial class IdvLoginClient
{
    public async Task<IReadOnlyList<LoginChannel>> GetChannelsAsync(CancellationToken cancellation)
    {
        var result = await GetAsync("manualChannels?game_id=h55", cancellation);
        if (result.ValueKind != JsonValueKind.Array) throw new JsonException();
        return result.EnumerateArray().Select(item => new LoginChannel(
            item.GetProperty("channel").GetString() ?? throw new JsonException(),
            item.TryGetProperty("name", out var name) ? name.GetString() ?? "其他渠道" :
                item.GetProperty("channel").GetString() == "oppo" ? "OPPO" : "其他渠道")).ToArray();
    }

    public async Task ImportAsync(LoginChannel channel, Action<LoginImportState> report,
        CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        var channelQuery = "channel=" + Uri.EscapeDataString(channel.Id) + "&game_id=h55";
        var previousQr = "";
        if (channel.SupportsInlineQr)
        {
            var previous = await GetAsync("qrcode?" + channelQuery, token);
            previousQr = ReadQr(previous);
        }
        var started = await GetAsync("import?" + channelQuery +
            (channel.SupportsInlineQr ? "&login_method=qr" : ""), token);
        if (!started.TryGetProperty("status", out var status) || status.GetString() != "pending")
            throw new InvalidOperationException("idv-login 不支持异步添加账号。");
        var taskId = started.GetProperty("task_id").GetString();
        if (string.IsNullOrWhiteSpace(taskId)) throw new JsonException();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var task = await GetAsync("import-status?task_id=" + Uri.EscapeDataString(taskId), token);
            var state = task.GetProperty("status").GetString();
            if (state == "done")
            {
                var success = task.TryGetProperty("success", out var value) && value.ValueKind == JsonValueKind.True;
                report(new(channel.Id, success ? "success" : "failed", Completed: true, Success: success));
                return;
            }
            if (state != "pending") throw new JsonException();
            if (channel.SupportsInlineQr)
            {
                var qr = await GetAsync("qrcode?" + channelQuery, token);
                var image = ReadQr(qr);
                var qrStatus = qr.TryGetProperty("status", out var s) ? s.GetString() ?? "loading" : "loading";
                // Upstream keeps the previous QR in a shared cache until its worker starts.
                // Never display that QR as a newly created login challenge.
                report(new(channel.Id, image.Length > 0 && image == previousQr ? "loading" : qrStatus,
                    image == previousQr || qrStatus is not ("ready" or "waiting") ? "" : image));
            }
            else
                report(new(channel.Id, "authorization"));
            await Task.Delay(1000, token);
        }
    }

    private static string ReadQr(JsonElement qr) =>
        qr.TryGetProperty("qrcode_base64", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
}
