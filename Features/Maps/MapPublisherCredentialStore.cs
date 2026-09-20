using IDVBuff.UpdateCore;
using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;

internal sealed class MapPublisherCredentialStore
{
    private readonly string _keyDirectory;

    public MapPublisherCredentialStore(string? rootDirectory = null) =>
        _keyDirectory = Path.Combine(
            rootDirectory ?? global::IDVBuff.AppDataPaths.RootDirectory,
            "MapPublishing",
            "keys");

    public (string PrivateKeyPem, string PublicKeyPem, string KeyId) GetOrCreate(
        string publisherHandle,
        string officialPublicKeyPem)
    {
        var handle = MapSubscriptionProtocol.NormalizePublisherHandle(publisherHandle);
        if (string.Equals(
            handle,
            MapSubscriptionProtocol.OfficialPublisherHandle,
            StringComparison.OrdinalIgnoreCase))
            return LoadOfficialCredential(officialPublicKeyPem);

        Directory.CreateDirectory(_keyDirectory);
        var path = Path.Combine(_keyDirectory, Sanitize(handle) + ".pem");
        string privatePem;
        if (File.Exists(path))
        {
            privatePem = File.ReadAllText(path);
        }
        else
        {
            var created = MapSubscriptionCrypto.CreatePublisherKey();
            privatePem = created.PrivateKeyPem;
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, privatePem);
            File.Move(temporary, path, overwrite: false);
        }
        return Describe(privatePem);
    }

    private static (string PrivateKeyPem, string PublicKeyPem, string KeyId) LoadOfficialCredential(
        string officialPublicKeyPem)
    {
        var configured = Environment.GetEnvironmentVariable("IDVB_MAP_OFFICIAL_PRIVATE_KEY_PATH");
        var candidates = new[]
        {
            configured,
            Path.Combine(Directory.GetCurrentDirectory(), ".secrets", "idvb-update-2026-01-private.pem")
        };
        var path = candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
        if (path is null)
            throw new CryptographicException(
                "未获得官方地图发布权限，暂无法以官方身份发布地图。请联系管理员完成授权后再试。");
        var credential = Describe(File.ReadAllText(path));
        if (!string.Equals(
            credential.KeyId,
            MapSubscriptionCrypto.ComputeKeyId(officialPublicKeyPem),
            StringComparison.Ordinal))
            throw new CryptographicException("所选私钥不是 IDVB 内置的 xigefuli 官方发布密钥。");
        return credential;
    }

    private static (string PrivateKeyPem, string PublicKeyPem, string KeyId) Describe(string privatePem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return (privatePem, key.ExportSubjectPublicKeyInfoPem(), MapSubscriptionCrypto.ComputeKeyId(key));
    }

    private static string Sanitize(string value) => new(
        value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
}
