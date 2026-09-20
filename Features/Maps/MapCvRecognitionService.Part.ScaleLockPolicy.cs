using System.Runtime.CompilerServices;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal Func<bool> ScaleLockingAllowed { get; set; } = static () => true;
    private readonly object _scalePolicyGate = new();
    private readonly Dictionary<AdaptiveScaleKey, AdaptiveScaleCoverageMilestones> _coverageMilestones = [];
    private readonly Dictionary<AdaptiveScaleKey, Point[]> _coverageReferences = [];
    private readonly ConditionalWeakTable<CapturedGameFrame, ScaleRefreshFrame> _scaleRefreshFrames = new();
    private readonly ConditionalWeakTable<CapturedGameFrame, CoverageFrame> _alignmentCoverage = new();
    private sealed record CoverageFrame(AdaptiveScaleKey Key, long Generation, int Hits, int Total,
        MapOverlayTransform Transform);
    private long _scalePolicyGeneration;
    private sealed record ScaleRefreshFrame(AdaptiveScaleKey Key, long Generation)
    {
        public bool Completed { get; set; }
    }

    internal bool CanReuseScale(CapturedGameFrame frame, Guid mapId, string floor)
    {
        if (!ScaleLockingAllowed())
            return false;
        var map = TryGetMap(mapId);
        if (map is null)
            return false;
        var key = AdaptiveScaleKey.Create(map, floor, frame.ClientBounds, frame.ViewportBounds);
        lock (_scalePolicyGate)
            return !_coverageMilestones.TryGetValue(key, out var state) || !state.RefreshPending;
    }

    private void CompleteVpsgScaleRefresh(CapturedGameFrame frame)
    {
        lock (_scalePolicyGate)
            if (_scaleRefreshFrames.TryGetValue(frame, out var value))
                value.Completed = true;
    }

    private double? ResolveVpsgScaleLock(CapturedGameFrame frame, MapRecord map, string floor, double? seed)
    {
        var key = AdaptiveScaleKey.Create(map, floor, frame.ClientBounds, frame.ViewportBounds);
        lock (_scalePolicyGate)
        {
            if (!ScaleLockingAllowed()
                || (_coverageMilestones.TryGetValue(key, out var state) && state.RefreshPending))
            {
                _scaleRefreshFrames.Remove(frame);
                _scaleRefreshFrames.Add(frame, new(key, _scalePolicyGeneration));
                return null;
            }
            return seed;
        }
    }

    internal bool WasScaleRefresh(CapturedGameFrame frame, MapRecord map, string floor)
    {
        var key = AdaptiveScaleKey.Create(map, floor, frame.ClientBounds, frame.ViewportBounds);
        lock (_scalePolicyGate)
            return _scaleRefreshFrames.TryGetValue(frame, out var value)
                && value.Completed && value.Key == key && value.Generation == _scalePolicyGeneration;
    }

    private CoverageFrame? MeasureAlignmentCoverage(CapturedGameFrame frame, RuntimeMapRecognition recognition,
        Vpsg3LiveObservation? observation = null)
    {
        if (recognition.Result.OverlayTransform is not { } transform)
            return null;
        var key = AdaptiveScaleKey.Create(recognition.Map, recognition.Result.Floor, frame.ClientBounds, frame.ViewportBounds);
        long generation;
        Point[] points;
        lock (_scalePolicyGate)
        {
            generation = _scalePolicyGeneration;
            if (_alignmentCoverage.TryGetValue(frame, out var cached)
                && cached.Key == key && cached.Generation == generation
                && ReferenceEquals(cached.Transform, transform))
                return cached;
            if (!_coverageReferences.TryGetValue(key, out points!))
            {
                // Coverage is an advisory post-alignment measurement.  A resident VPSG3 index
                // may still be valid while its on-disk source is being repaired or has changed;
                // that condition must not turn an already accepted alignment into an exception.
                if (!_repository.HasPrebuiltStructureLine(recognition.Map, recognition.Result.Floor))
                    return null;
                string path;
                try
                {
                    path = _repository.GetPrebuiltStructureLinePath(recognition.Map, recognition.Result.Floor);
                }
                catch (InvalidDataException)
                {
                    return null;
                }
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                    return null;
                using var lines = Cv2.ImRead(path, ImreadModes.Grayscale);
                if (lines.Empty())
                    return null;
                using var nonzero = new Mat();
                Cv2.FindNonZero(lines, nonzero);
                points = new Point[nonzero.Rows];
                for (var i = 0; i < points.Length; i++)
                    points[i] = nonzero.At<Point>(i);
                _coverageReferences[key] = points;
            }
        }
        if (points.Length == 0)
            return null;
        using var extracted = observation is null
            ? Vpsg3FastLiveExtractor.Extract(frame.Image, frame.ViewportBounds) : null;
        var live = observation ?? extracted!;
        var hits = AdaptiveScaleCoverageMilestones.CountCoveredPoints(
            points, live.ObservedEdges, transform, frame.ViewportBounds);
        var measured = new CoverageFrame(key, generation, hits, points.Length, transform);
        lock (_scalePolicyGate)
        {
            if (generation != _scalePolicyGeneration)
                return null;
            _alignmentCoverage.Remove(frame);
            _alignmentCoverage.Add(frame, measured);
        }
        return measured;
    }

    internal void ObserveAlignmentCoverage(CapturedGameFrame frame, RuntimeMapRecognition recognition)
    {
        var measured = MeasureAlignmentCoverage(frame, recognition);
        if (measured is null)
            return;
        var key = measured.Key;
        var coverage = (double)measured.Hits / measured.Total;
        lock (_scalePolicyGate)
        {
            if (measured.Generation != _scalePolicyGeneration)
                return;
            if (!_coverageMilestones.TryGetValue(key, out var state))
                _coverageMilestones[key] = state = new();
            var refreshed = WasScaleRefresh(frame, recognition.Map, recognition.Result.Floor);
            state.Observe(coverage, refreshed);
            _scaleRefreshFrames.Remove(frame);
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                "floor scale coverage milestone", details: new()
                {
                    ["mapId"] = key.MapId, ["floor"] = key.FloorKey,
                    ["totalCoverage"] = coverage, ["coveredReferencePoints"] = measured.Hits,
                    ["totalReferencePoints"] = measured.Total, ["milestonePercent"] = state.ReachedPercent,
                    ["refreshNextAlignment"] = state.RefreshPending, ["scaleRefreshed"] = refreshed
                });
        }
    }

    private void ResetScaleCoverage()
    {
        lock (_scalePolicyGate)
        {
            _scalePolicyGeneration++;
            _coverageMilestones.Clear();
            _coverageReferences.Clear();
            _scaleRefreshFrames.Clear();
            _alignmentCoverage.Clear();
        }
    }
}
