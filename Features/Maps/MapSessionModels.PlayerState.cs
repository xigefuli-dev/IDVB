using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed class MapPlayerState
{
    public PlayerSlot PlayerSlot { get; init; }
    public MapViewportPoint ViewportPoint { get; init; }
    public MapScreenPoint ScreenPoint { get; init; }
    public MapReferencePoint ReferencePoint { get; init; }
    public double MarkerWidth { get; init; }
    public double MarkerHeight { get; init; }
    public double Confidence { get; init; }
    public DateTimeOffset ObservedAt { get; init; }

    [JsonIgnore]
    public bool IsTrusted =>
        IsTrustedAt(MapSessionRules.MinimumPlayerConfidence);

    public bool IsTrustedAt(double minimumConfidence) =>
        ViewportPoint.IsFinite
        && ScreenPoint.IsFinite
        && ReferencePoint.IsFinite
        && Enum.IsDefined(PlayerSlot)
        && double.IsFinite(MarkerWidth)
        && MarkerWidth > 0d
        && double.IsFinite(MarkerHeight)
        && MarkerHeight > 0d
        && Confidence >= minimumConfidence;
}
