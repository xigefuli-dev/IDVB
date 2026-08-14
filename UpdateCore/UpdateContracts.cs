using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.UpdateCore;

public static class UpdateProtocol
{
    public const int EnvelopeSchemaVersion = 1;
    public const int PipeSchemaVersion = 1;
    public const string PackageId = "IdentityVisionBridge";
    public const string TestChannel = "win-x64-test";
    public const string StableChannel = "win-x64-stable";
    public const string DefaultUpdateRoot = "https://download.xgflee.com/updates/";
    public const string ShutdownPipeName = "IdentityVisionBridge.UpdateControl.v1";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool IsKnownChannel(string? channel) =>
        string.Equals(channel, TestChannel, StringComparison.Ordinal)
        || string.Equals(channel, StableChannel, StringComparison.Ordinal);

    public static Uri GetChannelUri(Uri updateRoot, string channel)
    {
        if (!IsKnownChannel(channel))
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown IDVB update channel.");

        var root = updateRoot.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? updateRoot
            : new Uri(updateRoot.AbsoluteUri + '/', UriKind.Absolute);
        return new Uri(root, channel + "/");
    }
}

public sealed record UpdateFeedEnvelope(
    int SchemaVersion,
    string KeyId,
    string Payload,
    string Signature);

public sealed record UpdateFeedPayload(
    int SchemaVersion,
    string Channel,
    string PackageId,
    string PublicVersion,
    string ProductVersion,
    string VelopackVersion,
    string MinimumVersion,
    bool MigrationBaseline,
    DateTimeOffset PublishedUtc,
    string Commit,
    string ReleaseNotes,
    string FeedJson,
    UpdateInstallerMetadata Installer);

public sealed record UpdateInstallerMetadata(
    string FileName,
    string Sha256,
    long Size);

public sealed record UpdateShutdownRequest(
    int SchemaVersion,
    string Type,
    string TargetVersion,
    int UpdaterProcessId);

public sealed record UpdateShutdownResponse(
    int SchemaVersion,
    bool Accepted,
    int MainProcessId,
    string? Error = null);

public sealed record VerifiedUpdateFeed(UpdateFeedPayload Payload, byte[] CanonicalPayload);

public enum UpdateWorkflowState
{
    Initializing,
    Checking,
    NoUpdate,
    UpdateAvailable,
    Downloading,
    ReadyToInstall,
    RequestingShutdown,
    Applying,
    Cancelled,
    Error
}
