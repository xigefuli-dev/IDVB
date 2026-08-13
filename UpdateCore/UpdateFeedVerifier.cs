using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.UpdateCore;

public interface IUpdateFeedVerifier
{
    VerifiedUpdateFeed Verify(string envelopeJson, string expectedChannel);
}

public sealed class EcdsaUpdateFeedVerifier : IUpdateFeedVerifier
{
    private readonly IReadOnlyDictionary<string, string> _publicKeysById;

    public EcdsaUpdateFeedVerifier(IReadOnlyDictionary<string, string> publicKeysById)
    {
        _publicKeysById = publicKeysById ?? throw new ArgumentNullException(nameof(publicKeysById));
    }

    public VerifiedUpdateFeed Verify(string envelopeJson, string expectedChannel)
    {
        if (!UpdateProtocol.IsKnownChannel(expectedChannel))
            throw new UpdateTrustException($"Unknown update channel '{expectedChannel}'.");

        UpdateFeedEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<UpdateFeedEnvelope>(
                envelopeJson,
                UpdateProtocol.JsonOptions)
                ?? throw new UpdateTrustException("The update envelope is empty.");
        }
        catch (JsonException exception)
        {
            throw new UpdateTrustException("The update envelope is not valid JSON.", exception);
        }

        if (envelope.SchemaVersion != UpdateProtocol.EnvelopeSchemaVersion)
            throw new UpdateTrustException($"Unsupported update envelope schema {envelope.SchemaVersion}.");
        if (!_publicKeysById.TryGetValue(envelope.KeyId, out var publicKeyPem))
            throw new UpdateTrustException($"The update signing key '{envelope.KeyId}' is not trusted.");

        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(envelope.Payload);
            signatureBytes = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException exception)
        {
            throw new UpdateTrustException("The update envelope contains invalid Base64 data.", exception);
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(publicKeyPem);
        }
        catch (CryptographicException exception)
        {
            throw new UpdateTrustException("The configured update public key is invalid.", exception);
        }

        if (!key.VerifyData(
                payloadBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new UpdateTrustException("The update feed signature is invalid.");
        }

        UpdateFeedPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<UpdateFeedPayload>(
                payloadBytes,
                UpdateProtocol.JsonOptions)
                ?? throw new UpdateTrustException("The signed update payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new UpdateTrustException("The signed update payload is not valid JSON.", exception);
        }

        if (payload.SchemaVersion != UpdateProtocol.EnvelopeSchemaVersion)
            throw new UpdateTrustException($"Unsupported signed payload schema {payload.SchemaVersion}.");
        if (!string.Equals(payload.Channel, expectedChannel, StringComparison.Ordinal))
            throw new UpdateTrustException("The signed update channel does not match the requested channel.");
        if (!string.Equals(payload.PackageId, UpdateProtocol.PackageId, StringComparison.Ordinal))
            throw new UpdateTrustException("The signed update package identifier is invalid.");
        if (string.IsNullOrWhiteSpace(payload.FeedJson))
            throw new UpdateTrustException("The signed update feed is missing.");
        string mappedVersion;
        try
        {
            mappedVersion = UpdateVersionMapper.ToVelopackVersion(
                payload.PublicVersion,
                payload.ProductVersion);
        }
        catch (FormatException exception)
        {
            throw new UpdateTrustException("The signed update version is invalid.", exception);
        }
        if (!string.Equals(payload.VelopackVersion, mappedVersion, StringComparison.Ordinal))
            throw new UpdateTrustException("The signed public and Velopack versions do not match.");
        if (string.IsNullOrWhiteSpace(payload.Commit)
            || payload.Commit.Length != 40
            || payload.Commit.Any(character => !Uri.IsHexDigit(character)))
            throw new UpdateTrustException("The signed source commit is invalid.");
        if (payload.Installer is null
            || string.IsNullOrWhiteSpace(payload.Installer.FileName)
            || !string.Equals(
                Path.GetFileName(payload.Installer.FileName),
                payload.Installer.FileName,
                StringComparison.Ordinal)
            || !payload.Installer.FileName.StartsWith("IDVB-Setup-", StringComparison.Ordinal)
            || !payload.Installer.FileName.EndsWith("-x64.exe", StringComparison.OrdinalIgnoreCase)
            || payload.Installer.Size <= 0
            || payload.Installer.Sha256.Length != 64
            || payload.Installer.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new UpdateTrustException("The signed migration installer metadata is invalid.");

        return new VerifiedUpdateFeed(payload, payloadBytes);
    }
}

public sealed class UpdateTrustException : Exception
{
    public UpdateTrustException(string message) : base(message) { }
    public UpdateTrustException(string message, Exception innerException) : base(message, innerException) { }
}
