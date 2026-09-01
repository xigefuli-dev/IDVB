using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.UpdateCore;

public sealed record MapSubscriptionUpdateSummary(int Checked, int Prepared, int Failed);

public sealed class MapSubscriptionUpdateEngine
{
    private readonly HttpClient _httpClient;

    public MapSubscriptionUpdateEngine(HttpClient? httpClient = null) =>
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<MapSubscriptionUpdateSummary> UpdateAllAsync(
        string subscriptionRoot,
        string officialPublicKeyPem,
        CancellationToken cancellationToken = default)
    {
        var store = new MapSubscriptionStore(subscriptionRoot);
        var records = store.Load().ToList();
        var checkedCount = 0;
        var preparedCount = 0;
        var failedCount = 0;
        foreach (var record in records.Where(item => item.Enabled))
        {
            checkedCount++;
            try
            {
                if (await PrepareAsync(subscriptionRoot, record, officialPublicKeyPem, cancellationToken))
                    preparedCount++;
                record.LastError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failedCount++;
                record.LastError = exception.Message;
            }
            record.LastCheckedAtUtc = DateTimeOffset.UtcNow;
        }
        store.Save(records);
        return new MapSubscriptionUpdateSummary(checkedCount, preparedCount, failedCount);
    }

    private async Task<bool> PrepareAsync(
        string root,
        MapSubscriptionRecord record,
        string officialPublicKeyPem,
        CancellationToken cancellationToken)
    {
        var link = MapSubscriptionLink.Parse(record.Link);
        if (!string.Equals(link.PublisherKeyId, record.PublisherKeyId, StringComparison.Ordinal))
            throw new CryptographicException("订阅记录中的发布者身份已被修改。");
        var feedBytes = await ReadUriAsync(link.FeedUri, 2 * 1024 * 1024, cancellationToken);
        var envelope = JsonSerializer.Deserialize<SignedMapPublicationEnvelope>(
            feedBytes, MapSubscriptionProtocol.JsonOptions)
            ?? throw new InvalidDataException("地图订阅 feed 为空。");
        var publication = MapSubscriptionCrypto.Verify(
            envelope, link.PublisherKeyId, officialPublicKeyPem);
        record.PublisherHandle = publication.PublisherHandle;
        record.PublisherDisplayName = publication.PublisherDisplayName;
        record.IsOfficialPublisher = publication.IsOfficialPublisher;
        record.IsBuilderPublisher = publication.IsBuilderPublisher;
        record.PackageName = publication.PackageName;
        var communityMetadata = await GetCommunityMetadataAsync(link.FeedUri, publication.PublicationId, cancellationToken);
        record.PublisherDisplayName ??= communityMetadata.PublisherDisplayName;
        record.PackageName ??= communityMetadata.PackageName;
        if (string.Equals(record.LastAppliedVersion, publication.Version, StringComparison.Ordinal))
            return false;
        if (record.LastPublishedAtUtc is { } previous && publication.PublishedAtUtc < previous)
            throw new CryptographicException("订阅 feed 发生版本回退，已拒绝应用。");
        if (string.Equals(
            record.LastAppliedPlaintextSha256,
            publication.PlaintextSha256,
            StringComparison.OrdinalIgnoreCase))
            return false;

        var packageUri = ResolvePackageUri(link.FeedUri, publication.PackageUri);
        var encrypted = await ReadUriAsync(
            packageUri,
            MapSubscriptionProtocol.MaximumEncryptedPackageBytes,
            cancellationToken);
        if (encrypted.LongLength != publication.EncryptedLength
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(encrypted)),
                publication.EncryptedSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("加密 IDVM 的长度或 SHA-256 与签名 feed 不一致。");
        var plaintext = MapSubscriptionCrypto.DecryptIdvm(
            encrypted, link.ContentKey, publication.PublicationId, publication.PlaintextSha256);
        if (plaintext.LongLength != publication.PlaintextLength)
            throw new CryptographicException("解密后的 IDVM 长度与签名 feed 不一致。");

        var pendingDirectory = Path.Combine(root, "pending", record.Id.ToString("N"));
        Directory.CreateDirectory(pendingDirectory);
        var packagePath = Path.Combine(pendingDirectory, publication.PlaintextSha256 + ".idvm");
        var packageTemporary = packagePath + ".tmp";
        await File.WriteAllBytesAsync(packageTemporary, plaintext, cancellationToken);
        File.Move(packageTemporary, packagePath, overwrite: true);
        var receipt = new PendingMapSubscriptionUpdate(
            MapSubscriptionProtocol.SchemaVersion,
            record.Id,
            publication,
            packagePath,
            DateTimeOffset.UtcNow);
        var receiptPath = Path.Combine(pendingDirectory, "pending.json");
        var receiptTemporary = receiptPath + ".tmp";
        await File.WriteAllTextAsync(
            receiptTemporary,
            JsonSerializer.Serialize(receipt, MapSubscriptionProtocol.JsonOptions),
            cancellationToken);
        File.Move(receiptTemporary, receiptPath, overwrite: true);
        return true;
    }

    private async Task<byte[]> ReadUriAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken)
    {
        if (uri.IsFile)
        {
            var info = new FileInfo(uri.LocalPath);
            if (!info.Exists) throw new FileNotFoundException("找不到订阅资源。", uri.LocalPath);
            if (info.Length > maximumBytes) throw new InvalidDataException("订阅资源超过安全大小上限。");
            return await File.ReadAllBytesAsync(uri.LocalPath, cancellationToken);
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("远程订阅资源必须使用 HTTPS。");
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"订阅资源返回 HTTP {(int)response.StatusCode}。");
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("订阅资源超过安全大小上限。");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (target.Length + read > maximumBytes)
                throw new InvalidDataException("订阅资源超过安全大小上限。");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return target.ToArray();
    }

    private static Uri ResolvePackageUri(Uri feedUri, string value)
    {
        if (!Uri.TryCreate(feedUri, value, out var result))
            throw new InvalidDataException("订阅包地址无效。");
        if (result.Scheme is not ("https" or "file"))
            throw new InvalidDataException("订阅包地址协议不受支持。");
        if (feedUri.Scheme == "https"
            && (!string.Equals(result.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(result.Host, feedUri.Host, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("远程订阅包必须与 feed 使用相同的 HTTPS 主机。");
        return result;
    }

    private async Task<(string? PublisherDisplayName, string? PackageName)> GetCommunityMetadataAsync(
        Uri feedUri,
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        var segments = feedUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!string.Equals(feedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || segments.Length != 5
            || !string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[1], "maps", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], "subscriptions", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(segments[3], out var feedPublicationId)
            || feedPublicationId != publicationId
            || !string.Equals(segments[4], "feed.json", StringComparison.OrdinalIgnoreCase))
            return default;

        try
        {
            var catalog = await ReadUriAsync(new Uri(feedUri, "/api/maps"), 2 * 1024 * 1024, cancellationToken);
            using var document = JsonDocument.Parse(catalog);
            if (!document.RootElement.TryGetProperty("publications", out var publications)) return default;
            foreach (var item in publications.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id)
                    || !Guid.TryParse(id.GetString(), out var catalogPublicationId)
                    || catalogPublicationId != publicationId)
                    continue;
                return (
                    item.TryGetProperty("publisherName", out var publisherName) ? publisherName.GetString() : null,
                    item.TryGetProperty("name", out var packageName) ? packageName.GetString() : null);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Public catalog metadata only improves labels; the signed feed remains authoritative.
        }
        return default;
    }
}
