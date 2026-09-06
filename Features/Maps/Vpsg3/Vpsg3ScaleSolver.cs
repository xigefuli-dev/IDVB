using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// A single local-maximum pitch peak detected on the live 1D structural projection.
/// Correlation is the positive autocorrelation value at the peak; PeakRatio is the
/// peak relative to the median absolute autocorrelation across all tested lags.
/// </summary>
public readonly record struct Vpsg3PitchCandidate(
    double Pitch,
    double Correlation,
    double PeakRatio);

/// <summary>
/// Kind of relation between a candidate pitch and the primary (highest-correlation) pitch.
/// Only candidates that are genuine harmonics of the primary — or nearly as strong as it —
/// are promoted to scale hypotheses; unrelated weak noise peaks are not.
/// </summary>
public enum Vpsg3HarmonicRelation
{
    /// <summary>No secondary pitch in play.</summary>
    None = 0,

    /// <summary>The primary (highest-correlation) pitch itself.</summary>
    Primary = 1,

    /// <summary>pitch is an integer multiple of the primary (×2, ×3, ×4, …).</summary>
    OctaveAbove = 2,

    /// <summary>pitch is a rational sub-multiple of the primary (×1/2, ×1/3, ×1/4, …).</summary>
    SubOctaveBelow = 3,

    /// <summary>pitch is a rational non-dyadic harmonic of the primary (×1.5, ×4/3, ×3/4, ×2/3, …).</summary>
    FractionalHarmonic = 4,

    /// <summary>pitch is unrelated to the primary but correlates almost as strongly (≥0.85× primary corr).</summary>
    CloseCompetitor = 5
}

/// <summary>
/// One scale to evaluate during bootstrap arbitration. SeedScale = Pitch / ReferencePitch.
/// Relation describes how this hypothesis relates to the primary scale hypothesis;
/// PeakRatio is the effective peak ratio (min of live &amp; reference) fed to the verification gate.
/// </summary>
public readonly record struct Vpsg3ScaleHypothesis(
    double SeedScale,
    double Pitch,
    double Correlation,
    Vpsg3HarmonicRelation Relation,
    double RatioToPrimaryPitch,
    double PeakRatio);

/// <summary>
/// Zero-allocation (success-path) output of the S-B scale stage, produced by
/// <see cref="Vpsg3ScaleSolver.BuildScaleStage"/>. Candidate peaks live in
/// <see cref="Vpsg3SolverScratch.PitchCandidateBuffer"/>, hypotheses in
/// <see cref="Vpsg3SolverScratch.ScaleHypothesisBuffer"/>; this struct carries the counts
/// plus the primary (dominant) scale prior. The scale stage is a *proposal* stage — final
/// scale choice is left to the 2D registration + verification gate, which is why ambiguous
/// harmonic families surface as multiple hypotheses instead of a single forced decision.
/// </summary>
public readonly record struct Vpsg3ScaleStageResult(
    Vpsg3ScaleStatus Status,
    string RejectReason,
    double ReferencePitch,
    double PrimaryScale,
    double PrimaryPeakRatio,
    int CandidateCount,
    int HypothesisCount)
{
    public bool Success => Status == Vpsg3ScaleStatus.Success;

    public static Vpsg3ScaleStageResult Failed(
        Vpsg3ScaleStatus status, string reason, double referencePitch = 0d) =>
        new(status, reason, referencePitch, 1.0d, 0d, 0, 0);
}

/// <summary>
/// Full, allocation-friendly projection of the S-B scale stage (for diagnostics and tests).
/// Backed by the same zero-allocation core as the production hot path.
/// </summary>
public sealed class Vpsg3ScalePlan
{
    public Vpsg3ScaleStatus Status { get; }
    public string RejectReason { get; }
    public double ReferencePitch { get; }

    /// <summary>Live pitch candidates (positive local maxima), sorted by correlation descending.</summary>
    public IReadOnlyList<Vpsg3PitchCandidate> Candidates { get; }

    /// <summary>
    /// Ordered scale hypotheses to arbitrate. Exactly one element in the clean single-peak
    /// case (fast path); up to 3 only when a genuine harmonic family / close competitor exists.
    /// </summary>
    public IReadOnlyList<Vpsg3ScaleHypothesis> Hypotheses { get; }

    /// <summary>Seed scale of the primary (highest-correlation) hypothesis.</summary>
    public double PrimaryScale { get; }

    /// <summary>Effective peak ratio of the primary hypothesis (min of live &amp; reference ratios).</summary>
    public double PrimaryPeakRatio { get; }

    public bool HasUsablePrimary { get; }

    internal Vpsg3ScalePlan(
        Vpsg3ScaleStatus status,
        string rejectReason,
        double referencePitch,
        double primaryScale,
        double primaryPeakRatio,
        bool hasUsablePrimary,
        Vpsg3PitchCandidate[]? candidates = null,
        Vpsg3ScaleHypothesis[]? hypotheses = null)
    {
        Status = status;
        RejectReason = rejectReason;
        ReferencePitch = referencePitch;
        PrimaryScale = primaryScale;
        PrimaryPeakRatio = primaryPeakRatio;
        HasUsablePrimary = hasUsablePrimary;
        Candidates = candidates ?? [];
        Hypotheses = hypotheses ?? [];
    }

    public static Vpsg3ScalePlan Failed(Vpsg3ScaleStatus status, string reason, double referencePitch = 0d) =>
        new(status, reason, referencePitch, 1.0d, 0d, false);
}

/// <summary>
/// Production scale solver for VPSG 3.0 (Method S-B).
/// Estimates seed scale priors from 1D structural projections and autocorrelation,
/// and — critically — detects the harmonic ambiguity (≈×2 / ≈×0.5 / ≈×0.25 / ≈×0.75 / ≈×1.5 …)
/// that map wall / room structure systematically induces in live projections. Instead of
/// committing to a single dominant pitch, it emits a small ordered set of scale hypotheses
/// for the 2D registration stages to arbitrate. Operates against resident
/// PreparedFloor.ScalePrior without opening reference images or clamping.
/// </summary>
public static class Vpsg3ScaleSolver
{
    private const double Epsilon = 1e-9d;
    private const int MinAutocorrLag = 12;
    private const int MaxScaleHypotheses = 3;

    /// <summary>Tolerance for a candidate pitch to be treated as a dyadic octave (×2 / ×0.5).</summary>
    private const double DyadicHarmonicTolerance = 0.10d;

    /// <summary>Tolerance for non-dyadic rational harmonics (×1.5, ×4/3, ×3/4, ×3, ×4, …).</summary>
    private const double FractionalHarmonicTolerance = 0.12d;

    /// <summary>Correlation fraction (of the primary) above which an unrelated peak is promoted as a close competitor.</summary>
    private const double CloseCompetitorCorrFraction = 0.85d;

    /// <summary>
    /// Minimum correlation a secondary candidate must carry (relative to the primary) to be a genuine
    /// harmonic contestant. Peaks far below the primary in correlation are noise lobes and must not be
    /// promoted even when their pitch happens to sit near a harmonic ratio.
    /// </summary>
    private const double MinHarmonicCorrFraction = 0.35d;

    /// <summary>Minimum positive autocorrelation a peak must clear to be retained at all.</summary>
    private const double MinPeakCorrelation = 0.05d;

    /// <summary>
    /// Solves for the dominant seed scale prior given live observation and prepared floor index.
    /// Thin compatibility wrapper around <see cref="BuildScaleStage"/> returning only the primary
    /// (highest-correlation) hypothesis — mirrors the legacy single-pitch contract exactly.
    /// </summary>
    public static Vpsg3ScaleResult Solve(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor preparedFloor,
        Vpsg3TuningConfig? config = null,
        Vpsg3SolverScratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(preparedFloor);

        var stage = BuildScaleStage(observation, preparedFloor, config, scratch);
        if (!stage.Success)
        {
            return Vpsg3ScaleResult.Failed(stage.Status, stage.RejectReason, stage.PrimaryPeakRatio);
        }

        return new Vpsg3ScaleResult(
            Vpsg3ScaleStatus.Success,
            stage.PrimaryScale,
            stage.PrimaryPeakRatio,
            0,
            string.Empty);
    }

    /// <summary>
    /// Computes the S-B scale proposal and writes the results into the supplied scratch:
    /// positive-local-maximum pitch candidates into <see cref="Vpsg3SolverScratch.PitchCandidateBuffer"/>
    /// and the ordered scale hypotheses into <see cref="Vpsg3SolverScratch.ScaleHypothesisBuffer"/>.
    /// Zero managed allocations on the success path.
    /// </summary>
    public static Vpsg3ScaleStageResult BuildScaleStage(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor preparedFloor,
        Vpsg3TuningConfig? config = null,
        Vpsg3SolverScratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(preparedFloor);

        var cfg = config ?? Vpsg3TuningConfig.Default;
        var sc = scratch ?? Vpsg3SolverScratch.Current;

        var refPrior = preparedFloor.ScalePrior;
        if (!refPrior.FastPathEligible || refPrior.ReferencePitch <= Epsilon)
        {
            return Vpsg3ScaleStageResult.Failed(
                Vpsg3ScaleStatus.DegenerateSignal,
                $"PreparedFloorReferenceIneligible: {refPrior.RejectReason}");
        }

        if (observation.EdgePixelCount < cfg.MinEdgePixels)
        {
            return Vpsg3ScaleStageResult.Failed(
                Vpsg3ScaleStatus.InsufficientEdgePixels,
                $"ObservationEdgePixelsBelowThreshold: {observation.EdgePixelCount} < {cfg.MinEdgePixels}");
        }

        var edges = observation.ObservedEdges;
        var width = edges.Width;
        var height = edges.Height;
        sc.EnsureScaleCapacity(width, height);

        // 1. Compute 1D projection along X axis (zero managed heap allocation).
        var buffer = sc.EdgeMaskBuffer;
        if (edges.IsContinuous())
        {
            Marshal.Copy(edges.Data, buffer, 0, width * height);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(edges.Ptr(y), buffer, y * width, width);
            }
        }

        var projX = sc.ProjectionBufferX.AsSpan(0, width);
        projX.Clear();

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                if (buffer[rowOffset + x] > 128)
                {
                    projX[x]++;
                }
            }
        }

        // 2. Extract pitch candidates (positive local maxima of the autocorrelation) in the
        //    feasible scale domain [RefPitch * MinSupportedScale, RefPitch * MaxSupportedScale].
        var refPitch = refPrior.ReferencePitch;
        var candidateCount = FindPitchCandidates(
            projX,
            sc,
            refPitch * cfg.MinSupportedScale,
            refPitch * cfg.MaxSupportedScale);
        if (candidateCount == 0)
        {
            return Vpsg3ScaleStageResult.Failed(
                Vpsg3ScaleStatus.PeakRatioBelowThreshold,
                "NoDominantPitchPeak",
                refPitch);
        }

        var candidates = sc.PitchCandidateBuffer;
        var primary = candidates[0];

        // 3. PeakRatio gating on the primary: effective peak ratio is min(live, reference).
        var primaryPeakRatio = Math.Min(primary.PeakRatio, refPrior.ReferencePeakRatio);
        if (primaryPeakRatio < cfg.PeakRatioThreshold || primary.Pitch <= 5.0d)
        {
            return Vpsg3ScaleStageResult.Failed(
                Vpsg3ScaleStatus.PeakRatioBelowThreshold,
                $"PeakRatioBelowThreshold: {primaryPeakRatio:F2} < {cfg.PeakRatioThreshold:F2} (Pitch={primary.Pitch:F1})",
                refPitch);
        }

        var primaryScale = primary.Pitch / refPitch;

        // 4. Fill the ordered scale hypotheses into the scratch buffer.
        //    Clean single-peak case → exactly one hypothesis (fast path, zero extra arbitration cost).
        //    Harmonic family present → promote dyadic / fractional harmonics of the primary plus any
        //    near-as-strong unrelated competitor, so the 2D registration can arbitrate 1× vs 2× vs 0.75× etc.
        var hypothesisCount = BuildHypotheses(
            candidates,
            candidateCount,
            refPitch,
            refPrior.ReferencePeakRatio,
            sc.ScaleHypothesisBuffer);

        return new Vpsg3ScaleStageResult(
            Vpsg3ScaleStatus.Success,
            string.Empty,
            refPitch,
            primaryScale,
            primaryPeakRatio,
            candidateCount,
            hypothesisCount);
    }

    /// <summary>
    /// Diagnostic/test convenience: runs the same zero-allocation core and snapshots the
    /// candidates and hypotheses into an allocation-friendly plan object.
    /// </summary>
    public static Vpsg3ScalePlan BuildScalePlan(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor preparedFloor,
        Vpsg3TuningConfig? config = null,
        Vpsg3SolverScratch? scratch = null)
    {
        var stage = BuildScaleStage(observation, preparedFloor, config, scratch);
        if (!stage.Success)
        {
            return Vpsg3ScalePlan.Failed(stage.Status, stage.RejectReason, stage.ReferencePitch);
        }

        var sc = scratch ?? Vpsg3SolverScratch.Current;
        var candidates = new Vpsg3PitchCandidate[stage.CandidateCount];
        for (var i = 0; i < candidates.Length; i++)
        {
            candidates[i] = sc.PitchCandidateBuffer[i];
        }

        var hypotheses = new Vpsg3ScaleHypothesis[stage.HypothesisCount];
        for (var i = 0; i < hypotheses.Length; i++)
        {
            hypotheses[i] = sc.ScaleHypothesisBuffer[i];
        }

        return new Vpsg3ScalePlan(
            Vpsg3ScaleStatus.Success,
            string.Empty,
            stage.ReferencePitch,
            stage.PrimaryScale,
            stage.PrimaryPeakRatio,
            hasUsablePrimary: true,
            candidates,
            hypotheses);
    }

    /// <summary>
    /// Detects positive local-maximum autocorrelation pitch peaks in the feasible scale domain and
    /// writes them into <see cref="Vpsg3SolverScratch.PitchCandidateBuffer"/> sorted by correlation
    /// descending. Also computes the median absolute autocorrelation over the full tested lag range,
    /// storing each candidate's PeakRatio = correlation / median. Returns the number of retained peaks.
    /// </summary>
    public static int FindPitchCandidates(
        ReadOnlySpan<double> signal,
        Vpsg3SolverScratch scratch,
        double minPitch,
        double maxPitch)
    {
        var n = signal.Length;
        if (n < 40) return 0;

        var sum = 0.0d;
        for (var i = 0; i < n; i++)
            sum += signal[i];
        var mean = sum / n;

        var centered = scratch.CenteredSignalBuffer.AsSpan(0, n);
        var variance = 0.0d;
        for (var i = 0; i < n; i++)
        {
            var diff = signal[i] - mean;
            centered[i] = diff;
            variance += diff * diff;
        }

        if (variance < Epsilon) return 0;

        var maxLag = n / 2;
        if (maxLag <= MinAutocorrLag) return 0;

        var span = maxLag - MinAutocorrLag;
        var autocorr = scratch.AutocorrBuffer.AsSpan(0, span);
        var harmonic = scratch.HarmonicCorrBuffer.AsSpan(0, span);
        var rCount = 0;

        for (var lag = MinAutocorrLag; lag < maxLag; lag++)
        {
            var dot = 0.0d;
            var limit = n - lag;
            for (var i = 0; i < limit; i++)
            {
                dot += centered[i] * centered[i + lag];
            }

            var r = dot / variance;
            autocorr[rCount] = Math.Abs(r);
            harmonic[rCount] = r;
            rCount++;
        }

        if (rCount == 0) return 0;

        // 2. Scan the feasible scale domain for positive local maxima (rightmost vertex of a plateau).
        var lo = (int)Math.Ceiling(Math.Max(minPitch, MinAutocorrLag));
        var hi = (int)Math.Floor(Math.Min(maxPitch, maxLag - 1.0d));
        var kept = 0;
        var buffer = scratch.PitchCandidateBuffer;

        for (var lag = lo; lag <= hi; lag++)
        {
            var idx = lag - MinAutocorrLag;
            var c = harmonic[idx];
            if (c <= MinPeakCorrelation) continue;

            var isPeak = (lag == lo || harmonic[idx - 1] < c)
                         && (lag == hi || c >= harmonic[idx + 1]);
            if (!isPeak) continue;

            if (kept < buffer.Length)
            {
                buffer[kept++] = new Vpsg3PitchCandidate(lag, c, 0d);
            }
        }

        if (kept == 0) return 0;

        // 3. Median absolute autocorrelation over all tested lags (identical statistic to legacy code).
        autocorr.Sort();
        var medianR = autocorr[rCount / 2];
        var medianSafe = Math.Max(0.01d, medianR);

        // 4. Fill ratios, then stable-sort retained peaks by correlation descending (insertion sort on ≤24 items).
        for (var i = 0; i < kept; i++)
        {
            buffer[i] = buffer[i] with { PeakRatio = buffer[i].Correlation / medianSafe };
        }

        for (var i = 1; i < kept; i++)
        {
            var key = buffer[i];
            var j = i - 1;
            while (j >= 0 && buffer[j].Correlation < key.Correlation)
            {
                buffer[j + 1] = buffer[j];
                j--;
            }
            buffer[j + 1] = key;
        }

        return kept;
    }

    /// <summary>
    /// Builds the ordered scale hypotheses from the correlation-sorted pitch candidates (the primary is
    /// always hypothesis 0). Up to <see cref="MaxScaleHypotheses"/> hypotheses follow when candidates are
    /// genuine harmonics of the primary or correlate almost as strongly; weak unrelated peaks are never
    /// promoted. Exposed for deterministic unit testing of the harmonic proposal logic.
    /// </summary>
    internal static int BuildHypotheses(
        Vpsg3PitchCandidate[] candidates,
        int candidateCount,
        double refPitch,
        double referencePeakRatio,
        Vpsg3ScaleHypothesis[] target)
    {
        if (candidateCount == 0 || refPitch <= Epsilon || target.Length == 0)
        {
            return 0;
        }

        var primary = candidates[0];
        var targetCount = 0;
        var primaryPeakRatio = Math.Min(primary.PeakRatio, referencePeakRatio);
        target[targetCount++] = new Vpsg3ScaleHypothesis(
            primary.Pitch / refPitch,
            primary.Pitch,
            primary.Correlation,
            Vpsg3HarmonicRelation.Primary,
            1.0d,
            primaryPeakRatio);

        for (var i = 1; i < candidateCount && targetCount < target.Length; i++)
        {
            var candidate = candidates[i];

            // A candidate far weaker than the primary is a noise lobe, never a real harmonic contest.
            if (candidate.Correlation < primary.Correlation * MinHarmonicCorrFraction) continue;

            var pitchRatio = candidate.Pitch / primary.Pitch;
            if (Math.Abs(pitchRatio - 1.0d) < 1e-6d) continue;

            var relation = ClassifyHarmonicRelation(
                pitchRatio,
                candidate.Correlation,
                primary.Correlation);
            if (relation == Vpsg3HarmonicRelation.None) continue;

            target[targetCount++] = new Vpsg3ScaleHypothesis(
                candidate.Pitch / refPitch,
                candidate.Pitch,
                candidate.Correlation,
                relation,
                pitchRatio,
                Math.Min(candidate.PeakRatio, referencePeakRatio));
        }

        return targetCount;
    }

    /// <summary>
    /// Classifies the pitch ratio of a secondary peak relative to the primary into a harmonic
    /// relation, or <see cref="Vpsg3HarmonicRelation.None"/> when the peak is an unrelated weak
    /// noise lobe that must not be promoted to a scale hypothesis.
    /// </summary>
    internal static Vpsg3HarmonicRelation ClassifyHarmonicRelation(
        double pitchRatio, double correlation, double primaryCorrelation)
    {
        // Dyadic octaves first (×2 / ×0.5) — the most common real-world harmonic ambiguity.
        if (IsNearRatio(pitchRatio, 2.0d, DyadicHarmonicTolerance)) return Vpsg3HarmonicRelation.OctaveAbove;
        if (IsNearRatio(pitchRatio, 0.5d, DyadicHarmonicTolerance)) return Vpsg3HarmonicRelation.SubOctaveBelow;

        // Integer / sub-integer multiples (×3, ×4 and their inverses) — observed as ÷4 rejections in the field.
        if (IsNearRatio(pitchRatio, 3.0d, FractionalHarmonicTolerance)
            || IsNearRatio(pitchRatio, 4.0d, FractionalHarmonicTolerance))
        {
            return Vpsg3HarmonicRelation.OctaveAbove;
        }

        if (IsNearRatio(pitchRatio, 1.0d / 3.0d, FractionalHarmonicTolerance)
            || IsNearRatio(pitchRatio, 1.0d / 4.0d, FractionalHarmonicTolerance))
        {
            return Vpsg3HarmonicRelation.SubOctaveBelow;
        }

        // Rational non-dyadic harmonics (×3/2, ×2/3, ×4/3, ×3/4, ×5/4, ×4/5, ×5/3, ×3/5) —
        // observed as ~0.75× thin-margin failures in replay.
        if (IsNearRatio(pitchRatio, 1.5d, FractionalHarmonicTolerance)
            || IsNearRatio(pitchRatio, 2.0d / 3.0d, FractionalHarmonicTolerance)
            || IsNearRatio(pitchRatio, 4.0d / 3.0d, FractionalHarmonicTolerance)
            || IsNearRatio(pitchRatio, 3.0d / 4.0d, FractionalHarmonicTolerance)
            || IsNearRatio(pitchRatio, 1.25d, FractionalHarmonicTolerance)
            || IsNearRatio(pitchRatio, 0.8d, FractionalHarmonicTolerance)
            || IsNearRatio(pitchRatio, 5.0d / 3.0d, FractionalHarmonicTolerance)
            || IsNearRatio(pitchRatio, 0.6d, FractionalHarmonicTolerance))
        {
            return Vpsg3HarmonicRelation.FractionalHarmonic;
        }

        // Unrelated but almost-as-strong: still worth arbitrating (covers non-integer mispicks).
        return correlation >= primaryCorrelation * CloseCompetitorCorrFraction
            ? Vpsg3HarmonicRelation.CloseCompetitor
            : Vpsg3HarmonicRelation.None;
    }

    private static bool IsNearRatio(double pitchRatio, double target, double tolerance) =>
        Math.Abs(pitchRatio - target) <= tolerance * target;
}
