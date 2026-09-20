using System.Security.Cryptography;
using System.Text.Json;
using IDVBuff.UpdateCore;

namespace IDVBuff.Tests;

public sealed class UpdateFrameworkTests
{
    [Fact]
    public void SignedEnvelopeRoundTripsWithPinnedEcdsaKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (verifier, envelope, payloadBytes) = CreateEnvelope(key, UpdateProtocol.TestChannel);

        var verified = verifier.Verify(envelope, UpdateProtocol.TestChannel);

        Assert.Equal("b01.4-26.08.12.0001", verified.Payload.PublicVersion);
        Assert.True(verified.Payload.MigrationBaseline);
        Assert.Equal("1.4.1-build.20260812.1", verified.Payload.MinimumVersion);
        Assert.Equal(payloadBytes, verified.CanonicalPayload);
    }

    [Fact]
    public void SignedEnvelopeRejectsPayloadTampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (verifier, envelopeJson, _) = CreateEnvelope(key, UpdateProtocol.TestChannel);
        var envelope = JsonSerializer.Deserialize<UpdateFeedEnvelope>(
            envelopeJson,
            UpdateProtocol.JsonOptions)!;
        var tampered = Convert.FromBase64String(envelope.Payload);
        tampered[^2] ^= 0x01;
        var changed = envelope with { Payload = Convert.ToBase64String(tampered) };

        var error = Assert.Throws<UpdateTrustException>(() => verifier.Verify(
            JsonSerializer.Serialize(changed, UpdateProtocol.JsonOptions),
            UpdateProtocol.TestChannel));

        Assert.Contains("signature", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignedEnvelopeCannotCrossChannels()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (verifier, envelope, _) = CreateEnvelope(key, UpdateProtocol.TestChannel);

        var error = Assert.Throws<UpdateTrustException>(() =>
            verifier.Verify(envelope, UpdateProtocol.StableChannel));

        Assert.Contains("channel", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownEnvelopeKeyIsRejectedBeforeFeedParsing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (_, envelope, _) = CreateEnvelope(key, UpdateProtocol.TestChannel);
        var verifier = new EcdsaUpdateFeedVerifier(new Dictionary<string, string>());

        var error = Assert.Throws<UpdateTrustException>(() =>
            verifier.Verify(envelope, UpdateProtocol.TestChannel));

        Assert.Contains("not trusted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("b01.4-26.08.12.0001", "1.4.1", "1.4.1-build.20260812.1")]
    [InlineData("b01.4-26.12.31.0042", "1.4.9", "1.4.9-build.20261231.42")]
    [InlineData("b01.5-26.08.21.0042", "1.5.0-preview", "1.5.0-preview-build.20260821.42")]
    public void PublicVersionMapsToDeterministicVelopackVersion(
        string publicVersion,
        string productVersion,
        string expected)
    {
        Assert.Equal(expected, UpdateVersionMapper.ToVelopackVersion(publicVersion, productVersion));
    }

    [Fact]
    public void ChannelUriRejectsUnknownChannel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UpdateProtocol.GetChannelUri(
            new Uri(UpdateProtocol.DefaultUpdateRoot),
            "win-x64-preview"));
    }

    private static (EcdsaUpdateFeedVerifier Verifier, string Envelope, byte[] PayloadBytes)
        CreateEnvelope(ECDsa key, string channel)
    {
        const string keyId = "test-key";
        var payload = new UpdateFeedPayload(
            UpdateProtocol.EnvelopeSchemaVersion,
            channel,
            UpdateProtocol.PackageId,
            "b01.4-26.08.12.0001",
            "1.4.1",
            "1.4.1-build.20260812.1",
            "1.4.1-build.20260812.1",
            true,
            DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
            new string('a', 40),
            "Test release",
            "{\"assets\":[]}",
            new UpdateInstallerMetadata("IDVB-Setup-b01.4-26.08.12.0001-x64.exe", new string('b', 64), 100));
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, UpdateProtocol.JsonOptions);
        var signature = key.SignData(
            payloadBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var envelope = new UpdateFeedEnvelope(
            UpdateProtocol.EnvelopeSchemaVersion,
            keyId,
            Convert.ToBase64String(payloadBytes),
            Convert.ToBase64String(signature));
        var verifier = new EcdsaUpdateFeedVerifier(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [keyId] = key.ExportSubjectPublicKeyInfoPem()
            });
        return (
            verifier,
            JsonSerializer.Serialize(envelope, UpdateProtocol.JsonOptions),
            payloadBytes);
    }
}
