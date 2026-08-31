using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IDVBuff.UpdateCore;

public static class MapSubscriptionCrypto
{
    private static readonly byte[] Magic = "IDVME2\0\0"u8.ToArray();
    private const int HeaderLength = 8 + 16 + 12 + 16 + 8 + 32;

    public static (string PrivateKeyPem, string PublicKeyPem, string KeyId) CreatePublisherKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem(), ComputeKeyId(key));
    }

    public static string ComputeKeyId(ECDsa key) =>
        Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    public static string ComputeKeyId(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return ComputeKeyId(key);
    }

    public static SignedMapPublicationEnvelope Sign(
        MapPublicationPayload payload,
        string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var publicPem = key.ExportSubjectPublicKeyInfoPem();
        var keyId = ComputeKeyId(key);
        if (!string.Equals(payload.PublisherKeyId, keyId, StringComparison.Ordinal))
            throw new CryptographicException("发布者公钥指纹与签名私钥不匹配。");
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, MapSubscriptionProtocol.JsonOptions);
        return new SignedMapPublicationEnvelope(
            MapSubscriptionProtocol.SchemaVersion,
            Convert.ToBase64String(payloadBytes),
            Convert.ToBase64String(key.SignData(payloadBytes, HashAlgorithmName.SHA256)),
            publicPem);
    }

    public static MapPublicationPayload Verify(
        SignedMapPublicationEnvelope envelope,
        string pinnedPublisherKeyId,
        string officialPublicKeyPem)
    {
        if (envelope.SchemaVersion != MapSubscriptionProtocol.SchemaVersion)
            throw new CryptographicException("不支持的地图订阅签名版本。");
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("地图订阅签名编码无效。", exception);
        }
        using var suppliedKey = ECDsa.Create();
        suppliedKey.ImportFromPem(envelope.PublisherPublicKeyPem);
        var suppliedKeyId = ComputeKeyId(suppliedKey);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(suppliedKeyId),
                Encoding.ASCII.GetBytes(pinnedPublisherKeyId.ToUpperInvariant())))
            throw new CryptographicException("发布者公钥与订阅链接中固定的身份不一致。");
        if (!suppliedKey.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
            throw new CryptographicException("地图订阅 feed 签名无效。");

        var payload = JsonSerializer.Deserialize<MapPublicationPayload>(
            payloadBytes, MapSubscriptionProtocol.JsonOptions)
            ?? throw new CryptographicException("地图订阅 feed 内容为空。");
        ValidatePayload(payload, suppliedKeyId, officialPublicKeyPem);
        return payload;
    }

    public static byte[] EncryptIdvm(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> contentKey,
        Guid publicationId)
    {
        if (contentKey.Length != 32)
            throw new CryptographicException("IDVM 内容密钥必须为 256 位。");
        var plainHash = SHA256.HashData(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(contentKey, tag.Length))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildAssociatedData(publicationId, plainHash));
        var result = new byte[HeaderLength + ciphertext.Length];
        Magic.CopyTo(result, 0);
        publicationId.TryWriteBytes(result.AsSpan(8, 16));
        nonce.CopyTo(result, 24);
        tag.CopyTo(result, 36);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(52, 8), plaintext.Length);
        plainHash.CopyTo(result, 60);
        ciphertext.CopyTo(result, HeaderLength);
        return result;
    }

    public static byte[] DecryptIdvm(
        ReadOnlySpan<byte> encrypted,
        ReadOnlySpan<byte> contentKey,
        Guid expectedPublicationId,
        string expectedPlaintextSha256)
    {
        if (contentKey.Length != 32 || encrypted.Length < HeaderLength
            || !encrypted[..8].SequenceEqual(Magic))
            throw new CryptographicException("加密 IDVM 容器头无效。");
        var publicationId = new Guid(encrypted.Slice(8, 16));
        var length = BinaryPrimitives.ReadInt64LittleEndian(encrypted.Slice(52, 8));
        var plainHash = encrypted.Slice(60, 32).ToArray();
        if (publicationId != expectedPublicationId || length < 0
            || length != encrypted.Length - HeaderLength
            || !string.Equals(Convert.ToHexString(plainHash), expectedPlaintextSha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("加密 IDVM 元数据与签名 feed 不一致。");
        if (length > int.MaxValue)
            throw new CryptographicException("加密 IDVM 超过当前客户端可处理的大小。");
        var plaintext = new byte[checked((int)length)];
        using (var aes = new AesGcm(contentKey, 16))
            aes.Decrypt(
                encrypted.Slice(24, 12),
                encrypted[HeaderLength..],
                encrypted.Slice(36, 16),
                plaintext,
                BuildAssociatedData(publicationId, plainHash));
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(plaintext), plainHash))
            throw new CryptographicException("解密后的 IDVM 摘要无效。");
        return plaintext;
    }

    private static void ValidatePayload(
        MapPublicationPayload payload,
        string suppliedKeyId,
        string officialPublicKeyPem)
    {
        if (payload.SchemaVersion != MapSubscriptionProtocol.SchemaVersion
            || payload.PublicationId == Guid.Empty
            || string.IsNullOrWhiteSpace(payload.Version)
            || string.IsNullOrWhiteSpace(payload.PackageUri)
            || payload.EncryptedLength <= 0
            || payload.EncryptedLength > MapSubscriptionProtocol.MaximumEncryptedPackageBytes
            || payload.PlaintextLength <= 0
            || payload.EncryptedSha256.Length != 64
            || payload.PlaintextSha256.Length != 64
            || !string.Equals(payload.PublisherKeyId, suppliedKeyId, StringComparison.Ordinal))
            throw new CryptographicException("地图订阅 feed 字段无效。");
        var handle = MapSubscriptionProtocol.NormalizePublisherHandle(payload.PublisherHandle);
        if (!string.Equals(handle, payload.PublisherHandle, StringComparison.Ordinal))
            throw new CryptographicException("发布者账号不是规范格式。");
        if (!string.Equals(handle, MapSubscriptionProtocol.OfficialPublisherHandle, StringComparison.OrdinalIgnoreCase))
            return;
        var officialKeyId = ComputeKeyId(officialPublicKeyPem);
        if (!string.Equals(suppliedKeyId, officialKeyId, StringComparison.Ordinal))
            throw new CryptographicException("保留账号 xigefuli 只能由 IDVB 内置官方密钥签名。");
    }

    private static byte[] BuildAssociatedData(Guid publicationId, ReadOnlySpan<byte> hash) =>
        Encoding.UTF8.GetBytes($"IDVB-IDVM-SECURE-2\n{publicationId:D}\n{Convert.ToHexString(hash)}");
}
