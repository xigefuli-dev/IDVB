using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Security.Credentials;

namespace IDVBuff.Features.Accounts;

internal sealed record VersionAccessResult(bool Allowed, bool LoginRequired, bool UpgradeRequired, string Message);

internal static class VersionAccessClient
{
    // Release switch for both mandatory login and online version eligibility checks.
    // Immutable during a process lifetime: enabling requires rebuilding and restarting.
    internal static bool Enabled { get; } = false;

    private const string ProofResource = "IdentityVisionBridge.VersionAccess";
    internal const string DownloadUrl = "https://download.xgflee.com/";
    private static readonly Lazy<HttpClient> Http = new(() =>
        new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        { Timeout = TimeSpan.FromSeconds(15) });

    internal static string? ReadToken()
    {
        try
        {
            var entry = new PasswordVault().Retrieve("IdentityVisionBridge.Account", "current");
            entry.RetrievePassword();
            using var doc = JsonDocument.Parse(entry.Password);
            return doc.RootElement.GetProperty("token").GetString();
        }
        catch { return null; }
    }

    internal static void ClearProof()
    {
        try { var vault = new PasswordVault(); vault.Remove(vault.Retrieve(ProofResource, "current")); }
        catch { }
    }

    internal static async Task MonitorHeadlessAsync(string productVersion, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                var access = await CheckAsync(productVersion, cancellationToken).ConfigureAwait(false);
                if (!access.Allowed && !cancellationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine(access.Message);
                    Environment.Exit(1);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { Environment.Exit(1); }
    }

    internal static async Task<VersionAccessResult> CheckAsync(string productVersion, CancellationToken cancellationToken = default)
    {
        // Guard before credentials, cached proofs and all network work, including direct CLI calls.
        if (!Enabled) return new(true, false, false, "");

        var token = ReadToken();
        if (string.IsNullOrEmpty(token)) return new(false, true, false, "请先登录 Identity Vision Bridge 账户。");
        var version = productVersion.Split('-')[0];
        try
        {
            var saved = new PasswordVault().Retrieve(ProofResource, "current");
            saved.RetrievePassword();
            var envelope = JsonSerializer.Deserialize<VersionAccessEnvelope>(saved.Password, VersionAccessProof.Json)!;
            VersionAccessProof.Verify(envelope, token, version, null, DateTimeOffset.UtcNow);
            return ReadToken() == token ? new(true, false, false, "") : new(false, true, false, "账户已变更，请重新登录。");
        }
        catch { /* No valid signed offline grant: online verification is mandatory. */ }
        try
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://community.idvb.xgflee.com/api/client/version-access");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(JsonSerializer.Serialize(new { version, nonce }), Encoding.UTF8, "application/json");
            using var response = await Http.Value.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(false, true, false, "登录已失效，请重新登录。");
            response.EnsureSuccessStatusCode();
            var envelope = JsonSerializer.Deserialize<VersionAccessEnvelope>(await response.Content.ReadAsStringAsync(cancellationToken), VersionAccessProof.Json)!;
            var claims = VersionAccessProof.Verify(envelope, token, version, nonce, DateTimeOffset.UtcNow);
            if (ReadToken() != token) return new(false, true, false, "账户已变更，请重新登录。");
            ClearProof();
            if (claims.Allowed && claims.OfflineExempt)
                new PasswordVault().Add(new PasswordCredential(ProofResource, "current", JsonSerializer.Serialize(envelope)));
            return new(claims.Allowed, false, !claims.Allowed, claims.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or
            CryptographicException or JsonException or FormatException or ArgumentException)
        {
            return new(false, false, false, "无法完成联网权限校验。请检查网络和系统时间后重试。");
        }
    }
}
