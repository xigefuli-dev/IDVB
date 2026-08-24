using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdentityVisionBridge.PluginPackaging;

public sealed record IdvpManifest
{
    public string Format { get; init; } = "idvb-plugin";

    public int FormatVersion { get; init; } = 1;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Version { get; init; }

    public required string Description { get; init; }

    public required IdvpPublisher Publisher { get; init; }

    public required IdvpEntryPoint EntryPoint { get; init; }

    public required IdvpCompatibility Compatibility { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public IdvpRiskDeclarations Risks { get; init; } = new();

    public IReadOnlyList<IdvpSettingDefinition> Settings { get; init; } = [];

    public IReadOnlyList<IdvpCommandDefinition> Commands { get; init; } = [];

    public IReadOnlyList<IdvpFileEntry> Files { get; init; } = [];

    public string? UpdateSource { get; init; }
}

public sealed record IdvpPublisher
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? KeyId { get; init; }
}

public sealed record IdvpEntryPoint
{
    public required string Assembly { get; init; }

    public required string Type { get; init; }
}

public sealed record IdvpCompatibility
{
    public required string PluginApi { get; init; }

    public required string Host { get; init; }

    public string TargetFramework { get; init; } = "net10.0";

    public string RuntimeIdentifier { get; init; } = "win-x64";
}

public sealed record IdvpRiskDeclarations
{
    public bool NativeCode { get; init; }

    public bool NetworkAccess { get; init; }

    public bool ExternalFileAccess { get; init; }

    public bool InputAutomation { get; init; }
}

public sealed record IdvpSettingDefinition
{
    public required string Key { get; init; }

    public required string Type { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public JsonElement Default { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public double? Step { get; init; }

    public IReadOnlyList<IdvpChoiceOption> Options { get; init; } = [];
}

public sealed record IdvpChoiceOption
{
    public required string Value { get; init; }

    public required string DisplayName { get; init; }
}

public sealed record IdvpCommandDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }
}

public sealed record IdvpFileEntry
{
    public required string Path { get; init; }

    public long Length { get; init; }

    public required string Sha256 { get; init; }
}

public sealed record IdvpSignature
{
    public required string Algorithm { get; init; }

    public string? KeyId { get; init; }

    public string? PublicKeySpki { get; init; }

    public string? Signature { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IdvpManifest))]
[JsonSerializable(typeof(IdvpSignature))]
internal partial class IdvpJsonContext : JsonSerializerContext;
