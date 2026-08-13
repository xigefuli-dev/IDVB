using System.Net.Http;
using System.Security.Cryptography;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace IDVBuff.UpdateCore;

/// <summary>
/// Velopack source backed by an IDVB ECDSA-signed envelope. The feed is never
/// handed to Velopack until its signature, channel, package id, and asset paths
/// have been validated against the embedded trust root.
/// </summary>
public sealed class SignedWebUpdateSource : IUpdateSource, IDisposable
{
    private readonly Uri _channelUri;
    private readonly string _expectedChannel;
    private readonly IUpdateFeedVerifier _verifier;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public SignedWebUpdateSource(
        Uri updateRoot,
        string expectedChannel,
        IUpdateFeedVerifier verifier,
        HttpClient? httpClient = null)
    {
        _channelUri = UpdateProtocol.GetChannelUri(updateRoot, expectedChannel);
        _expectedChannel = expectedChannel;
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _ownsClient = httpClient is null;
    }

    public UpdateFeedPayload? LastVerifiedPayload { get; private set; }

    public async Task<VelopackAssetFeed> FetchVerifiedFeedAsync(
        CancellationToken cancellationToken = default)
    {
        var envelopeJson = await _httpClient.GetStringAsync(
            new Uri(_channelUri, "feed-envelope.json"),
            cancellationToken).ConfigureAwait(false);
        var verified = _verifier.Verify(envelopeJson, _expectedChannel);
        var feed = VelopackAssetFeed.FromJson(verified.Payload.FeedJson);
        ValidateAssets(feed.Assets, verified.Payload.VelopackVersion);
        LastVerifiedPayload = verified.Payload;
        return feed;
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        if (!string.Equals(channel, _expectedChannel, StringComparison.Ordinal))
            throw new UpdateTrustException("Velopack requested an unexpected update channel.");

        var feed = await FetchVerifiedFeedAsync().ConfigureAwait(false);
        logger.Info($"Verified IDVB update feed for channel '{_expectedChannel}'.");
        return feed;
    }

    public async Task DownloadReleaseEntry(
        IVelopackLogger logger,
        VelopackAsset releaseEntry,
        string localFile,
        Action<int> progress,
        CancellationToken cancelToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseEntry);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFile);
        ValidateAssets([releaseEntry], LastVerifiedPayload?.VelopackVersion);

        using var response = await _httpClient.GetAsync(
            new Uri(_channelUri, releaseEntry.FileName),
            HttpCompletionOption.ResponseHeadersRead,
            cancelToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var expectedLength = response.Content.Headers.ContentLength ?? releaseEntry.Size;
        if (expectedLength != releaseEntry.Size)
            throw new UpdateTrustException("The downloaded asset length does not match the signed feed.");
        await using var input = await response.Content.ReadAsStreamAsync(cancelToken).ConfigureAwait(false);
        await using var output = new FileStream(
            localFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous);
        var buffer = new byte[81920];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancelToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancelToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            written += read;
            if (expectedLength > 0)
                progress((int)Math.Clamp(written * 100L / expectedLength, 0, 100));
        }
        if (written != releaseEntry.Size)
            throw new UpdateTrustException("The downloaded asset is incomplete.");
        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actualHash, releaseEntry.SHA256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateTrustException("The downloaded asset failed SHA-256 verification.");
        progress(100);
        logger.Info($"Downloaded verified-feed asset '{releaseEntry.FileName}'.");
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }

    private static void ValidateAssets(
        IEnumerable<VelopackAsset> assets,
        string? expectedVersion)
    {
        foreach (var asset in assets)
        {
            if (!string.Equals(asset.PackageId, UpdateProtocol.PackageId, StringComparison.Ordinal))
                throw new UpdateTrustException("The update feed contains another package identifier.");
            if (string.IsNullOrWhiteSpace(asset.FileName)
                || !string.Equals(Path.GetFileName(asset.FileName), asset.FileName, StringComparison.Ordinal)
                || !asset.FileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateTrustException("The update feed contains an unsafe asset path.");
            }
            if (asset.Size <= 0 || string.IsNullOrWhiteSpace(asset.SHA256))
                throw new UpdateTrustException("The update feed contains incomplete asset integrity metadata.");
            if (!string.IsNullOrWhiteSpace(expectedVersion)
                && !string.Equals(asset.Version.ToString(), expectedVersion, StringComparison.Ordinal))
                throw new UpdateTrustException("The update feed asset version does not match the signed payload.");
        }
    }
}
