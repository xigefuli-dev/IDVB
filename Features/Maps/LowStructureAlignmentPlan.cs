using IDVBuff.Core.Models;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal enum LowStructureAlignmentRoute
{
    CachedFixed,
    ShapeSeed,
    SparseCoarseSeed,
    IncrementalRecovery
}

internal static class LowStructureScaleEvidenceRules
{
    public const int MinimumIndependentScaleConfirmations = 5;
    public const double MinimumClusterTolerance = 0.003d;
    public const double MaximumClusterTolerance = 0.006d;
    public const double MaximumLockRelativeDifference = MaximumClusterTolerance;
    public const double ResolutionToleranceMultiplier = 1.2d;

    public static double ResolveClusterTolerance(double relativeResolution) =>
        Math.Clamp(
            double.IsFinite(relativeResolution) && relativeResolution > 0d
                ? relativeResolution * ResolutionToleranceMultiplier
                : MinimumClusterTolerance,
            MinimumClusterTolerance,
            MaximumClusterTolerance);

    public static double RelativeDifference(double first, double second) =>
        Math.Abs(first - second) / Math.Max(Math.Max(first, second), 0.000001d);

    public static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(double.IsFinite).Order().ToArray();
        if (ordered.Length == 0)
            return 0d;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    public static double RelativeMad(IEnumerable<double> values)
    {
        var samples = values.Where(double.IsFinite).ToArray();
        var median = Median(samples);
        return samples.Length == 0 || median <= 0d
            ? 0d
            : Median(samples.Select(value => Math.Abs(value - median) / median));
    }

    public static bool IsIndependentScaleRoute(string? route) =>
        Enum.TryParse<LowStructureAlignmentRoute>(route, out var parsed)
        && parsed is LowStructureAlignmentRoute.ShapeSeed
            or LowStructureAlignmentRoute.SparseCoarseSeed
            or LowStructureAlignmentRoute.IncrementalRecovery;
}

internal sealed record LowStructureShapeScaleEvidence(
    double WidthScale,
    double HeightScale,
    double ComponentScale,
    double LineSpacingScale,
    double WidthProjectionCorrelation = 0d,
    double HeightProjectionCorrelation = 0d)
{
    public bool HasWidthScale => IsUsable(WidthScale);
    public bool HasHeightScale => IsUsable(HeightScale);

    public bool AxesAgree(double tolerance) =>
        HasWidthScale
        && HasHeightScale
        && RelativeDifference(WidthScale, HeightScale)
            <= Math.Max(0d, tolerance);

    private static bool IsUsable(double value) =>
        double.IsFinite(value) && value > 0.05d;

    internal static double RelativeDifference(double first, double second) =>
        !IsUsable(first) || !IsUsable(second)
            ? double.PositiveInfinity
            : Math.Abs(first - second) / Math.Max(first, second);
}

internal sealed record LowStructureScaleProposal(
    double Scale,
    int Votes,
    double AxisAgreement,
    double ProjectionAgreement,
    double Score);

/// <summary>
/// Pure low-structure routing policy. It deliberately separates the number
/// of scales in a recovery grid from the amount of work allowed in one call.
/// </summary>
internal sealed partial record LowStructureAlignmentPlan(
    LowStructureAlignmentRoute Route,
    IReadOnlyList<double> Scales,
    int TranslationTopK,
    int BudgetMilliseconds,
    bool CanDirectAccept,
    int RecoveryBatch,
    int RecoveryTotalScaleCount,
    string BudgetTerminationReason = "",
    double ScaleResolutionRatio = 0d,
    int ScaleBasinCount = 0,
    bool ScaleSelectionAmbiguous = false)
{
    public bool UsesVpsg => false;

    internal static LowStructureConfig CreateConfig(
        MapStructureRegistrationTuning tuning) => new()
    {
        MinimumScale = tuning.LowStructureMinimumScale,
        MaximumScale = tuning.LowStructureMaximumScale,
        ScaleHypothesisCount = tuning.LowStructureScaleHypothesisCount,
        MaximumScalesPerFrame = tuning.LowStructureMaximumScalesPerFrame,
        TranslationTopK = tuning.LowStructureTranslationTopK,
        ScaleConsistencyTolerance = tuning.LowStructureScaleConsistencyTolerance,
        WarmPathBudgetMilliseconds = tuning.LowStructureWarmPathBudgetMilliseconds,
        ColdPathBudgetMilliseconds = tuning.LowStructureColdPathBudgetMilliseconds,
        EndToEndBudgetMilliseconds = tuning.LowStructureEndToEndBudgetMilliseconds
    };

    public static LowStructureAlignmentPlan CachedFixed(
        double scale,
        LowStructureConfig config) =>
        new(
            LowStructureAlignmentRoute.CachedFixed,
            IsUsable(scale) ? [scale] : [],
            Math.Clamp(config.TranslationTopK, 1, 2),
            Math.Clamp(config.WarmPathBudgetMilliseconds, 50, 300),
            CanDirectAccept: true,
            RecoveryBatch: 0,
            RecoveryTotalScaleCount: 1);

    public static LowStructureAlignmentPlan ShapeSeed(
        IReadOnlyList<LowStructureScaleProposal> proposals,
        LowStructureShapeScaleEvidence evidence,
        LowStructureConfig config) =>
        new(
            LowStructureAlignmentRoute.ShapeSeed,
            (proposals.Count > 0
                ? proposals
                .Where(proposal => IsUsable(proposal.Scale))
                .Select(proposal => proposal.Scale)
                .DistinctBy(scale => Math.Round(scale, 6))
                .Take(Math.Clamp(config.MaximumScalesPerFrame, 1, 3))
                .ToArray()
                : [Math.Clamp(
                    Math.Sqrt(Math.Max(config.MinimumScale, 0.05d)
                        * Math.Max(config.MinimumScale, config.MaximumScale)),
                    Math.Max(config.MinimumScale, 0.05d),
                    Math.Max(config.MinimumScale, config.MaximumScale))]),
            Math.Clamp(config.TranslationTopK, 1, 2),
            Math.Clamp(config.ColdPathBudgetMilliseconds, 50, 700),
            evidence.AxesAgree(config.ScaleConsistencyTolerance),
            RecoveryBatch: 0,
            RecoveryTotalScaleCount: 0);

    public static LowStructureAlignmentPlan SparseCoarseSeed(
        IReadOnlyList<double> rankedScales,
        LowStructureConfig config,
        double scaleResolutionRatio = 0d,
        int scaleBasinCount = 0,
        bool ambiguous = false)
    {
        var selection = LowStructureScaleSelectionContext.Current;
        if (selection is not null
            && selection.Scales.SequenceEqual(rankedScales))
        {
            scaleResolutionRatio = selection.RelativeResolution;
            scaleBasinCount = selection.BasinCount;
            ambiguous = selection.Ambiguous;
        }
        return
        new(
            LowStructureAlignmentRoute.SparseCoarseSeed,
            rankedScales
                .Where(IsUsable)
                .DistinctBy(scale => Math.Round(scale, 6))
                .Take(Math.Clamp(config.MaximumScalesPerFrame, 1, 3))
                .ToArray(),
            Math.Clamp(config.TranslationTopK, 1, 2),
            Math.Clamp(config.ColdPathBudgetMilliseconds, 50, 700),
            // The coarse selector is deliberately cheap and can rank an
            // oversized partial overlap first. Exact evaluation must compare
            // all selected scale basins before any transform is accepted.
            CanDirectAccept: false,
            RecoveryBatch: 0,
            RecoveryTotalScaleCount: rankedScales.Count,
            ScaleResolutionRatio: scaleResolutionRatio,
            ScaleBasinCount: scaleBasinCount,
            ScaleSelectionAmbiguous: ambiguous);
    }

    public static LowStructureAlignmentPlan IncrementalRecovery(
        IReadOnlyList<double> recoveryGrid,
        int batch,
        LowStructureConfig config) =>
        new(
            LowStructureAlignmentRoute.IncrementalRecovery,
            recoveryGrid
                .Where(IsUsable)
                .Take(Math.Clamp(config.MaximumScalesPerFrame, 1, 3))
                .ToArray(),
            Math.Clamp(config.TranslationTopK, 1, 2),
            Math.Clamp(config.ColdPathBudgetMilliseconds, 50, 700),
            CanDirectAccept: false,
            RecoveryBatch: Math.Max(0, batch),
            RecoveryTotalScaleCount: recoveryGrid.Count);

    private static bool IsUsable(double scale) =>
        double.IsFinite(scale) && scale > 0.05d;
}

internal static class LowStructureScaleProposalBuilder
{
    public static LowStructureShapeScaleEvidence FromFeatures(
        MapStructureFeatures live,
        MapStructureFeatures reference)
    {
        var livePoints = MapStructureScaleSearch.FindNonZeroPoints(live.StructureMask);
        var referencePoints = MapStructureScaleSearch.FindNonZeroPoints(reference.StructureMask);
        if (livePoints.Length == 0 || referencePoints.Length == 0)
            return new(0d, 0d, 0d, 0d, 0d, 0d);
        var liveBounds = Cv2.BoundingRect(livePoints);
        var referenceBounds = Cv2.BoundingRect(referencePoints);
        var widthScale = liveBounds.Width / (double)Math.Max(1, referenceBounds.Width);
        var heightScale = liveBounds.Height / (double)Math.Max(1, referenceBounds.Height);
        var componentScale = ResolveComponentScale(
            live,
            reference,
            widthScale,
            heightScale);
        var lineSpacingScale = ResolveLineSpacingScale(
            live.StructureMask,
            reference.StructureMask,
            componentScale);
        return new(
            widthScale,
            heightScale,
            componentScale,
            lineSpacingScale,
            ProjectionCorrelation(live.StructureMask, reference.StructureMask, true),
            ProjectionCorrelation(live.StructureMask, reference.StructureMask, false));
    }

    public static IReadOnlyList<LowStructureScaleProposal> Cluster(
        IEnumerable<(double Scale, double Weight, bool IsAxisEvidence)> votes,
        double minimumScale,
        double maximumScale,
        double tolerance,
        int maximumProposals = 3)
    {
        ArgumentNullException.ThrowIfNull(votes);
        var valid = votes
            .Where(v => double.IsFinite(v.Scale)
                && v.Scale >= minimumScale
                && v.Scale <= maximumScale
                && double.IsFinite(v.Weight)
                && v.Weight > 0d)
            .Select(v => v with { Weight = Math.Max(0.01d, v.Weight) })
            .ToArray();
        if (valid.Length == 0)
            return [];

        var relativeTolerance = Math.Max(0.0001d, tolerance);
        var clusters = new List<List<(double Scale, double Weight, bool IsAxisEvidence)>>();
        foreach (var vote in valid.OrderBy(v => v.Scale))
        {
            var current = clusters.LastOrDefault();
            var center = current is null
                ? double.NaN
                : current.Sum(item => item.Scale * item.Weight)
                    / current.Sum(item => item.Weight);
            if (current is null
                || Math.Abs(vote.Scale - center) / Math.Max(center, 0.05d)
                    > relativeTolerance)
            {
                clusters.Add([vote]);
                continue;
            }
            current.Add(vote);
        }
        return clusters
            .Select(ToProposal)
            .OrderByDescending(proposal => proposal.Score)
            .ThenByDescending(proposal => proposal.Votes)
            .ThenBy(proposal => proposal.Scale)
            .Take(Math.Clamp(maximumProposals, 1, 3))
            .ToArray();
    }

    private static LowStructureScaleProposal ToProposal(
        IReadOnlyCollection<(double Scale, double Weight, bool IsAxisEvidence)> cluster) =>
        new(
            cluster.Sum(v => v.Scale * v.Weight) / cluster.Sum(v => v.Weight),
            cluster.Count,
            AxisAgreement(cluster),
            ProjectionAgreement(cluster),
            cluster.Sum(v => v.Weight)
                * Math.Clamp(AxisAgreement(cluster), 0d, 1d));

    private static double AxisAgreement(
        IReadOnlyCollection<(double Scale, double Weight, bool IsAxisEvidence)> cluster)
    {
        var axisScales = cluster
            .Where(v => v.IsAxisEvidence)
            .Select(v => v.Scale)
            .ToArray();
        return axisScales.Length < 2
            ? 1d
            : Math.Clamp(1d - LowStructureShapeScaleEvidence.RelativeDifference(
                axisScales.Min(), axisScales.Max()), 0d, 1d);
    }

    private static double ResolveComponentScale(
        MapStructureFeatures live,
        MapStructureFeatures reference,
        double widthScale,
        double heightScale)
    {
        var liveTiming = live.DiagnosticTiming;
        var referenceTiming = reference.DiagnosticTiming;
        var liveWidth = liveTiming?.DominantComponentWidth > 0
            ? liveTiming.DominantComponentWidth
            : 0;
        var liveHeight = liveTiming?.DominantComponentHeight > 0
            ? liveTiming.DominantComponentHeight
            : 0;
        var referenceWidth = referenceTiming?.DominantComponentWidth > 0
            ? referenceTiming.DominantComponentWidth
            : 0;
        var referenceHeight = referenceTiming?.DominantComponentHeight > 0
            ? referenceTiming.DominantComponentHeight
            : 0;
        if (liveWidth <= 0 || liveHeight <= 0 || referenceWidth <= 0 || referenceHeight <= 0)
            return Math.Sqrt(widthScale * heightScale);
        return GeometricMean(
            liveWidth / (double)referenceWidth,
            liveHeight / (double)referenceHeight,
            Math.Sqrt(widthScale * heightScale));
    }

    private static double ResolveLineSpacingScale(
        Mat live,
        Mat reference,
        double fallback)
    {
        var liveX = EstimateActiveSpacing(live, horizontal: true);
        var liveY = EstimateActiveSpacing(live, horizontal: false);
        var referenceX = EstimateActiveSpacing(reference, horizontal: true);
        var referenceY = EstimateActiveSpacing(reference, horizontal: false);
        var ratios = new[]
        {
            Ratio(liveX, referenceX),
            Ratio(liveY, referenceY)
        }.Where(double.IsFinite).ToArray();
        return ratios.Length == 0 ? fallback : ratios.Average();
    }

    private static double EstimateActiveSpacing(Mat binary, bool horizontal)
    {
        var projection = Projection(binary, horizontal);
        var maximum = projection.Length == 0 ? 0d : projection.Max();
        if (maximum <= 0d)
            return double.NaN;
        var active = projection
            .Select((value, index) => (value, index))
            .Where(item => item.value >= maximum * 0.45d)
            .Select(item => item.index)
            .ToArray();
        var gaps = active
            .Zip(active.Skip(1), (first, second) => second - first)
            .Where(gap => gap > 1)
            .ToArray();
        return gaps.Length == 0 ? double.NaN : gaps.OrderBy(gap => gap).ElementAt(gaps.Length / 2);
    }

    private static double ProjectionCorrelation(Mat first, Mat second, bool horizontal)
    {
        var a = Resample(Projection(first, horizontal), 32);
        var b = Resample(Projection(second, horizontal), 32);
        if (a.Length == 0 || b.Length == 0)
            return 0d;
        var meanA = a.Average();
        var meanB = b.Average();
        var numerator = 0d;
        var varianceA = 0d;
        var varianceB = 0d;
        for (var index = 0; index < a.Length; index++)
        {
            var da = a[index] - meanA;
            var db = b[index] - meanB;
            numerator += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }
        return varianceA <= 1e-9d || varianceB <= 1e-9d
            ? 0d
            : Math.Clamp((numerator / Math.Sqrt(varianceA * varianceB) + 1d) / 2d, 0d, 1d);
    }

    private static double[] Projection(Mat binary, bool horizontal)
    {
        var length = horizontal ? binary.Width : binary.Height;
        var height = binary.Height;
        var width = binary.Width;
        var values = new double[length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (binary.At<byte>(y, x) == 0)
                continue;
            values[horizontal ? x : y]++;
        }
        return values;
    }

    private static double[] Resample(IReadOnlyList<double> source, int length)
    {
        if (source.Count == 0)
            return [];
        return Enumerable.Range(0, length)
            .Select(index => source[(int)Math.Min(
                source.Count - 1,
                Math.Floor(index * source.Count / (double)length))])
            .ToArray();
    }

    private static double GeometricMean(double first, double second, double fallback)
    {
        var result = Math.Sqrt(first * second);
        return double.IsFinite(result) && result > 0.05d ? result : fallback;
    }

    private static double Ratio(double numerator, double denominator) =>
        double.IsFinite(numerator) && double.IsFinite(denominator) && denominator > 0d
            ? numerator / denominator
            : double.NaN;

    public static LowStructureAlignmentPlan CreateShapeSeedPlan(
        LowStructureShapeScaleEvidence evidence,
        LowStructureConfig config)
    {
        var votes = new List<(double Scale, double Weight, bool IsAxisEvidence)>();
        Add(votes, evidence.WidthScale, 3d, true);
        Add(votes, evidence.HeightScale, 3d, true);
        Add(votes, evidence.ComponentScale, 2d, false);
        Add(votes, evidence.LineSpacingScale, 1d, false);
        var proposals = Cluster(
            votes,
            Math.Max(config.MinimumScale, 0.05d),
            Math.Max(config.MinimumScale, config.MaximumScale),
            config.ScaleConsistencyTolerance,
            config.MaximumScalesPerFrame);
        return LowStructureAlignmentPlan.ShapeSeed(proposals, evidence, config);

        static void Add(
            ICollection<(double Scale, double Weight, bool IsAxisEvidence)> target,
            double scale,
            double weight,
            bool isAxisEvidence)
        {
            if (double.IsFinite(scale) && scale > 0.05d)
                target.Add((scale, weight, isAxisEvidence));
        }
    }

    private static double ProjectionAgreement(
        IReadOnlyCollection<(double Scale, double Weight, bool IsAxisEvidence)> cluster)
    {
        var projectionVotes = cluster
            .Where(v => !v.IsAxisEvidence)
            .Sum(v => v.Weight);
        return projectionVotes > 0d
            ? Math.Clamp(projectionVotes / cluster.Sum(v => v.Weight), 0d, 1d)
            : 0d;
    }
}
