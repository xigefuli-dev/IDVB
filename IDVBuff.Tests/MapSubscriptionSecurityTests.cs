using IDVBuff.UpdateCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed class MapSubscriptionSecurityTests
{
    [Fact]
    public void LinkRoundTripsFeedKeyAndPublisherPin()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var publisher = new string('A', 64);
        var original = new MapSubscriptionLink(
            new Uri("https://download.example/maps/feed.json"), key, publisher);

        var parsed = MapSubscriptionLink.Parse(original.ToUriString());

        Assert.Equal(original.FeedUri, parsed.FeedUri);
        Assert.Equal(key, parsed.ContentKey);
        Assert.Equal(publisher, parsed.PublisherKeyId);
    }

    [Fact]
    public void AuthenticatedEncryptionRejectsCiphertextTampering()
    {
        var plaintext = RandomNumberGenerator.GetBytes(4096);
        var key = RandomNumberGenerator.GetBytes(32);
        var publicationId = Guid.NewGuid();
        var encrypted = MapSubscriptionCrypto.EncryptIdvm(plaintext, key, publicationId);
        encrypted[^1] ^= 0x40;

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            MapSubscriptionCrypto.DecryptIdvm(
                encrypted,
                key,
                publicationId,
                Convert.ToHexString(SHA256.HashData(plaintext))));
    }

    [Fact]
    public void ReservedOfficialHandleRejectsAValidSignatureFromAnotherKey()
    {
        var official = MapSubscriptionCrypto.CreatePublisherKey();
        var attacker = MapSubscriptionCrypto.CreatePublisherKey();
        var payload = CreatePayload(
            MapSubscriptionProtocol.OfficialPublisherHandle,
            attacker.KeyId);
        var envelope = MapSubscriptionCrypto.Sign(payload, attacker.PrivateKeyPem);

        var error = Assert.Throws<CryptographicException>(() =>
            MapSubscriptionCrypto.Verify(envelope, attacker.KeyId, official.PublicKeyPem));

        Assert.Contains("xigefuli", error.Message);
    }

    [Fact]
    public async Task LocalFeedIsVerifiedDecryptedAndPreparedWithoutChangingSubscriptions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var official = MapSubscriptionCrypto.CreatePublisherKey();
            var publisher = MapSubscriptionCrypto.CreatePublisherKey();
            var publicationId = Guid.NewGuid();
            var plaintext = RandomNumberGenerator.GetBytes(2048);
            var contentKey = RandomNumberGenerator.GetBytes(32);
            var encrypted = MapSubscriptionCrypto.EncryptIdvm(plaintext, contentKey, publicationId);
            var packagePath = Path.Combine(root, "package.idvm.secure");
            await File.WriteAllBytesAsync(packagePath, encrypted);
            var payload = CreatePayload("@mapper", publisher.KeyId) with
            {
                PublicationId = publicationId,
                PackageUri = Path.GetFileName(packagePath),
                EncryptedLength = encrypted.LongLength,
                EncryptedSha256 = Convert.ToHexString(SHA256.HashData(encrypted)),
                PlaintextLength = plaintext.LongLength,
                PlaintextSha256 = Convert.ToHexString(SHA256.HashData(plaintext))
            };
            var feedPath = Path.Combine(root, "feed.json");
            await File.WriteAllTextAsync(
                feedPath,
                JsonSerializer.Serialize(
                    MapSubscriptionCrypto.Sign(payload, publisher.PrivateKeyPem),
                    MapSubscriptionProtocol.JsonOptions));
            var subscriptionRoot = Path.Combine(root, "subscriptions");
            var link = new MapSubscriptionLink(new Uri(feedPath), contentKey, publisher.KeyId);
            new MapSubscriptionStore(subscriptionRoot).Save([
                new MapSubscriptionRecord
                {
                    Link = link.ToUriString(),
                    FeedUri = link.FeedUri.AbsoluteUri,
                    PublisherKeyId = publisher.KeyId
                }
            ]);

            var result = await new MapSubscriptionUpdateEngine().UpdateAllAsync(
                subscriptionRoot, official.PublicKeyPem);

            Assert.Equal(1, result.Checked);
            Assert.Equal(1, result.Prepared);
            var receiptPath = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(subscriptionRoot, "pending"), "pending.json", SearchOption.AllDirectories));
            var receipt = JsonSerializer.Deserialize<PendingMapSubscriptionUpdate>(
                await File.ReadAllTextAsync(receiptPath), MapSubscriptionProtocol.JsonOptions)!;
            Assert.Equal(plaintext, await File.ReadAllBytesAsync(receipt.PackagePath));
            Assert.Null(Assert.Single(new MapSubscriptionStore(subscriptionRoot).Load())
                .LastAppliedPlaintextSha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SameVersionIsUpToDateAndPersistsPublicSubscriptionDetails()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var official = MapSubscriptionCrypto.CreatePublisherKey();
            var publisher = MapSubscriptionCrypto.CreatePublisherKey();
            var contentKey = RandomNumberGenerator.GetBytes(32);
            var payload = CreatePayload("@mapper", publisher.KeyId) with
            {
                PublisherDisplayName = "地图作者",
                PackageName = "湖景村地图包"
            };
            var feedPath = Path.Combine(root, "feed.json");
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(
                MapSubscriptionCrypto.Sign(payload, publisher.PrivateKeyPem),
                MapSubscriptionProtocol.JsonOptions));
            var subscriptionRoot = Path.Combine(root, "subscriptions");
            var link = new MapSubscriptionLink(new Uri(feedPath), contentKey, publisher.KeyId);
            new MapSubscriptionStore(subscriptionRoot).Save([new MapSubscriptionRecord
            {
                Link = link.ToUriString(), FeedUri = link.FeedUri.AbsoluteUri,
                PublisherKeyId = publisher.KeyId, LastAppliedVersion = payload.Version,
                LastPublishedAtUtc = payload.PublishedAtUtc.AddMinutes(1)
            }]);

            var result = await new MapSubscriptionUpdateEngine().UpdateAllAsync(subscriptionRoot, official.PublicKeyPem);
            var record = Assert.Single(new MapSubscriptionStore(subscriptionRoot).Load());

            Assert.Equal(0, result.Failed);
            Assert.Equal(0, result.Prepared);
            Assert.Equal("地图作者", record.PublisherDisplayName);
            Assert.Equal("湖景村地图包", record.PackageName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static MapPublicationPayload CreatePayload(string handle, string keyId) => new(
        MapSubscriptionProtocol.SchemaVersion,
        Guid.NewGuid(),
        handle,
        keyId,
        "20260831120000-ABCDEF012345",
        DateTimeOffset.UtcNow,
        "all-classes",
        false,
        "package.idvm.secure",
        1,
        new string('A', 64),
        1,
        new string('B', 64));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "idvb-map-sub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
