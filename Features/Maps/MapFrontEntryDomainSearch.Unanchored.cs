using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapFrontEntryDomainSearch
{
    internal static MapFrontEntryDomainSearchResult SolveUnanchored(
        MapFrontEntryReferenceIndex source, IReadOnlyList<Point> witnesses,
        double minimumScale, double maximumScale, CancellationToken cancellationToken,
        Func<bool>? budgetExpired = null)
        => SolveUnanchoredDomains(source, witnesses, minimumScale, maximumScale,
            cancellationToken, budgetExpired, stopAtFirstPossibleLeaf: false);

    internal static MapFrontEntryDomainClassificationResult ClassifyUnanchored(
        MapFrontEntryReferenceIndex source, IReadOnlyList<Point> witnesses,
        double minimumScale, double maximumScale, CancellationToken cancellationToken,
        Func<bool>? budgetExpired = null)
        => ToClassification(SolveUnanchoredDomains(source, witnesses, minimumScale, maximumScale,
            cancellationToken, budgetExpired, stopAtFirstPossibleLeaf: true), cancellationToken);

    private static MapFrontEntryDomainSearchResult SolveUnanchoredDomains(
        MapFrontEntryReferenceIndex source, IReadOnlyList<Point> witnesses,
        double minimumScale, double maximumScale, CancellationToken cancellationToken,
        Func<bool>? budgetExpired, bool stopAtFirstPossibleLeaf)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (witnesses.Count == 0 || !(minimumScale > 0)
            || !(maximumScale >= minimumScale) || !double.IsFinite(minimumScale + maximumScale)
            || source.ReferenceWidth <= 0 || source.ReferenceHeight <= 0
            || source.RawWhiteXByRow.Length != source.ReferenceHeight)
            return new(false, 0, 0, [], "Full-floor wall coverage is unavailable.");

        // Any supporting transform sends a source white pixel p to within3
        // of the first query witness q. Relative to the floor center c,
        // z = s*(c-p) - residual, so |z| <= s*halfDiagonal +3.
        // This covers every translation that can support q; it assigns no
        // entrance role and never changes the immutable reference anchors.
        var center = new Point2d(source.ReferenceWidth / 2d, source.ReferenceHeight / 2d);
        var halfDiagonal = double.Hypot(source.ReferenceWidth, source.ReferenceHeight) / 2;
        var radius = double.BitIncrement(6 * (halfDiagonal
            + Vpsg3LiveObservation.AutomaticPoseTolerancePixels / minimumScale));
        var extent = double.BitIncrement(Math.Max(8, radius * maximumScale / 6));
        if (!double.IsFinite(radius + extent))
            return new(false, 0, 0, [], "Full-floor envelope arithmetic is unavailable.");
        var queryOrigin = new Point2d(witnesses[0].X, witnesses[0].Y);
        var pending = new Stack<(MapFrontEntryPoseDomain Box, double Radius)>();
        pending.Push((new(minimumScale, maximumScale, -extent, extent, -extent, extent,
            center, queryOrigin), radius));
        return CompareDomains(source, witnesses, pending, cancellationToken, budgetExpired,
            stopAtFirstPossibleLeaf);
    }
}
