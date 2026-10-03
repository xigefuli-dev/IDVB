using IdentityVisionBridge.Vision;

namespace IDVBuff.Features.Maps;

internal static class VisionApiMapper
{
    public static VisionTransform? Transform(MapOverlayTransform? transform) => transform is null ? null : new()
    {
        ScaleX = transform.ScaleX, ScaleY = transform.ScaleY,
        OffsetX = transform.OffsetX, OffsetY = transform.OffsetY,
        ReferenceWidth = transform.ReferenceWidth, ReferenceHeight = transform.ReferenceHeight,
        OrientationDegrees = transform.OrientationDegrees
    };

    public static VisionTransform? Transform(MapSimilarityTransform? transform) => transform is null ? null : new()
    {
        ScaleX = transform.Scale, ScaleY = transform.Scale,
        OffsetX = transform.TranslationX, OffsetY = transform.TranslationY,
        OrientationDegrees = (int)Math.Round(transform.RotationDegrees)
    };

    public static VisionCandidate Candidate(SideEntranceScanCandidate candidate) => new()
    {
        MapId = candidate.Map.Id, DisplayName = candidate.Map.DisplayName, FloorKey = candidate.FloorKey,
        Score = candidate.MatchScore, EvidenceState = candidate.IdentityEvidence.State.ToString(),
        Reason = candidate.RejectionDetail
    };
}
