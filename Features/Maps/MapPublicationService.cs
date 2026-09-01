using IDVBuff.UpdateCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed record MapPublicationResult(
    Guid PublicationId,
    string Version,
    string PublisherHandle,
    string PublisherDisplayName,
    bool IsOfficialPublisher,
    bool IsBuilderPublisher,
    string OutputDirectory,
    string SubscriptionLink,
    string PackageName,
    string CoverPath,
    string PackagePath);

public sealed class MapPublicationService
{
    private readonly IdvmPackageService _packages;
    private readonly MapPublisherCredentialStore _credentials;

    public MapPublicationService(
        MapRepository repository,
        string? dataRoot = null)
    {
        _packages = new IdvmPackageService(repository);
        _credentials = new MapPublisherCredentialStore(dataRoot);
    }

    public async Task<MapPublicationResult> PublishAsync(
        IdvmExportScope scope,
        string? className,
        string outputParentDirectory,
        string publisherHandle,
        string publisherDisplayName,
        bool isOfficialPublisher,
        bool isBuilderPublisher,
        bool intendedForOfficialWebsite,
        string packageName,
        string coverPath,
        Guid? publicationId = null,
        byte[]? existingContentKey = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _packages.GetExportMapsAsync(scope, className, cancellationToken);
        if (snapshot.Any(map => map.AcquisitionKind == MapAcquisitionKind.Subscription
            && (publicationId is null || map.SubscriptionId != publicationId)))
            throw new InvalidOperationException("只有发布者自己的已发布地图类可以更新。");

        var handle = MapSubscriptionProtocol.NormalizePublisherHandle(publisherHandle);
        var officialPublicKeyPem = MapSubscriptionTrust.LoadOfficialPublicKey(AppContext.BaseDirectory);
        var credential = _credentials.GetOrCreate(handle, officialPublicKeyPem);
        var isOfficial = string.Equals(
            credential.KeyId,
            MapSubscriptionCrypto.ComputeKeyId(officialPublicKeyPem),
            StringComparison.Ordinal);
        if (!isOfficial && string.Equals(
            handle,
            MapSubscriptionProtocol.OfficialPublisherHandle,
            StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("xigefuli 官方身份验证失败。");

        publicationId ??= Guid.NewGuid();
        var staging = Path.Combine(
            Path.GetTempPath(),
            "IDVB",
            "map-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var plaintextPath = Path.Combine(staging, "payload.idvm");
        try
        {
            await _packages.ExportAsync(scope, className, plaintextPath, cancellationToken);
            var plaintext = await File.ReadAllBytesAsync(plaintextPath, cancellationToken);
            var plainHash = Convert.ToHexString(SHA256.HashData(plaintext));
            var contentKey = existingContentKey ?? RandomNumberGenerator.GetBytes(32);
            if (contentKey.Length != 32) throw new CryptographicException("订阅内容密钥无效。");
            var encrypted = MapSubscriptionCrypto.EncryptIdvm(plaintext, contentKey, publicationId.Value);
            var encryptedHash = Convert.ToHexString(SHA256.HashData(encrypted));
            var publishedAt = DateTimeOffset.UtcNow;
            var version = $"{publishedAt:yyyyMMddHHmmss}-{plainHash[..12]}";
            var output = Path.Combine(outputParentDirectory, $"IDVB-Map-{publicationId:N}");
            Directory.CreateDirectory(output);
            var securePackageName = $"maps-{version}.idvm.secure";
            var packagePath = Path.Combine(output, securePackageName);
            var packageTemporary = packagePath + ".tmp";
            await File.WriteAllBytesAsync(packageTemporary, encrypted, cancellationToken);
            File.Move(packageTemporary, packagePath, overwrite: true);

            var payload = new MapPublicationPayload(
                MapSubscriptionProtocol.SchemaVersion,
                publicationId.Value,
                handle,
                credential.KeyId,
                version,
                publishedAt,
                scope == IdvmExportScope.CurrentClass ? "current-class" : "all-classes",
                intendedForOfficialWebsite,
                securePackageName,
                encrypted.LongLength,
                encryptedHash,
                plaintext.LongLength,
                plainHash,
                publisherDisplayName.Trim(),
                isOfficialPublisher,
                isBuilderPublisher,
                packageName.Trim());
            var envelope = MapSubscriptionCrypto.Sign(payload, credential.PrivateKeyPem);
            var feedPath = Path.Combine(output, "feed.json");
            var feedTemporary = feedPath + ".tmp";
            await File.WriteAllTextAsync(
                feedTemporary,
                JsonSerializer.Serialize(envelope, MapSubscriptionProtocol.JsonOptions),
                cancellationToken);
            File.Move(feedTemporary, feedPath, overwrite: true);
            var link = new MapSubscriptionLink(
                new Uri(feedPath), contentKey, credential.KeyId).ToUriString();
            return new MapPublicationResult(
                publicationId.Value, version, handle, publisherDisplayName.Trim(), isOfficialPublisher, isBuilderPublisher, output, link,
                packageName.Trim(), coverPath, packagePath);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { }
        }
    }
}
