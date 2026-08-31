using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IDVBuff.Features.Maps;
using IDVBuff.UpdateCore;
using Windows.Security.Credentials;

namespace IDVBuff.Features.Accounts;

internal sealed record AccountIdentity(
    string DisplayName,
    string PublisherHandle,
    string? AvatarUrl,
    bool IsOfficial);

internal static class AccountSession
{
    private const string CredentialResource = "IdentityVisionBridge.Account";
    private const string CredentialUserName = "current";
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://community.idvb.xgflee.com/") };
    private static string? _publishToken;
    public static AccountIdentity? Identity { get; private set; }
    public static event EventHandler? Changed;

    static AccountSession() => Restore();

    public static async Task<AccountIdentity> LoginAsync(CancellationToken cancellationToken = default)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/callback";
        var authorize = new UriBuilder(Http.BaseAddress!) { Path = "oauth/authorize" };
        authorize.Query = string.Join("&",
            $"client_id=idvb-desktop",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"code_challenge={challenge}",
            $"state={state}");
        if (!await Windows.System.Launcher.LaunchUriAsync(authorize.Uri))
            throw new InvalidOperationException("无法打开系统浏览器。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(timeout.Token) ?? string.Empty;
        var target = requestLine.Split(' ').ElementAtOrDefault(1);
        var callback = target is null ? null : new Uri(new Uri(redirectUri), target);
        var values = ParseQuery(callback?.Query);
        values.TryGetValue("code", out var code);
        var callbackAccepted = values.GetValueOrDefault("state") == state
            && code is not null;
        var responseMessage = callbackAccepted
            ? "登录完成，可以关闭此页面并返回 IDVB。"
            : values.GetValueOrDefault("error") == "access_denied"
                ? "已取消登录，可以关闭此页面并返回 IDVB。"
                : "登录未完成，请返回 IDVB 后重试。";
        var responseText = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n<!doctype html><meta charset=utf-8><title>IDVB</title><p>{responseMessage}</p>";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(responseText), timeout.Token);
        if (!callbackAccepted)
            throw values.GetValueOrDefault("error") == "access_denied"
                ? new OperationCanceledException("已取消登录。")
                : new UnauthorizedAccessException("登录未完成，请重试。");

        using var tokenResponse = await Http.PostAsync("api/oauth/token", new StringContent(
            JsonSerializer.Serialize(new { clientId = "idvb-desktop", redirectUri, code = code!, codeVerifier = verifier }),
            Encoding.UTF8, "application/json"), timeout.Token);
        var json = await tokenResponse.Content.ReadAsStringAsync(timeout.Token);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            using var error = JsonDocument.Parse(json);
            throw new UnauthorizedAccessException(
                error.RootElement.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "登录未完成或已过期。"
                    : "登录未完成或已过期。");
        }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var user = root.GetProperty("user");
        var identity = new AccountIdentity(
            user.GetProperty("displayName").GetString() ?? "IDVB 用户",
            user.GetProperty("publisherHandle").GetString() ?? throw new InvalidDataException("账户缺少发布者标识。"),
            user.TryGetProperty("avatarUrl", out var avatar) ? avatar.GetString() : null,
            user.TryGetProperty("isOfficial", out var official) && official.GetBoolean());
        Set(root.GetProperty("token").GetString()!, identity);
        return identity;
    }

    public static void Set(string token, AccountIdentity identity)
    {
        _publishToken = token;
        Identity = identity;
        Save(token, identity);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Clear()
    {
        _publishToken = null;
        Identity = null;
        RemoveSaved();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static async Task LogoutAsync()
    {
        var token = _publishToken;
        try
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, "api/auth/publish-token");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                _ = await Http.SendAsync(request);
            }
        }
        finally
        {
            Clear();
        }
    }

    public static async Task<AccountIdentity> RequirePublishAccessAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_publishToken))
            throw new UnauthorizedAccessException("请先在左侧“账户”中登录。");

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/auth/publish-token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _publishToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Clear();
            throw new UnauthorizedAccessException("登录已过期，请重新登录。");
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var user = document.RootElement.GetProperty("user");
        return new AccountIdentity(
            user.GetProperty("displayName").GetString() ?? "IDVB 用户",
            user.GetProperty("publisherHandle").GetString() ?? throw new InvalidDataException("账户缺少发布者标识。"),
            user.TryGetProperty("avatarUrl", out var avatar) ? avatar.GetString() : null,
            user.TryGetProperty("isOfficial", out var official) && official.GetBoolean());
    }

    public static async Task<string> UploadPublicationAsync(
        MapPublicationResult publication,
        CancellationToken cancellationToken = default)
    {
        _ = await RequirePublishAccessAsync(cancellationToken);
        var link = MapSubscriptionLink.Parse(publication.SubscriptionLink);
        var feedPath = Path.Combine(publication.OutputDirectory, "feed.json");
        var packagePath = Directory.EnumerateFiles(
            publication.OutputDirectory, "*.idvm.secure", SearchOption.TopDirectoryOnly).Single();
        await using var package = File.OpenRead(packagePath);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(feedPath, cancellationToken)), "feed", "feed.json");
        form.Add(new StreamContent(package), "package", Path.GetFileName(packagePath));
        form.Add(new StringContent(Base64Url(link.ContentKey)), "contentKey");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/maps/publish") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _publishToken);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString() ?? "官网拒绝了地图发布。"
                : "官网拒绝了地图发布。");
        return document.RootElement.GetProperty("subscriptionLink").GetString()
            ?? throw new InvalidDataException("官网没有返回更新订阅链接。");
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void Save(string token, AccountIdentity identity)
    {
        RemoveSaved();
        new PasswordVault().Add(new PasswordCredential(
            CredentialResource,
            CredentialUserName,
            JsonSerializer.Serialize(new { token, identity })));
    }

    private static void Restore()
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(CredentialResource, CredentialUserName);
            credential.RetrievePassword();
            using var document = JsonDocument.Parse(credential.Password);
            var root = document.RootElement;
            _publishToken = root.GetProperty("token").GetString();
            Identity = root.GetProperty("identity").Deserialize<AccountIdentity>();
        }
        catch
        {
            _publishToken = null;
            Identity = null;
        }
    }

    private static void RemoveSaved()
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(CredentialResource, CredentialUserName));
        }
        catch { }
    }

    private static Dictionary<string, string> ParseQuery(string? query) =>
        (query ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));
}
