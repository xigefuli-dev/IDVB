using System.Security.Cryptography;

namespace IdentityVisionBridge.PluginPackaging;

public static class IdvpCrypto
{
    public static (string PrivateKeyPem, string PublicKeyPem, string KeyId) CreatePublisherKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return (key.ExportECPrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem(), ComputeKeyId(publicKey));
    }

    public static string ComputeKeyId(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));

    public static string GetKeyIdFromPrivateKeyPem(string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        EnsureP256(key);
        return ComputeKeyId(key.ExportSubjectPublicKeyInfo());
    }

    internal static IdvpSignature Sign(ReadOnlySpan<byte> manifestBytes, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        EnsureP256(key);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return new IdvpSignature
        {
            Algorithm = IdvpConstants.SignatureAlgorithm,
            KeyId = ComputeKeyId(publicKey),
            PublicKeySpki = Convert.ToBase64String(publicKey),
            Signature = Convert.ToBase64String(key.SignData(manifestBytes, HashAlgorithmName.SHA256))
        };
    }

    internal static void Verify(ReadOnlySpan<byte> manifestBytes, IdvpSignature signature, bool allowUnsigned)
    {
        if (string.Equals(signature.Algorithm, IdvpConstants.UnsignedAlgorithm, StringComparison.Ordinal))
        {
            if (!allowUnsigned)
            {
                throw new IdvpPackageException("Unsigned IDVP packages are rejected outside developer mode.");
            }

            if (signature.KeyId is not null || signature.PublicKeySpki is not null || signature.Signature is not null)
            {
                throw new IdvpPackageException("An unsigned signature record must not contain key material.");
            }

            return;
        }

        if (!string.Equals(signature.Algorithm, IdvpConstants.SignatureAlgorithm, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(signature.KeyId) ||
            string.IsNullOrWhiteSpace(signature.PublicKeySpki) ||
            string.IsNullOrWhiteSpace(signature.Signature))
        {
            throw new IdvpPackageException("The IDVP signature record is incomplete or uses an unsupported algorithm.");
        }

        try
        {
            var publicKey = Convert.FromBase64String(signature.PublicKeySpki);
            var signatureBytes = Convert.FromBase64String(signature.Signature);
            var computedKeyId = ComputeKeyId(publicKey);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(computedKeyId),
                    Convert.FromHexString(signature.KeyId)))
            {
                throw new IdvpPackageException("The publisher key ID does not match the embedded public key.");
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length)
            {
                throw new IdvpPackageException("The publisher public key contains trailing data.");
            }

            EnsureP256(key);
            if (!key.VerifyData(manifestBytes, signatureBytes, HashAlgorithmName.SHA256))
            {
                throw new IdvpPackageException("The IDVP manifest signature is invalid.");
            }
        }
        catch (FormatException exception)
        {
            throw new IdvpPackageException("The IDVP signature contains invalid base64 or hexadecimal data.", exception);
        }
        catch (CryptographicException exception)
        {
            throw new IdvpPackageException("The IDVP publisher key or signature is invalid.", exception);
        }
    }

    private static void EnsureP256(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            throw new IdvpPackageException("IDVP publisher keys must use ECDSA P-256.");
        }
    }
}
