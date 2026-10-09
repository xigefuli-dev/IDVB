using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

internal enum VisibleAwareCorrelationMode { LegacyFullResolutionMat, CoarseMat, CoarseUMat }

internal readonly record struct VisibleAwareSearchDiagnostics(
    bool Ran, double SearchMilliseconds, int CandidateCount, double BestCost,
    double SecondCost, double VisibleFraction, int VisibleStructurePixels,
    int VisibleEdgePixels, string RequestedBackend = "", string ActualBackend = "",
    string? UMatFallbackReason = null, double CoarseMilliseconds = 0d,
    double RefineMilliseconds = 0d, double UploadMilliseconds = 0d,
    double DownloadMilliseconds = 0d, int CoarsePeakCount = 0,
    int RefinedCandidateCount = 0, bool BudgetSkipped = false)
{
    public static readonly VisibleAwareSearchDiagnostics Empty = new();
}

internal interface IVisibleAwareCorrelationBackend : IDisposable
{
    string Name { get; }
    double UploadMilliseconds { get; }
    double DownloadMilliseconds { get; }
    Mat Correlate(Mat reference, Mat structure, Mat visible);
}

internal sealed class MatCorrelationBackend : IVisibleAwareCorrelationBackend
{
    public string Name => "Mat";
    public double UploadMilliseconds => 0;
    public double DownloadMilliseconds => 0;
    public Mat Correlate(Mat r, Mat s, Mat v) => MapStructureVisibleAwareSearch.ComputeIoU(r, s, v);
    public void Dispose() { }
}

internal sealed class UMatCorrelationBackend : IVisibleAwareCorrelationBackend
{
    private readonly Dictionary<nint, UMat> _references = new();
    private double _upload, _download;
    public string Name => "UMat";
    public double UploadMilliseconds => _upload;
    public double DownloadMilliseconds => _download;

    public Mat Correlate(Mat reference, Mat structure, Mat visible)
    {
        if (!_references.TryGetValue(reference.CvPtr, out var referenceU))
        {
            var timer = Stopwatch.StartNew();
            referenceU = reference.GetUMat(AccessFlag.READ, UMatUsageFlags.None);
            _references.Add(reference.CvPtr, referenceU);
            timer.Stop(); _upload += timer.Elapsed.TotalMilliseconds;
        }
        using var structureU = structure.GetUMat(AccessFlag.READ, UMatUsageFlags.None);
        using var visibleU = visible.GetUMat(AccessFlag.READ, UMatUsageFlags.None);
        using var tp = new UMat(); using var refVisible = new UMat();
        Cv2.MatchTemplate(referenceU, structureU, tp, TemplateMatchModes.CCorr);
        Cv2.MatchTemplate(referenceU, visibleU, refVisible, TemplateMatchModes.CCorr);
        using var union = new UMat();
        Cv2.Add(refVisible, Cv2.Sum(structure).Val0, union);
        Cv2.Subtract(union, tp, union); Cv2.Max(union, 1d, union);
        using var iou = new UMat();
        Cv2.Divide(tp, union, iou); Cv2.Min(iou, 1d, iou); Cv2.Max(iou, 0d, iou);
        var timer2 = Stopwatch.StartNew();
        using var downloaded = iou.GetMat(AccessFlag.READ);
        var result = downloaded.Clone();
        timer2.Stop(); _download += timer2.Elapsed.TotalMilliseconds;
        return result;
    }
    public void Dispose() { foreach (var item in _references.Values) item.Dispose(); _references.Clear(); }
}

internal sealed class VisibleAwareCorrelationSession : IDisposable
{
    private static int _umatUnavailable;
    private IVisibleAwareCorrelationBackend _backend;
    public VisibleAwareCorrelationSession(VisibleAwareCorrelationMode mode)
    {
        RequestedMode = mode;
        _backend = mode == VisibleAwareCorrelationMode.CoarseUMat && Volatile.Read(ref _umatUnavailable) == 0
            ? new UMatCorrelationBackend() : new MatCorrelationBackend();
        if (mode == VisibleAwareCorrelationMode.CoarseUMat && _backend is MatCorrelationBackend)
            FallbackReason = "UMat disabled after an earlier process failure";
    }
    public VisibleAwareCorrelationMode RequestedMode { get; }
    public string RequestedBackend => RequestedMode == VisibleAwareCorrelationMode.CoarseUMat ? "UMat" : "Mat";
    public string ActualBackend => _backend.Name;
    public string? FallbackReason { get; private set; }
    public double UploadMilliseconds => _backend.UploadMilliseconds;
    public double DownloadMilliseconds => _backend.DownloadMilliseconds;
    internal PreparedMatCorrelation? PrepareRefinement(
        QueryGeometry query,
        Mat structure,
        Mat visible,
        int factor,
        int normalizedVisibleMaskErodePixels)
    {
        if (factor <= 1 || RequestedMode != VisibleAwareCorrelationMode.CoarseMat
            || _backend is not MatCorrelationBackend) return null;
        return PreparedMatCorrelation.TryCreate(
            this,
            query,
            structure,
            visible,
            factor,
            normalizedVisibleMaskErodePixels,
            RequestedMode,
            ActualBackend,
            out _);
    }
    public Mat Correlate(Mat reference, Mat structure, Mat visible,
        PreparedMatCorrelation? prepared = null)
    {
        try
        {
            using var preparedAttempt = _backend is MatCorrelationBackend && prepared is not null
                ? MapOperationTraceAmbient.StartChild(
                    "visible_aware_prepared_correlation", MapOperationWaitKind.Compute,
                    route: $"query={structure.Width}x{structure.Height}/reference={reference.Width}x{reference.Height}")
                : null;
            var preparedRoute = PreparedMatCorrelationRoute.UnsupportedReference;
            Mat? preparedResponse = null;
            var preparedUsed = _backend is MatCorrelationBackend && prepared is not null
                && prepared.TryCorrelate(this, reference, structure, visible,
                    out preparedResponse, out preparedRoute, out _);
            var response = preparedUsed
                ? preparedResponse!
                : _backend.Correlate(reference, structure, visible);
            preparedAttempt?.Complete(terminalReason: preparedRoute.ToString());
            if (response.Empty() || response.Width != reference.Width - structure.Width + 1
                || response.Height != reference.Height - structure.Height + 1 || !Cv2.CheckRange(response))
            { response.Dispose(); throw new InvalidOperationException("Invalid correlation response"); }
            return response;
        }
        catch (Exception ex) when (_backend is UMatCorrelationBackend)
        {
            FallbackReason = $"{ex.GetType().Name}: {ex.Message}";
            Interlocked.Exchange(ref _umatUnavailable, 1);
            _backend.Dispose(); _backend = new MatCorrelationBackend();
            return _backend.Correlate(reference, structure, visible);
        }
    }
    internal static void ResetStickyFallbackForTests() => Interlocked.Exchange(ref _umatUnavailable, 0);
    internal static void WarmUpUMat()
    {
        using var reference = Mat.Ones(8, 8, MatType.CV_32FC1);
        using var template = Mat.Ones(3, 3, MatType.CV_32FC1);
        using var session = new VisibleAwareCorrelationSession(VisibleAwareCorrelationMode.CoarseUMat);
        using var response = session.Correlate(reference, template, template);
    }
    public void Dispose() => _backend.Dispose();
}

internal static class MapStructureVisibleAwareSearch
{
    internal static Mat ComputeIoU(Mat reference, Mat structure, Mat visible)
    {
        using var tp = new Mat(); using var refVisible = new Mat();
        using (MapOperationTraceAmbient.StartChild(
            "visible_aware_structure_ccorr", MapOperationWaitKind.Compute))
            Cv2.MatchTemplate(reference, structure, tp, TemplateMatchModes.CCorr);
        using (MapOperationTraceAmbient.StartChild(
            "visible_aware_visibility_ccorr", MapOperationWaitKind.Compute))
            Cv2.MatchTemplate(reference, visible, refVisible, TemplateMatchModes.CCorr);
        using var union = new Mat();
        Cv2.Add(refVisible, Cv2.Sum(structure).Val0, union);
        Cv2.Subtract(union, tp, union); Cv2.Max(union, 1d, union);
        var result = new Mat(); Cv2.Divide(tp, union, result);
        Cv2.Min(result, 1d, result); Cv2.Max(result, 0d, result);
        return result;
    }

    internal static VisibleAwareSearchDiagnostics CollectVisibleAwareCandidates(
        QueryGeometry query, MapStructureFeatures reference, Mat referenceDistance,
        MapStructureRegistrationRequest request, double scale, MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        MapStructureScaleSearch.ScaleSearchContext context, List<MapStructureCandidate> candidates)
    {
        if ((!tuning.EnableVisibleAwareShadow && !tuning.EnableVisibleAwareInjection)
            || query.VisibleMask is null || query.VisibleMask.Empty()) return VisibleAwareSearchDiagnostics.Empty;
        if (tuning.EnforceTimeBudget
            && context.VisibleAwareTotalMs >= tuning.VisibleAwareSearchBudgetMilliseconds)
            return new(false, 0, 0, double.PositiveInfinity, double.PositiveInfinity, 0, 0, 0,
                BudgetSkipped: true);
        var totalVisible = Cv2.CountNonZero(query.VisibleMask);
        var visibleFraction = (double)totalVisible / (query.VisibleMask.Width * query.VisibleMask.Height);
        if (visibleFraction < tuning.VisibleAwareMinimumVisibleFraction) return VisibleAwareSearchDiagnostics.Empty;
        using var visible8 = new Mat(query.VisibleMask, query.Bounds);
        var normalizedVisibleMaskErodePixels =
            Math.Clamp(tuning.SafeVisibleMaskErodePixels, 0, 3);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect,
            new Size(1 + normalizedVisibleMaskErodePixels * 2,
                1 + normalizedVisibleMaskErodePixels * 2));
        using var safe8 = new Mat(); Cv2.Erode(visible8, safe8, kernel);
        using var structure8 = new Mat(query.Structure, query.Bounds);
        using var edges8 = new Mat(query.Edges, query.Bounds);
        using var visibleStructure8 = new Mat(); Cv2.BitwiseAnd(structure8, safe8, visibleStructure8);
        var structurePixels = Cv2.CountNonZero(visibleStructure8);
        if (structurePixels < tuning.VisibleAwareMinimumVisibleStructurePixels) return VisibleAwareSearchDiagnostics.Empty;
        using var visibleEdges = new Mat(); Cv2.BitwiseAnd(edges8, safe8, visibleEdges);
        using var structure = ToFloat(visibleStructure8); using var safe = ToFloat(safe8);
        context.VisibleAwareSession ??= new VisibleAwareCorrelationSession(tuning.VisibleAwareCorrelationMode);
        var session = context.VisibleAwareSession;
        var reference8 = reciprocalScale.StructureMask ?? reference.StructureMask;
        var factor = tuning.VisibleAwareCorrelationMode == VisibleAwareCorrelationMode.LegacyFullResolutionMat
            ? 1 : SelectFactor(tuning.VisibleAwareCoarseDownsample, reference8.Size(), structure.Size());
        if (factor == 0) return VisibleAwareSearchDiagnostics.Empty;

        var coarseTimer = Stopwatch.StartNew();
        using var coarseCorrelation = MapOperationTraceAmbient.StartChild(
            "visible_aware_coarse_correlation", MapOperationWaitKind.Compute,
            route: $"requested={session.RequestedBackend}/actual={session.ActualBackend}"
                + $"/factor={factor}/query={structure.Width}x{structure.Height}"
                + $"/reference={reference8.Width}x{reference8.Height}"
                + $"/scale={scale:R}/bounds={query.Bounds.X},{query.Bounds.Y}");
        if (reciprocalScale.StructureMask is not null
            && (context.VisibleAwareReciprocalReference is null
                || context.VisibleAwareReciprocalFactor != factor))
        {
            context.VisibleAwareReciprocalReference?.Dispose();
            using var reciprocalFloat = ToFloat(reference8);
            context.VisibleAwareReciprocalReference = ResizeArea(reciprocalFloat, factor);
            context.VisibleAwareReciprocalFactor = factor;
        }
        var referenceCoarse = context.VisibleAwareReciprocalReference
            ?? reference.GetOrCreateUnitStructureMask(factor);
        using var structureCoarse = ResizeArea(structure, factor); using var safeCoarse = ResizeArea(safe, factor);
        using var coarseResponse = session.Correlate(referenceCoarse, structureCoarse, safeCoarse);
        if (request.RestrictSearchToLockedTransform)
        {
            // The visible-aware correlator is also used by the fixed-scale
            // tracking route. Keep its response inside the same translation
            // window as restricted template search; it must not silently turn
            // a tracking request into a global search.
            var scoreDomain = new Size(
                Math.Max(1, reference8.Width - structure.Width + 1),
                Math.Max(1, reference8.Height - structure.Height + 1));
            var expected = MapStructureScaleSearch.ExpectedReferenceLocation(
                request, scale, query.Bounds);
            var radiusInReferencePixels = Math.Max(
                tuning.MinimumSpanPixels,
                (int)Math.Ceiling(
                    (request.TrackingMode
                        ? tuning.TrackingSearchRadiusPixels
                        : tuning.PreviousAlignmentSearchRadiusPixels)
                    / Math.Max(0.0001d, scale)));
            var domain = MapStructureScaleSearch.CenteredSearchRect(
                scoreDomain, expected.X, expected.Y, radiusInReferencePixels);
            var left = Math.Clamp(
                (int)Math.Floor(domain.X / (double)factor),
                0,
                coarseResponse.Width);
            var top = Math.Clamp(
                (int)Math.Floor(domain.Y / (double)factor),
                0,
                coarseResponse.Height);
            var right = Math.Clamp(
                (int)Math.Ceiling(domain.Right / (double)factor),
                left,
                coarseResponse.Width);
            var bottom = Math.Clamp(
                (int)Math.Ceiling(domain.Bottom / (double)factor),
                top,
                coarseResponse.Height);
            if (left > 0)
                Cv2.Rectangle(coarseResponse,
                    new Rect(0, 0, left, coarseResponse.Height),
                    Scalar.All(0), -1);
            if (top > 0)
                Cv2.Rectangle(coarseResponse,
                    new Rect(0, 0, coarseResponse.Width, top),
                    Scalar.All(0), -1);
            if (right < coarseResponse.Width)
                Cv2.Rectangle(coarseResponse,
                    new Rect(right, 0, coarseResponse.Width - right,
                        coarseResponse.Height),
                    Scalar.All(0), -1);
            if (bottom < coarseResponse.Height)
                Cv2.Rectangle(coarseResponse,
                    new Rect(0, bottom, coarseResponse.Width,
                        coarseResponse.Height - bottom),
                    Scalar.All(0), -1);
        }
        var peaks = Peaks(coarseResponse, tuning.VisibleAwareTopK); coarseTimer.Stop();
        coarseCorrelation.Complete();
        var refineTimer = Stopwatch.StartNew();
        using var correlationRefinement = MapOperationTraceAmbient.StartChild(
            "visible_aware_correlation_refinement", MapOperationWaitKind.Compute);
        var refined = new List<(int X, int Y, double Score)>();
        using (var prepared = session.PrepareRefinement(
            query,
            structure,
            safe,
            factor,
            normalizedVisibleMaskErodePixels))
        foreach (var peak in peaks)
        {
            if (tuning.EnforceTimeBudget
                && context.VisibleAwareTotalMs + coarseTimer.Elapsed.TotalMilliseconds
                    + refineTimer.Elapsed.TotalMilliseconds >= tuning.VisibleAwareSearchBudgetMilliseconds)
                break;
            if (factor == 1) { refined.Add(peak); continue; }
            var cx = peak.X * factor; var cy = peak.Y * factor; var radius = 2 * factor;
            var left = Math.Max(0, cx - radius); var top = Math.Max(0, cy - radius);
            var right = Math.Min(reference8.Width - structure.Width, cx + radius);
            var bottom = Math.Min(reference8.Height - structure.Height, cy + radius);
            if (right < left || bottom < top) continue;
            using var refinementPeak = MapOperationTraceAmbient.StartChild(
                "visible_aware_refinement_peak", MapOperationWaitKind.Compute,
                route: $"requested={session.RequestedBackend}/actual={session.ActualBackend}"
                    + $"/factor={factor}/query={structure.Width}x{structure.Height}"
                    + $"/reference={structure.Width + right - left}x{structure.Height + bottom - top}"
                    + $"/origin={left},{top}/peak={peak.X},{peak.Y}"
                    + $"/scale={scale:R}/bounds={query.Bounds.X},{query.Bounds.Y}",
                attemptIndex: refined.Count + 1);
            using var roi8 = new Mat(reference8, new Rect(left, top,
                structure.Width + right - left, structure.Height + bottom - top));
            using var conversion = MapOperationTraceAmbient.StartChild(
                "visible_aware_roi_conversion", MapOperationWaitKind.Compute);
            using var roi = ToFloat(roi8);
            conversion.Complete();
            using var correlation = MapOperationTraceAmbient.StartChild(
                "visible_aware_roi_correlation", MapOperationWaitKind.Compute);
            using var response = session.Correlate(roi, structure, safe, prepared);
            correlation.Complete();
            Cv2.MinMaxLoc(response, out _, out var max, out _, out var location);
            refined.Add((left + location.X, top + location.Y, max));
        }
        refineTimer.Stop();
        correlationRefinement.Complete();
        using var candidateEvaluation = MapOperationTraceAmbient.StartChild(
            "visible_aware_candidate_evaluation", MapOperationWaitKind.Compute);
        var costs = new List<double>();
        foreach (var peak in refined.DistinctBy(p => (p.X, p.Y)))
        {
            if (peak.X < 0 || peak.Y < 0 || peak.X + query.Bounds.Width > referenceDistance.Width
                || peak.Y + query.Bounds.Height > referenceDistance.Height) continue;
            var evaluated = MapStructureEvaluator.Evaluate(query, reference, referenceDistance, request,
                scale, peak.X, peak.Y, true, tuning, reciprocalScale);
            costs.Add(evaluated.CompositeCost);
            if (tuning.EnableVisibleAwareInjection) candidates.Add(evaluated with { FromVisibleAware = true,
                VisibleFraction = visibleFraction, VisibleStructurePixels = structurePixels,
                VisibleEdgePixels = Cv2.CountNonZero(visibleEdges) });
        }
        costs.Sort();
        candidateEvaluation.Complete();
        return new(true, coarseTimer.Elapsed.TotalMilliseconds + refineTimer.Elapsed.TotalMilliseconds,
            refined.Count, costs.Count > 0 ? costs[0] : double.PositiveInfinity,
            costs.Count > 1 ? costs[1] : double.PositiveInfinity, visibleFraction, structurePixels,
            Cv2.CountNonZero(visibleEdges), session.RequestedBackend, session.ActualBackend,
            session.FallbackReason, coarseTimer.Elapsed.TotalMilliseconds, refineTimer.Elapsed.TotalMilliseconds,
            session.UploadMilliseconds, session.DownloadMilliseconds, peaks.Count, refined.Count);
    }

    private static int SelectFactor(int preferred, Size reference, Size template)
    {
        foreach (var factor in new[] { preferred, 2, 1 }.Distinct().Where(x => x >= 1))
            if (template.Width / factor < reference.Width / factor && template.Height / factor < reference.Height / factor)
                return factor;
        return 0;
    }
    private static Mat ToFloat(Mat source) { var r = new Mat(); source.ConvertTo(r, MatType.CV_32FC1, 1d / 255d); return r; }
    private static Mat ResizeArea(Mat source, int factor)
    {
        if (factor == 1) return source.Clone(); var r = new Mat();
        Cv2.Resize(source, r, new Size(Math.Max(1, source.Width / factor), Math.Max(1, source.Height / factor)),
            0, 0, InterpolationFlags.Area); return r;
    }
    private static List<(int X, int Y, double Score)> Peaks(Mat response, int count)
    {
        var result = new List<(int, int, double)>(); using var scores = response.Clone();
        var radius = Math.Max(4, Math.Min(response.Width, response.Height) / 8);
        for (var i = 0; i < count; i++) { Cv2.MinMaxLoc(scores, out _, out var max, out _, out var at);
            if (max <= 0 || !double.IsFinite(max)) break; result.Add((at.X, at.Y, max));
            Cv2.Circle(scores, at, radius, Scalar.All(0), -1); }
        return result;
    }
}
