using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace IDVBuff.Plugins.IdvLogin;

public sealed record LoginAccount(string Id, string Name, string Channel = "")
{
    public override string ToString() => Name;
}

/// <summary>Only communicates with the independently running local application.</summary>
public sealed partial class IdvLoginClient : IDisposable
{
    private readonly HttpClient _http;

    public IdvLoginClient() : this(CreateHandler()) { }

    public IdvLoginClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://localhost/_idv-login/"),
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 1024 * 1024
        };
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        // Force loopback even if hosts/DNS changes. TLS still validates localhost.
        ConnectCallback = async (_, cancellation) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, 443, cancellation);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    };

    private async Task<JsonElement> GetAsync(string path, CancellationToken cancellation)
    {
        using var response = await _http.GetAsync(path, cancellation);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return json.RootElement.Clone();
    }

    public async Task<IReadOnlyList<LoginAccount>> GetAccountsAsync(CancellationToken cancellation)
    {
        var health = await GetAsync("health", cancellation);
        if (!health.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !health.TryGetProperty("status", out var status) || status.GetString() != "ok")
            throw new InvalidOperationException("idv-login 服务尚未就绪。");
        var result = await GetAsync("list?game_id=h55", cancellation);
        if (result.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("idv-login 账号接口不兼容，请检查其版本。");
        return result.EnumerateArray().Select(item => new LoginAccount(
            item.GetProperty("uuid").GetString() ?? throw new JsonException(),
            item.GetProperty("name").GetString() ?? "未命名账号",
            item.TryGetProperty("login_channel", out var channel) && channel.ValueKind == JsonValueKind.String
                ? channel.GetString() ?? "" : "")).ToArray();
    }

    public async Task<LoginSelectionResult> SelectAccountAsync(string accountId, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        var result = await GetAsync("switch?game_id=h55&uuid=" + Uri.EscapeDataString(accountId), token);
        if (result.TryGetProperty("status", out var status) && status.GetString() == "pending")
        {
            var taskId = result.GetProperty("task_id").GetString();
            if (string.IsNullOrWhiteSpace(taskId)) throw new JsonException();
            do
            {
                await Task.Delay(700, token);
                result = await GetAsync("switch-status?task_id=" + Uri.EscapeDataString(taskId), token);
                var state = result.GetProperty("status").GetString();
                if (state == "done")
                    return result.TryGetProperty("result", out var value)
                        ? LoginSelectionResponse.Parse(value) : throw new JsonException();
                if (state != "pending") throw new JsonException();
            } while (true);
        }
        // Legacy synchronous responses only confirm selection, not successful game login.
        throw new InvalidOperationException("当前 idv-login 未返回可确认的登录结果，请在其账号界面完成登录。");
    }

    public async Task<LoginSelectionResult> SelectAccountWhenReadyAsync(string accountId,
        Action<LoginSelectionResult> progress, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            var result = await SelectAccountAsync(accountId, timeout.Token);
            progress(result);
            if (result != LoginSelectionResult.WaitingForGame) return result;
            // Only retry the explicitly unsubmitted preparation branch, never a
            // failed, unrecognized, or already submitted login operation.
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        }
    }

    public void Dispose() => _http.Dispose();
}
