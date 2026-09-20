using IDVBuff.Survey.Domain;

namespace IDVBuff.RealCLI;

internal sealed class SurveyReplayManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string? Name { get; set; }
    public string MapClass { get; set; } = "S1";
    public Guid? MatchId { get; set; }
    public long OperationEpoch { get; set; } = 1;
    public List<SurveyReplayFrame> Frames { get; set; } = [];
}

internal sealed class SurveyReplayFrame
{
    public string Path { get; set; } = string.Empty;
    public long MapToggleVersion { get; set; }
    public string FloorKey { get; set; } = "1f";
    public DateTimeOffset? CapturedAt { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public double Dpi { get; set; } = 120d;
    public SurveyPixelRect? Viewport { get; set; }
}

internal sealed record SurveyReplayResult(
    Guid ProjectId,
    long Revision,
    int ObservationCount,
    int RegisteredCount,
    int UnregisteredCount,
    int ConstraintCount,
    string VisualAssetSha256,
    string StructureAssetSha256,
    IReadOnlyList<SurveyReplayObservationResult> Observations);

internal sealed record SurveyReplayObservationResult(
    Guid ObservationId,
    long MapToggleVersion,
    string State,
    double Quality,
    string? Error,
    double TranslationX,
    double TranslationY,
    double RotationDegrees,
    double ScaleX,
    double ScaleY);
