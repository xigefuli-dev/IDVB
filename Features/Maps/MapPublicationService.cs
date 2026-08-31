using IDVBuff.UpdateCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed record MapPublicationResult(
    Guid PublicationId,
    string Version,
    string PublisherHandle,
    bool IsOfficialPublisher,
    string OutputDirectory,
    string SubscriptionLink);

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
        bool intendedForOfficialWebsite,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _packages.GetExportMapsAsync(scope, className, cancellationToken);
        if (snapshot.Any(map => map.AcquisitionKind == MapAcquisitionKind.Subscription))
            throw new InvalidOperationException("订阅获得的地图不能再次发布。");

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

        var publicationId = Guid.NewGuid();
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
            var contentKey = RandomNumberGenerator.GetBytes(32);
            var encrypted = MapSubscriptionCrypto.EncryptIdvm(plaintext, contentKey, publicationId);
            var encryptedHash = Convert.ToHexString(SHA256.HashData(encrypted));
            var publishedAt = DateTimeOffset.UtcNow;
            var version = $"{publishedAt:yyyyMMddHHmmss}-{plainHash[..12]}";
            var output = Path.Combine(outputParentDirectory, $"IDVB-Map-{publicationId:N}");
            Directory.CreateDirectory(output);
            var packageName = $"maps-{version}.idvm.secure";
            var packagePath = Path.Combine(output, packageName);
            var packageTemporary = packagePath + ".tmp";
            await File.WriteAllBytesAsync(packageTemporary, encrypted, cancellationToken);
            File.Move(packageTemporary, packagePath, overwrite: false);

            var payload = new MapPublicationPayload(
                MapSubscriptionProtocol.SchemaVersion,
                publicationId,
                handle,
                credential.KeyId,
                version,
                publishedAt,
                scope == IdvmExportScope.CurrentClass ? "current-class" : "all-classes",
                intendedForOfficialWebsite,
                packageName,
                encrypted.LongLength,
                encryptedHash,
                plaintext.LongLength,
                plainHash);
            var envelope = MapSubscriptionCrypto.Sign(payload, credential.PrivateKeyPem);
            var feedPath = Path.Combine(output, "feed.json");
            var feedTemporary = feedPath + ".tmp";
            await File.WriteAllTextAsync(
                feedTemporary,
                JsonSerializer.Serialize(envelope, MapSubscriptionProtocol.JsonOptions),
                cancellationToken);
            File.Move(feedTemporary, feedPath, overwrite: false);
            var link = new MapSubscriptionLink(
                new Uri(feedPath), contentKey, credential.KeyId).ToUriString();
            return new MapPublicationResult(
                publicationId, version, handle, isOfficial, output, link);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { }
        }
    }
}
