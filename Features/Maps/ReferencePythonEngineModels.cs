using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

internal sealed class ReferencePythonEngineRequest
{
    public string Operation { get; init; } = "identify";
    public string? MapClass { get; init; }
    public Guid? MapId { get; init; }
    public string? SourceMapId { get; init; }
    public string? Floor { get; init; }
    public int BudgetMs { get; init; } = 1000;
    public ReferencePythonPose? PriorPose { get; init; }
}

/// <summary>Pose in full client-image pixels, before adding the window origin.</summary>
internal sealed class ReferencePythonPose
{
    public Guid MapId { get; init; }
    public string Floor { get; init; } = string.Empty;
    public bool Trusted { get; init; }
    public double Scale { get; init; }
    public double Tx { get; init; }
    public double Ty { get; init; }
}

internal sealed class ReferencePythonEngineResponse
{
    public string Id { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public Guid? MapId { get; init; }
    public string? SourceMapId { get; init; }
    public string? Floor { get; init; }
    public bool IdentityVerified { get; init; }
    public bool PoseVerified { get; init; }
    public double? Confidence { get; init; }
    public double? Scale { get; init; }
    public double? Tx { get; init; }
    public double? Ty { get; init; }
    public string? Reason { get; init; }
    public bool? Ok { get; init; }
    public bool? Ready { get; init; }
    public string? Error { get; init; }
    public JsonElement Timings { get; init; }
    public JsonElement Evidence { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; init; }
}
