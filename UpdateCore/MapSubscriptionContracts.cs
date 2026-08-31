using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.UpdateCore;

public static class MapSubscriptionProtocol
{
    public const int SchemaVersion = 1;
    public const string OfficialPublisherHandle = "@xigefuli";
    public const string OfficialTrustFileName = "idvb-update-2026-01.pem";
    public const long MaximumEncryptedPackageBytes = 384L * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string NormalizePublisherHandle(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!normalized.StartsWith('@'))
            normalized = "@" + normalized;
        if (normalized.Length is < 2 or > 65
            || normalized.Skip(1).Any(character =>
                !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.')))
            throw new ArgumentException("发布者账号必须以 @ 开头，且只能包含字母、数字、点、短横线或下划线。");
        return normalized;
    }
}

public sealed record MapSubscriptionLink(
    Uri FeedUri,
    byte[] ContentKey,
    string PublisherKeyId)
{
    public static MapSubscriptionLink Parse(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
            throw new FormatException("更新订阅链接无效。");
        if (string.Equals(uri.Scheme, "idvb-sub", StringComparison.OrdinalIgnoreCase))
            return ParseIdvbLink(uri);
        if (uri.Scheme is "https" or "file")
            return ParseDirectLink(uri);
        throw new FormatException("更新订阅链接只支持 idvb-sub、https 或 file 协议。");
    }

    public string ToUriString()
    {
        var feed = Uri.EscapeDataString(FeedUri.AbsoluteUri);
        var key = Base64Url.Encode(ContentKey);
        var publisher = Uri.EscapeDataString(PublisherKeyId);
        return $"idvb-sub://v1?feed={feed}&key={key}&publisher={publisher}";
    }

    private static MapSubscriptionLink ParseIdvbLink(Uri uri)
    {
        if (!string.Equals(uri.Host, "v1", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("不支持的更新订阅链接版本。");
        var values = ParseParameters(uri.Query.TrimStart('?'));
        if (!values.TryGetValue("feed", out var feedText)
            || !Uri.TryCreate(feedText, UriKind.Absolute, out var feed))
            throw new FormatException("更新订阅链接缺少有效 feed 地址。");
        return CreateValidated(feed, values.GetValueOrDefault("key"), values.GetValueOrDefault("publisher"));
    }

    private static MapSubscriptionLink ParseDirectLink(Uri uri)
    {
        var values = ParseParameters(uri.Fragment.TrimStart('#'));
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return CreateValidated(
            builder.Uri,
            values.GetValueOrDefault("idvb-key"),
            values.GetValueOrDefault("idvb-publisher"));
    }

    private static MapSubscriptionLink CreateValidated(Uri feed, string? keyText, string? publisher)
    {
        if (feed.Scheme is not ("https" or "file"))
            throw new FormatException("feed 地址只支持 HTTPS 或本地 file 协议。");
        byte[] key;
        try { key = Base64Url.Decode(keyText ?? string.Empty); }
        catch (FormatException) { throw new FormatException("订阅内容密钥格式无效。"); }
        if (key.Length != 32)
            throw new FormatException("订阅内容密钥必须为 256 位。");
        if (string.IsNullOrWhiteSpace(publisher) || publisher.Length != 64
            || publisher.Any(character => !Uri.IsHexDigit(character)))
            throw new FormatException("订阅链接缺少有效的发布者公钥指纹。");
        return new MapSubscriptionLink(feed, key, publisher.ToUpperInvariant());
    }

    private static Dictionary<string, string> ParseParameters(string value) => value
        .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => part.Split('=', 2))
        .Where(pair => pair.Length == 2)
        .ToDictionary(
            pair => Uri.UnescapeDataString(pair[0]),
            pair => Uri.UnescapeDataString(pair[1]),
            StringComparer.OrdinalIgnoreCase);
}

public sealed record MapPublicationPayload(
    int SchemaVersion,
    Guid PublicationId,
    string PublisherHandle,
    string PublisherKeyId,
    string Version,
    DateTimeOffset PublishedAtUtc,
    string Scope,
    bool IntendedForOfficialWebsite,
    string PackageUri,
    long EncryptedLength,
    string EncryptedSha256,
    long PlaintextLength,
    string PlaintextSha256);

public sealed record SignedMapPublicationEnvelope(
    int SchemaVersion,
    string Payload,
    string Signature,
    string PublisherPublicKeyPem);

public sealed class MapSubscriptionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Link { get; set; } = string.Empty;
    public string FeedUri { get; set; } = string.Empty;
    public string PublisherKeyId { get; set; } = string.Empty;
    public string? PublisherHandle { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAtUtc { get; set; }
    public DateTimeOffset? LastAppliedAtUtc { get; set; }
    public DateTimeOffset? LastPublishedAtUtc { get; set; }
    public string? LastAppliedPlaintextSha256 { get; set; }
    public string? LastAppliedVersion { get; set; }
    public Dictionary<string, string> ClassBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Guid> InstalledMapIds { get; set; } = [];
    public string? LastError { get; set; }
}

public sealed record PendingMapSubscriptionUpdate(
    int SchemaVersion,
    Guid SubscriptionId,
    MapPublicationPayload Publication,
    string PackagePath,
    DateTimeOffset PreparedAtUtc);

public sealed class MapSubscriptionStore
{
    private readonly string _path;
    public MapSubscriptionStore(string rootDirectory) =>
        _path = Path.Combine(rootDirectory, "subscriptions.json");

    public IReadOnlyList<MapSubscriptionRecord> Load()
    {
        if (!File.Exists(_path)) return [];
        var records = JsonSerializer.Deserialize<List<MapSubscriptionRecord>>(
            File.ReadAllText(_path), MapSubscriptionProtocol.JsonOptions) ?? [];
        foreach (var record in records)
        {
            record.ClassBindings = new Dictionary<string, string>(
                record.ClassBindings ?? [], StringComparer.OrdinalIgnoreCase);
            record.InstalledMapIds ??= [];
        }
        return records;
    }

    public void Save(IEnumerable<MapSubscriptionRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(records, MapSubscriptionProtocol.JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }
}
