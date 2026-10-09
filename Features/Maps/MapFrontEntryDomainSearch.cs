using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal readonly record struct MapFrontEntryPoseDomain(
    double ScaleMinimum, double ScaleMaximum,
    double GateOffsetXMinimum, double GateOffsetXMaximum,
    double GateOffsetYMinimum, double GateOffsetYMaximum,
    Point2d SourceGate, Point2d QueryGate)
{
    internal (double Scale, double X, double Y) Midpoint
    {
        get
        {
            var scale = (ScaleMinimum + ScaleMaximum) * .5;
            return (scale, QueryGate.X + (GateOffsetXMinimum + GateOffsetXMaximum) * .5
                - scale * SourceGate.X,
                QueryGate.Y + (GateOffsetYMinimum + GateOffsetYMaximum) * .5
                - scale * SourceGate.Y);
        }
    }
}

internal sealed record MapFrontEntryDomainSearchResult(
    bool Complete, long VisitedDomains, long ContradictedDomains,
    IReadOnlyList<MapFrontEntryPoseDomain> RemainingDomains, string FailureReason)
{
    internal bool StoppedAtPossibleLeaf { get; init; }
}

internal enum MapFrontEntryDomainOutcome { PossibleLeaf, Contradicted, Unresolved }

internal sealed record MapFrontEntryDomainClassificationResult(
    MapFrontEntryDomainOutcome Outcome, bool EnumerationComplete,
    long VisitedDomains, long ContradictedDomains, int? RemainingDomainCount,
    bool PossibleLeafObserved, string FailureReason)
{
    internal bool Complete => Outcome != MapFrontEntryDomainOutcome.Unresolved;
}

internal enum MapFrontEntryPoseSupport { Supported, Contradicted, Unresolved }

internal sealed record MapFrontEntryPoseCheck(MapFrontEntryPoseSupport Support,
    int TestedWitnesses, int UnsupportedWitnesses, Point? FirstUnsupportedWitness);

/// <summary>
/// Tests positive observed wall pixels over the complete gate/scale search
/// envelope. Each exclusion requires that no exact source white pixel can
/// reach one query witness within3 anywhere in that domain. Black query pixels
/// and unobserved doors are never evidence. Surviving boxes are possibilities,
/// not successful poses, identities or permission to display an overlay.
/// </summary>
internal static partial class MapFrontEntryDomainSearch
{
    internal static bool IsPoseSupported(MapFrontEntryReferenceIndex source,
        IReadOnlyList<Point> witnesses, double scale, double x, double y,
        CancellationToken cancellationToken) =>
        CheckPose(source, witnesses, scale, x, y, cancellationToken).Support
            == MapFrontEntryPoseSupport.Supported;

    internal static MapFrontEntryPoseCheck CheckPose(MapFrontEntryReferenceIndex source,
        IReadOnlyList<Point> witnesses, double scale, double x, double y,
        CancellationToken cancellationToken, Func<bool>? budgetExpired = null)
    {
        var tested = 0;
        var unsupported = 0;
        Point? firstUnsupported = null;
        var uncertain = false;
        MapFrontEntryPoseCheck Unresolved() =>
            new(MapFrontEntryPoseSupport.Unresolved, tested, unsupported, firstUnsupported);
        if (!(scale > 0) || !double.IsFinite(scale) || !double.IsFinite(x) || !double.IsFinite(y)
            || witnesses.Count == 0 || source.ReferenceWidth <= 0 || source.ReferenceHeight <= 0
            || source.RawWhiteXByRow.Length != source.ReferenceHeight) return Unresolved();
        var singleton = new MapFrontEntryPoseDomain(scale, scale, x, x, y, y, new(), new());
        foreach (var query in witnesses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budgetExpired?.Invoke() == true) return Unresolved();
            var centerX = (query.X - x) / scale;
            var centerY = (query.Y - y) / scale;
            var radius = Vpsg3LiveObservation.AutomaticPoseTolerancePixels / scale;
            if (!double.IsFinite(centerX + centerY + radius)) return Unresolved();
            var x0 = (int)Math.Clamp(Math.Floor(centerX - radius) - 1, 0, source.ReferenceWidth);
            var x1 = (int)Math.Clamp(Math.Ceiling(centerX + radius) + 1, -1, source.ReferenceWidth - 1);
            var y0 = (int)Math.Clamp(Math.Floor(centerY - radius) - 1, 0, source.ReferenceHeight);
            var y1 = (int)Math.Clamp(Math.Ceiling(centerY + radius) + 1, -1, source.ReferenceHeight - 1);
            var supported = false;
            for (var rowIndex = y0; rowIndex <= y1 && !supported; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budgetExpired?.Invoke() == true) return Unresolved();
                var row = source.RawWhiteXByRow[rowIndex];
                var left = 0; var right = row.Length;
                while (left < right)
                {
                    var middle = left + (right - left) / 2;
                    if (row[middle] < x0) left = middle + 1;
                    else right = middle;
                }
                for (var i = left; i < row.Length && row[i] <= x1; i++)
                    if (double.Hypot(row[i] * scale + x - query.X,
                        rowIndex * scale + y - query.Y) <= Vpsg3LiveObservation.AutomaticPoseTolerancePixels)
                    { supported = true; break; }
            }
            tested++;
            if (!supported)
            {
                // A concrete failed pose is not unfinished map search. Still
                // require the outward-rounded singleton test to certify absence;
                // floating-point contact or incomplete work stays unresolved.
                var possible = CanSupport(source, singleton, query, cancellationToken, budgetExpired);
                if (possible is null) return Unresolved();
                if (possible.Value) uncertain = true;
                unsupported++;
                firstUnsupported ??= query;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (budgetExpired?.Invoke() == true || uncertain) return Unresolved();
        return new(unsupported == 0 ? MapFrontEntryPoseSupport.Supported
            : MapFrontEntryPoseSupport.Contradicted, tested, unsupported, firstUnsupported);
    }

    internal static MapFrontEntryDomainSearchResult Solve(
        MapFrontEntryReferenceIndex source, IReadOnlyList<Point> witnesses,
        IReadOnlyList<Point2d> gates, double minimumScale, double maximumScale,
        CancellationToken cancellationToken, Func<bool>? budgetExpired = null)
        => SolveAnchoredDomains(source, witnesses, gates, minimumScale, maximumScale,
            cancellationToken, budgetExpired, stopAtFirstPossibleLeaf: false);

    internal static MapFrontEntryDomainClassificationResult Classify(
        MapFrontEntryReferenceIndex source, IReadOnlyList<Point> witnesses,
        IReadOnlyList<Point2d> gates, double minimumScale, double maximumScale,
        CancellationToken cancellationToken, Func<bool>? budgetExpired = null)
        => ToClassification(SolveAnchoredDomains(source, witnesses, gates, minimumScale,
            maximumScale, cancellationToken, budgetExpired, stopAtFirstPossibleLeaf: true), cancellationToken);

    private static MapFrontEntryDomainClassificationResult ToClassification(
        MapFrontEntryDomainSearchResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observed = result.RemainingDomains.Count > 0;
        var outcome = result.StoppedAtPossibleLeaf || result.Complete && observed
            ? MapFrontEntryDomainOutcome.PossibleLeaf
            : result.Complete ? MapFrontEntryDomainOutcome.Contradicted
            : MapFrontEntryDomainOutcome.Unresolved;
        return new(outcome, result.Complete, result.VisitedDomains, result.ContradictedDomains,
            result.Complete ? result.RemainingDomains.Count : null, observed, result.FailureReason);
    }

    private static MapFrontEntryDomainSearchResult SolveAnchoredDomains(
        MapFrontEntryReferenceIndex source, IReadOnlyList<Point> witnesses,
        IReadOnlyList<Point2d> gates, double minimumScale, double maximumScale,
        CancellationToken cancellationToken, Func<bool>? budgetExpired,
        bool stopAtFirstPossibleLeaf)
    {
        var pending = new Stack<(MapFrontEntryPoseDomain Box, double Radius)>();
        if (witnesses.Count == 0 || gates.Count == 0 || !(minimumScale > 0)
            || !(maximumScale >= minimumScale) || !double.IsFinite(minimumScale + maximumScale)
            || gates.Any(gate => !double.IsFinite(gate.X) || !double.IsFinite(gate.Y)))
            return new(false, 0, 0, [], "No supported wall witnesses or finite scale envelope.");
        // Role is unknown in the live detector. Keep every supplied MAIN/SIDE/
        // second-floor interpretation rather than choosing one from a label.
        if (source.Anchors.Length == 0 || source.Anchors.Any(anchor => !anchor.HasUsableBounds))
            return new(false, 0, 0, [], "Entrance reference coverage is unavailable.");
        foreach (var anchor in source.Anchors)
        foreach (var gate in gates)
        {
            var extent = Math.Max(8, anchor.Radius!.Value * maximumScale / 6);
            if (!double.IsFinite(extent))
                return new(false, 0, 0, [], "Entrance envelope arithmetic is unavailable.");
            pending.Push((new(minimumScale, maximumScale, -extent, extent, -extent, extent,
                anchor.Center!.Value, gate), anchor.Radius.Value));
        }
        return CompareDomains(source, witnesses, pending, cancellationToken, budgetExpired,
            stopAtFirstPossibleLeaf);
    }

    private static MapFrontEntryDomainSearchResult CompareDomains(
        MapFrontEntryReferenceIndex source, IReadOnlyList<Point> witnesses,
        Stack<(MapFrontEntryPoseDomain Box, double Radius)> pending,
        CancellationToken cancellationToken, Func<bool>? budgetExpired,
        bool stopAtFirstPossibleLeaf = false)
    {
        var remaining = new List<MapFrontEntryPoseDomain>();
        // Witnesses are fixed for this comparison; child domains retain their
        // query gate. Cache only the scale-independent extent within this call.
        var witnessExtents = new Dictionary<Point2d, double>();
        long visited = 0, contradicted = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budgetExpired?.Invoke() == true)
                return new(false, visited, contradicted, remaining, "Wall-domain comparison exceeded its budget.");
            var (box, radius) = pending.Pop();
            visited++;
            if (visited > 250_000)
                return new(false, visited, contradicted, remaining, "Wall-domain comparison remains unresolved.");
            var gateTolerance = Math.Max(8, radius * box.ScaleMaximum / 6);
            var gateX = DistanceToInterval(0, box.GateOffsetXMinimum, box.GateOffsetXMaximum);
            var gateY = DistanceToInterval(0, box.GateOffsetYMinimum, box.GateOffsetYMaximum);
            if (double.Hypot(gateX, gateY) > gateTolerance + ArithmeticPadding(gateTolerance))
            {
                contradicted++;
                continue;
            }
            var excluded = false;
            foreach (var witness in witnesses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var support = CanSupport(source, box, witness, cancellationToken, budgetExpired);
                if (support is null)
                    return new(false, visited, contradicted, remaining, "Wall-domain comparison exceeded its budget.");
                if (support == false)
                {
                    excluded = true;
                    break;
                }
            }
            if (excluded)
            {
                contradicted++;
                continue;
            }
            var scaleSpan = box.ScaleMaximum - box.ScaleMinimum;
            var xSpan = box.GateOffsetXMaximum - box.GateOffsetXMinimum;
            var ySpan = box.GateOffsetYMaximum - box.GateOffsetYMinimum;
            if (!witnessExtents.TryGetValue(box.QueryGate, out var witnessExtent))
            {
                witnessExtent = witnesses.Max(point => double.Hypot(
                    point.X - box.QueryGate.X, point.Y - box.QueryGate.Y));
                witnessExtents.Add(box.QueryGate, witnessExtent);
            }
            var relativeExtent = witnessExtent / box.ScaleMinimum
                + gateTolerance / box.ScaleMinimum;
            var scaleDisplacement = scaleSpan * relativeExtent;
            // This resolution closes subdivision only. These boxes still keep
            // all their possibilities until a caller verifies a real pose.
            if (scaleDisplacement + xSpan + ySpan <= 2)
            {
                remaining.Add(box);
                if (stopAtFirstPossibleLeaf)
                {
                    // One retained leaf proves only that this floor cannot yet
                    // be excluded. Other branches may remain unenumerated; they
                    // cannot invalidate this possibility or certify a pose.
                    cancellationToken.ThrowIfCancellationRequested();
                    if (budgetExpired?.Invoke() == true)
                        return new(false, visited, contradicted, remaining,
                            "Wall-domain comparison exceeded its budget.");
                    return new(pending.Count == 0, visited, contradicted, remaining, string.Empty)
                        { StoppedAtPossibleLeaf = true };
                }
                continue;
            }
            if (scaleDisplacement >= Math.Max(xSpan, ySpan))
            {
                var middle = (box.ScaleMinimum + box.ScaleMaximum) * .5;
                if (middle == box.ScaleMinimum || middle == box.ScaleMaximum)
                    return new(false, visited, contradicted, remaining, "Scale interval cannot be resolved numerically.");
                pending.Push((box with { ScaleMinimum = middle }, radius));
                pending.Push((box with { ScaleMaximum = middle }, radius));
            }
            else if (xSpan >= ySpan)
            {
                var middle = (box.GateOffsetXMinimum + box.GateOffsetXMaximum) * .5;
                pending.Push((box with { GateOffsetXMinimum = middle }, radius));
                pending.Push((box with { GateOffsetXMaximum = middle }, radius));
            }
            else
            {
                var middle = (box.GateOffsetYMinimum + box.GateOffsetYMaximum) * .5;
                pending.Push((box with { GateOffsetYMinimum = middle }, radius));
                pending.Push((box with { GateOffsetYMaximum = middle }, radius));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (budgetExpired?.Invoke() == true)
            return new(false, visited, contradicted, remaining, "Wall-domain comparison exceeded its budget.");
        return new(true, visited, contradicted, remaining, string.Empty);
    }

    private static bool? CanSupport(MapFrontEntryReferenceIndex source,
        MapFrontEntryPoseDomain box, Point query, CancellationToken cancellationToken,
        Func<bool>? budgetExpired)
    {
        const double tolerance = Vpsg3LiveObservation.AutomaticPoseTolerancePixels;
        // Inverse-coordinate bounds only narrow integer enumeration. The
        // forward image-box distance below decides support; padding cannot
        // falsely certify absence at a floating-point boundary.
        var xBounds = InverseBounds(query.X - box.QueryGate.X,
            box.GateOffsetXMinimum, box.GateOffsetXMaximum, box.ScaleMinimum,
            box.ScaleMaximum, box.SourceGate.X, tolerance);
        var yBounds = InverseBounds(query.Y - box.QueryGate.Y,
            box.GateOffsetYMinimum, box.GateOffsetYMaximum, box.ScaleMinimum,
            box.ScaleMaximum, box.SourceGate.Y, tolerance);
        if (!double.IsFinite(xBounds.Minimum + xBounds.Maximum + yBounds.Minimum + yBounds.Maximum))
            return true;
        var x0 = (int)Math.Clamp(Math.Floor(xBounds.Minimum) - 1, 0, source.ReferenceWidth);
        var x1 = (int)Math.Clamp(Math.Ceiling(xBounds.Maximum) + 1, -1, source.ReferenceWidth - 1);
        var y0 = (int)Math.Clamp(Math.Floor(yBounds.Minimum) - 1, 0, source.ReferenceHeight);
        var y1 = (int)Math.Clamp(Math.Ceiling(yBounds.Maximum) + 1, -1, source.ReferenceHeight - 1);
        for (var y = y0; y <= y1; y++)
        {
            if ((y & 7) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budgetExpired?.Invoke() == true) return null;
            }
            var row = source.RawWhiteXByRow[y];
            var left = 0; var right = row.Length;
            while (left < right)
            {
                var middle = left + (right - left) / 2;
                if (row[middle] < x0) left = middle + 1;
                else right = middle;
            }
            for (var i = left; i < row.Length && row[i] <= x1; i++)
            {
                var px = row[i] - box.SourceGate.X;
                var py = y - box.SourceGate.Y;
                var imageX0 = box.QueryGate.X + Math.Min(px * box.ScaleMinimum, px * box.ScaleMaximum)
                    + box.GateOffsetXMinimum;
                var imageX1 = box.QueryGate.X + Math.Max(px * box.ScaleMinimum, px * box.ScaleMaximum)
                    + box.GateOffsetXMaximum;
                var imageY0 = box.QueryGate.Y + Math.Min(py * box.ScaleMinimum, py * box.ScaleMaximum)
                    + box.GateOffsetYMinimum;
                var imageY1 = box.QueryGate.Y + Math.Max(py * box.ScaleMinimum, py * box.ScaleMaximum)
                    + box.GateOffsetYMaximum;
                // Use original operand magnitudes, not just their potentially
                // cancelled image result, including the inverse q-g subtraction.
                var padding = ArithmeticPadding(Math.Abs(box.QueryGate.X) + Math.Abs(box.QueryGate.Y)
                    + (Math.Abs(row[i]) + Math.Abs(y) + Math.Abs(box.SourceGate.X)
                        + Math.Abs(box.SourceGate.Y)) * box.ScaleMaximum
                    + Math.Abs(box.GateOffsetXMinimum) + Math.Abs(box.GateOffsetXMaximum)
                    + Math.Abs(box.GateOffsetYMinimum) + Math.Abs(box.GateOffsetYMaximum)
                    + Math.Abs(query.X) + Math.Abs(query.Y));
                if (!double.IsFinite(padding + imageX0 + imageX1 + imageY0 + imageY1)) return true;
                var dx = DistanceToInterval(query.X, imageX0 - padding, imageX1 + padding);
                var dy = DistanceToInterval(query.Y, imageY0 - padding, imageY1 + padding);
                if (double.Hypot(dx, dy) <= tolerance + padding) return true;
            }
        }
        return false;
    }

    private static (double Minimum, double Maximum) InverseBounds(double query,
        double offsetMinimum, double offsetMaximum, double scaleMinimum,
        double scaleMaximum, double anchor, double tolerance)
    {
        // Both offset extrema need both tolerance extrema. The lower numerator
        // is q-maxOffset-3, the upper is q-minOffset+3.
        var lowNumerator = query - offsetMaximum - tolerance;
        var highNumerator = query - offsetMinimum + tolerance;
        var a = lowNumerator / scaleMinimum; var b = lowNumerator / scaleMaximum;
        var c = highNumerator / scaleMinimum; var d = highNumerator / scaleMaximum;
        var padding = ArithmeticPadding(Math.Abs(anchor) + Math.Abs(query) / scaleMinimum
            + (Math.Abs(offsetMinimum) + Math.Abs(offsetMaximum) + tolerance) / scaleMinimum);
        return (anchor + Math.Min(Math.Min(a, b), Math.Min(c, d)) - padding,
            anchor + Math.Max(Math.Max(a, b), Math.Max(c, d)) + padding);
    }

    private static double DistanceToInterval(double point, double minimum, double maximum) =>
        point < minimum ? minimum - point : point > maximum ? point - maximum : 0;
    private static double ArithmeticPadding(double magnitude) =>
        128 * 2.220446049250313e-16 * (1 + magnitude);
}
