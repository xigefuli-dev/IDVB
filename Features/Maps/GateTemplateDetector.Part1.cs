using IDVBuff.Core.Contracts;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Detects the two in-game Gate icons from one preprocessed map viewport.
/// The detector owns its template and remembers the last successful scale.
/// </summary>
public sealed partial class GateTemplateDetector : IDisposable
{

    private static IReadOnlyList<double> GetConfirmationScales(double? predictedScale)
    {
        if (predictedScale is not { } ps || ps <= 0d || !double.IsFinite(ps))
            return [];

        return new[]
        {
            ps * 0.95d,
            ps,
            ps * 1.05d,
        }
        .Select(s => Math.Clamp(s, 0.12d, 1.5d))
        .DistinctBy(s => Math.Round(s, 3))
        .ToArray();
    }

    private static IReadOnlyList<double> GetLockedOnlyScales(double? lockedScale)
    {
        if (lockedScale is not { } ls || ls <= 0d || !double.IsFinite(ls))
            return [];

        return new[] { Math.Clamp(ls, 0.12d, 1.5d) };
    }

    // ── ROI helpers ───────────────────────────────────────────────────────

    private static Rect BuildConfirmationRoi(
        MapScreenRect predictedRegion,
        int templateWidth,
        int templateHeight,
        GateSearchContext context,
        Mat matchImage,
        MapScreenRect viewportBounds)
    {
        var paddingX = Math.Max(
            context.LocalRoiMinimumPaddingPixels,
            (int)Math.Round(templateWidth * context.LocalRoiTemplatePaddingFactor)
                + context.MaximumExpectedMotionPixels);
        var paddingY = Math.Max(
            context.LocalRoiMinimumPaddingPixels,
            (int)Math.Round(templateHeight * context.LocalRoiTemplatePaddingFactor)
                + context.MaximumExpectedMotionPixels);

        // Convert absolute screen coordinates to viewport-local coordinates
        // for ROI construction. matchImage is the viewport crop.
        var localCenterX = predictedRegion.CenterX - viewportBounds.X;
        var localCenterY = predictedRegion.CenterY - viewportBounds.Y;
        var left = Math.Max(0, (int)Math.Round(localCenterX - (predictedRegion.Width / 2d) - paddingX));
        var top = Math.Max(0, (int)Math.Round(localCenterY - (predictedRegion.Height / 2d) - paddingY));
        var right = Math.Min(matchImage.Width,
            (int)Math.Round(localCenterX + (predictedRegion.Width / 2d) + paddingX));
        var bottom = Math.Min(matchImage.Height,
            (int)Math.Round(localCenterY + (predictedRegion.Height / 2d) + paddingY));

        if (right <= left || bottom <= top)
            return new Rect(0, 0, Math.Min(templateWidth, matchImage.Width),
                Math.Min(templateHeight, matchImage.Height));

        return new Rect(left, top, right - left, bottom - top);
    }

    // ── Cross-scale spatial clustering ────────────────────────────────────

    /// <summary>
    /// Groups raw candidates into spatially-clustered groups.
    /// Each cluster represents the same physical gate detected at adjacent scales.
    /// </summary>
    internal static List<List<GateDetection>> ClusterAcrossScales(
        List<GateDetection> raw)
    {
        if (raw.Count == 0) return [];

        // Sort by score descending; greedily assign each candidate to the first
        // existing cluster it overlaps (IoU >= threshold), or start a new cluster.
        var clusters = new List<List<GateDetection>>();
        foreach (var candidate in raw.OrderByDescending(c => c.Score))
        {
            var found = false;
            foreach (var cluster in clusters)
            {
                foreach (var member in cluster)
                {
                    if (IntersectionOverUnion(
                            candidate.ScreenBounds, member.ScreenBounds)
                        >= GateTemplateRules.SpatialClusterIouThreshold)
                    {
                        cluster.Add(candidate);
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
            if (!found)
                clusters.Add([candidate]);
        }
        return clusters;
    }

    internal static IReadOnlyList<GateDetection> SelectTopCandidates(
        List<List<GateDetection>> clusters)
    {
        var selected = new List<GateDetection>();
        // Each cluster → single best candidate (highest score).
        var bestPerCluster = clusters
            .Select(cluster => cluster.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .ToList();

        foreach (var candidate in bestPerCluster)
        {
            if (selected.Any(existing =>
                    IntersectionOverUnion(
                        existing.ScreenBounds, candidate.ScreenBounds)
                    >= GateTemplateRules.NmsIouThreshold))
            {
                continue;
            }
            selected.Add(candidate);
            if (selected.Count == GateTemplateRules.MaximumGateCandidates)
                break;
        }
        return selected;
    }

    // ── Static helpers ────────────────────────────────────────────────────

    private static Rect CreateSuppressionRect(Point location, Size template, Size output)
    {
        var left = Math.Max(0, location.X - (template.Width / 2));
        var top = Math.Max(0, location.Y - (template.Height / 2));
        var right = Math.Min(output.Width, location.X + template.Width);
        var bottom = Math.Min(output.Height, location.Y + template.Height);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static double IntersectionOverUnion(MapScreenRect left, MapScreenRect right)
    {
        var intersectionLeft = Math.Max(left.X, right.X);
        var intersectionTop = Math.Max(left.Y, right.Y);
        var intersectionRight = Math.Min(left.X + left.Width, right.X + right.Width);
        var intersectionBottom = Math.Min(left.Y + left.Height, right.Y + right.Height);
        var intersectionWidth = Math.Max(0d, intersectionRight - intersectionLeft);
        var intersectionHeight = Math.Max(0d, intersectionBottom - intersectionTop);
        var intersection = intersectionWidth * intersectionHeight;
        var union = (left.Width * left.Height) + (right.Width * right.Height) - intersection;
        return union <= 0d ? 0d : intersection / union;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_configProvider is not null)
            _configProvider.ConfigChanged -= OnConfigChanged;
        _gateSource.Dispose();
    }
}
