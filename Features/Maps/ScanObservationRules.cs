using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Presentation is reversible; it never constitutes identity evidence.</summary>
internal static class ScanObservationRules
{
    // Readiness only: a small explored area must reach gate/identity verification even
    // when most of the calibrated viewport is fog. This never authorizes publication.
    public static bool HasVisibleStructure(Mat image)
    {
        using var observation = Vpsg3FastLiveExtractor.Extract(image, null, 256);
        using var points = observation.ObservedEdges.FindNonZero();
        if (points.Total() < 80) return false;
        var bounds = Cv2.BoundingRect(points);
        return bounds.Width >= 30 && bounds.Height >= 30;
    }

    public static SideEntranceScanCandidate? SelectPreview(
        IReadOnlyList<SideEntranceScanCandidate> candidates,
        Guid? previousMapId, string? previousFloor)
    {
        var supported = candidates.Where(c => c.Disposition == SideEntranceCandidateDisposition.Reliable
            && c.IdentityEvidence.State == ScanIdentityState.Supported
            && c.VerifiedTransform is not null
            && double.IsFinite(c.IdentityEvidence.ForwardMeanPixels)).ToArray();
        // Keep a still-supported resource stable while similar variants exchange rank.
        // A previous result absent from this frame may remain a mini-map preview only.
        return supported.FirstOrDefault(c => c.Map.Id == previousMapId && c.FloorKey == previousFloor)
            ?? supported.OrderBy(c => c.IdentityEvidence.ForwardMeanPixels
                + (1 - c.IdentityEvidence.SupportedFraction) * 5).FirstOrDefault();
    }
}
