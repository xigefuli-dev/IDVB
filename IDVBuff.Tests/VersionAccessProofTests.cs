using System.Security.Cryptography;
using System.Text.Json;
using IDVBuff.Features.Accounts;
using Xunit;

namespace IDVBuff.Tests;

public sealed class VersionAccessProofTests
{
    private const string Token = "authenticated-account-token";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private static VersionAccessClaims Claims(bool privileged = false) => new("idvb-desktop-access-v1", "1.6.6", "nonce",
        VersionAccessProof.HashToken(Token), true, privileged, Now.ToUnixTimeSeconds(), Now.ToUnixTimeSeconds() + 120, "", "https://download.xgflee.com/");

    private static VersionAccessEnvelope Sign(ECDsa key, VersionAccessClaims claims)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(claims);
        return new(Convert.ToBase64String(bytes), Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    [Fact]
    public void OnlineProofRequiresSignatureAccountNonceVersionAndFreshness()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var proof = Sign(key, Claims());
        var pem = key.ExportSubjectPublicKeyInfoPem();
        Assert.True(VersionAccessProof.Verify(proof, Token, "1.6.6", "nonce", Now, pem).Allowed);
        Assert.Throws<CryptographicException>(() => VersionAccessProof.Verify(proof, "other-token", "1.6.6", "nonce", Now, pem));
        Assert.Throws<CryptographicException>(() => VersionAccessProof.Verify(proof, Token, "1.6.7", "nonce", Now, pem));
        Assert.Throws<CryptographicException>(() => VersionAccessProof.Verify(proof, Token, "1.6.6", "previous-request", Now, pem));
        Assert.Throws<CryptographicException>(() => VersionAccessProof.Verify(proof, Token, "1.6.6", "nonce", Now.AddSeconds(120), pem));
        Assert.Throws<CryptographicException>(() => VersionAccessProof.Verify(proof, Token, "1.6.6", "nonce", Now.AddMinutes(-2), pem));
        var tampered = proof with { Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Claims(true))) };
        Assert.Throws<CryptographicException>(() => VersionAccessProof.Verify(tampered, Token, "1.6.6", null, Now, pem));
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => VersionAccessProof.Verify(proof, Token, "1.6.6", "nonce", Now, wrongKey.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void OnlySignedPrivilegeAllowsOfflineAcrossVersionsAndStillBindsTheAccount()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        Assert.Throws<CryptographicException>(() => VersionAccessProof.Verify(Sign(key, Claims()), Token, "1.6.6", null, Now, pem));
        Assert.True(VersionAccessProof.Verify(Sign(key, Claims(true)), Token, "2.0.0", null, Now.AddYears(2), pem).Allowed);
        Assert.Throws<CryptographicException>(() => VersionAccessProof.Verify(Sign(key, Claims(true)), "new-account", "1.6.6", null, Now, pem));
        Assert.Throws<CryptographicException>(() => VersionAccessProof.Verify(Sign(key, Claims(true) with { Allowed = false }), Token, "1.6.6", null, Now, pem));
    }
}
