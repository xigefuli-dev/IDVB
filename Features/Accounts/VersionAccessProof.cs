using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IDVBuff.Features.Accounts;

internal sealed record VersionAccessEnvelope(string Payload, string Signature);
internal sealed record VersionAccessClaims(string Audience, string Version, string Nonce, string TokenHash,
    bool Allowed, bool OfflineExempt, long IssuedAt, long ExpiresAt, string Message, string DownloadUrl);

internal static class VersionAccessProof
{
    internal const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEcpBrflF6R5HFD7rm7B4iSa3xQaLw
        AQrIqHhSsvLGRUNwLpcrynOnVTYZkKV0yPbIrxKheSdCahrGg+n1hku0og==
        -----END PUBLIC KEY-----
        """;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal static string HashToken(string token) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static VersionAccessClaims Verify(VersionAccessEnvelope envelope, string token, string version,
        string? nonce, DateTimeOffset now, string publicKey = PublicKey)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKey);
        var payload = Convert.FromBase64String(envelope.Payload);
        if (!key.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("版本权限签名无效。");
        var claims = JsonSerializer.Deserialize<VersionAccessClaims>(payload, Json)
            ?? throw new CryptographicException("版本权限凭据为空。");
        if (claims.Audience != "idvb-desktop-access-v1" || claims.TokenHash != HashToken(token))
            throw new CryptographicException("版本权限凭据与登录账户不匹配。");
        if (nonce is null)
        {
            // A signed privilege grant is intentionally valid offline across versions.
            // Local display-name / role metadata is never an authorization source.
            if (!claims.Allowed || !claims.OfflineExempt)
                throw new CryptographicException("该账户必须联网校验。");
        }
        else if (claims.Nonce != nonce || claims.Version != version || claims.ExpiresAt <= now.ToUnixTimeSeconds()
            || claims.IssuedAt > now.ToUnixTimeSeconds() + 60 || claims.ExpiresAt - claims.IssuedAt != 120)
            throw new CryptographicException("版本权限响应已失效，请校准系统时间后重试。");
        return claims;
    }
}
