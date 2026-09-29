using System.Diagnostics;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace IDVBuff.Features.Maps;

/// <summary>Independent identity evidence. Transform is a measured hypothesis, never a trusted alignment.</summary>
public sealed record MapLocalFeatureIdentityCandidate
{
    public required MapRecord Map { get; init; }
    public required string FloorKey { get; init; }
    public MapOverlayTransform Transform { get; init; } = new();
    public bool HasPose { get; init; }
    public string FailureReason { get; init; } = string.Empty;
    public int RawRatioMatches { get; init; }
    public int Matches { get; init; }
    public int Inliers { get; init; }
    public int AxisAlignedInliers { get; init; }
    public double InlierRatio { get; init; }
    public double MedianError { get; init; } = 99;
    public double P90Error { get; init; } = 99;
    public double SimilarityMedianError { get; init; } = 99;
    public double SimilarityP90Error { get; init; } = 99;
    public double RotationDegrees { get; init; }
    public double HullFraction { get; init; }
    public double SpanX { get; init; }
    public double SpanY { get; init; }
    public int OccupiedTiles { get; init; }
    public double Score { get; init; } = -2.16;
}

public sealed record MapLocalFeatureIdentitySearch(
    IReadOnlyList<MapLocalFeatureIdentityCandidate> Candidates,
    bool RetrievalCompleted,
    double ElapsedMilliseconds,
    string FailureReason = "");

/// <summary>
/// Full-floor local descriptors for identity retrieval only. Every class/floor owns
/// its immutable descriptors; no scale, pose, session, or tracking state is cached.
/// The recognition service owns replacement when the map catalog changes.
/// </summary>
public sealed class MapLocalFeatureIdentityIndex : IDisposable
{
    private readonly MapRepository _repository;
    private readonly MapRecord[] _maps;
    private readonly object _gate = new();
    private readonly object _useGate = new();
    private readonly Dictionary<(string Class, string Floor), Task<ReferenceGroup>> _groups = [];
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _disposed;

    public MapLocalFeatureIdentityIndex(MapRepository repository, IReadOnlyList<MapRecord> maps)
    {
        _repository = repository;
        _maps = maps.Select(map => map.Clone()).ToArray();
    }

    public Task PrepareAsync(string mapClass, string floorKey)
    {
        var key = Key(mapClass, floorKey);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_groups.TryGetValue(key, out var task))
            {
                task = Task.Run(() => Prepare(key));
                _groups.Add(key, task);
            }
            return task;
        }
    }

    internal bool IsPreparationFinished(string mapClass, string floorKey)
    {
        lock (_gate)
            return _disposed || (_groups.TryGetValue(Key(mapClass, floorKey), out var task) && task.IsCompleted);
    }

    public MapLocalFeatureIdentitySearch Identify(CapturedGameFrame frame, string mapClass,
        string floorKey, CancellationToken cancellationToken, Func<bool> canCompute)
    {
        var timer = Stopwatch.StartNew();
        var candidates = new List<MapLocalFeatureIdentityCandidate>();
        MapLocalFeatureIdentitySearch Result(bool complete, string reason = "") => new(
            candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Map.SequenceNumber)
                .ThenBy(c => c.Map.Id).ToArray(), complete, timer.Elapsed.TotalMilliseconds, reason);
        bool CanContinue() => !_disposed && !cancellationToken.IsCancellationRequested && canCompute();
        if (!Monitor.TryEnter(_useGate)) return Result(false, "index-in-use");
        try
        {
            if (!CanContinue()) return Result(false, "cancelled-or-deadline");
            Task<ReferenceGroup>? task;
            lock (_gate) _groups.TryGetValue(Key(mapClass, floorKey), out task);
            if (task is not { IsCompletedSuccessfully: true }) return Result(false, "index-not-ready");
            var group = task.Result;
            if (frame.Image.Empty() || !frame.ViewportBounds.IsValid
                || Math.Abs(frame.Image.Width - frame.ViewportBounds.Width) > 1
                || Math.Abs(frame.Image.Height - frame.ViewportBounds.Height) > 1)
                return Result(false, "invalid-viewport");
            try
            {
                using var sift = CreateSift();
                using var query = Extract(frame.Image, sift);
                if (!CanContinue()) return Result(false, "cancelled-or-deadline");
                using var matcher = new BFMatcher(NormTypes.L2);
                foreach (var reference in group.Entries)
                {
                    if (!CanContinue()) return Result(false, "cancelled-or-deadline");
                    candidates.Add(Match(reference, query, frame.ViewportBounds, matcher, CanContinue));
                }
                return CanContinue()
                    ? Result(group.Complete && group.Entries.Length > 0, group.FailureReason)
                    : Result(false, "cancelled-or-deadline");
            }
            catch (OperationCanceledException) { return Result(false, "cancelled-or-deadline"); }
            catch (OpenCVException) { return Result(false, "feature-computation-failed"); }
        }
        finally { Monitor.Exit(_useGate); }
    }

    private ReferenceGroup Prepare((string Class, string Floor) key)
    {
        var entries = new List<ReferenceEntry>();
        var complete = true;
        var failure = string.Empty;
        try
        {
            using var sift = CreateSift(reference: true);
            foreach (var map in _maps.Where(m => string.Equals(m.Class.Trim(), key.Class,
                         StringComparison.OrdinalIgnoreCase)))
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                Features? feature = null;
                var reason = string.Empty;
                var actualFloor = key.Floor;
                try
                {
                    var floor = map.Floors.FirstOrDefault(f => string.Equals(f.Key, key.Floor,
                        StringComparison.OrdinalIgnoreCase));
                    if (floor is null) throw new InvalidDataException("missing-floor");
                    actualFloor = floor.Key;
                    var path = _repository.GetFloorRecognitionPath(map, floor.Key);
                    using var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
                    if (image.Empty() || image.Width != floor.RecognitionWidth
                        || image.Height != floor.RecognitionHeight)
                        throw new InvalidDataException("missing-or-mismatched-reference");
                    feature = Extract(image, sift, balanceReference: true);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                    or InvalidOperationException or OpenCVException)
                {
                    complete = false;
                    reason = "reference-unavailable";
                    failure = reason;
                }
                entries.Add(new ReferenceEntry(map, actualFloor, feature, reason));
            }
            _shutdown.Token.ThrowIfCancellationRequested();
            return new ReferenceGroup(entries.ToArray(), complete, failure);
        }
        catch
        {
            foreach (var entry in entries) entry.Feature?.Dispose();
            throw;
        }
    }

    private static MapLocalFeatureIdentityCandidate Match(ReferenceEntry reference, Features query,
        MapScreenRect viewport, BFMatcher matcher, Func<bool> canCompute)
    {
        var result = new MapLocalFeatureIdentityCandidate
        {
            Map = reference.Map, FloorKey = reference.Floor,
            FailureReason = reference.FailureReason
        };
        var source = reference.Feature;
        if (source is null || source.Descriptors.Rows < 2 || query.Descriptors.Rows < 2)
            return result with { FailureReason = source is null ? reference.FailureReason : "no-descriptors" };
        var forward = matcher.KnnMatch(source.Descriptors, query.Descriptors, 2)
            .Where(pair => pair.Length == 2 && pair[0].Distance < pair[1].Distance * .76)
            .Select(pair => pair[0]).ToArray();
        if (!canCompute()) throw new OperationCanceledException();
        var backward = matcher.Match(query.Descriptors, source.Descriptors)
            .ToDictionary(match => match.QueryIdx, match => match.TrainIdx);
        var seenSource = new HashSet<(int, int)>();
        var seenQuery = new HashSet<(int, int)>();
        var matches = new List<DMatch>();
        foreach (var match in forward.OrderBy(m => m.Distance))
        {
            if (!backward.TryGetValue(match.TrainIdx, out var reverse) || reverse != match.QueryIdx) continue;
            var sp = Pixel(source.Points[match.QueryIdx].Pt);
            var qp = Pixel(query.Points[match.TrainIdx].Pt);
            if (seenSource.Contains(sp) || seenQuery.Contains(qp)) continue;
            seenSource.Add(sp); seenQuery.Add(qp); matches.Add(match);
        }
        result = result with { RawRatioMatches = forward.Length, Matches = matches.Count };
        if (matches.Count < 5) return result with { FailureReason = "insufficient-matches" };
        if (!canCompute()) throw new OperationCanceledException();
        var a = matches.Select(m => OriginalPoint(source, m.QueryIdx)).ToArray();
        var b = matches.Select(m => OriginalPoint(query, m.TrainIdx)).ToArray();
        using var inputA = Mat.FromArray(a);
        using var inputB = Mat.FromArray(b);
        using var mask = new Mat();
        using var matrix = Cv2.EstimateAffinePartial2D(inputA, inputB, mask,
            RobustEstimationAlgorithms.RANSAC, 5, 2000, .995, 10);
        if (!canCompute()) throw new OperationCanceledException();
        if (matrix is null || matrix.Empty() || mask.Empty())
            return result with { FailureReason = "no-similarity-pose" };
        var indexes = Enumerable.Range(0, matches.Count)
            .Where(i => (mask.Rows == 1 ? mask.At<byte>(0, i) : mask.At<byte>(i, 0)) != 0).ToArray();
        if (indexes.Length < 2) return result with { FailureReason = "insufficient-inliers" };
        var inA = indexes.Select(i => a[i]).ToArray();
        var inB = indexes.Select(i => b[i]).ToArray();
        var aa = matrix.At<double>(0, 0); var ab = matrix.At<double>(0, 1);
        var ba = matrix.At<double>(1, 0); var bb = matrix.At<double>(1, 1);
        var tx = matrix.At<double>(0, 2); var ty = matrix.At<double>(1, 2);
        if (!new[] { aa, ab, ba, bb, tx, ty }.All(double.IsFinite))
            return result with { FailureReason = "invalid-similarity-pose" };
        var similarityErrors = indexes.Select(i => Distance(
            aa * a[i].X + ab * a[i].Y + tx - b[i].X,
            ba * a[i].X + bb * a[i].Y + ty - b[i].Y)).Order().ToArray();
        // MapOverlayTransform cannot represent fractional rotation. Refit its
        // axis-aligned scale/translation from the same inliers; never drop rotation.
        var ax = inA.Average(p => (double)p.X); var ay = inA.Average(p => (double)p.Y);
        var bx = inB.Average(p => (double)p.X); var by = inB.Average(p => (double)p.Y);
        double numerator = 0, denominator = 0;
        for (var i = 0; i < inA.Length; i++)
        {
            var dx = inA[i].X - ax; var dy = inA[i].Y - ay;
            numerator += dx * (inB[i].X - bx) + dy * (inB[i].Y - by);
            denominator += dx * dx + dy * dy;
        }
        var scale = denominator > 0 ? numerator / denominator : 0;
        if (!double.IsFinite(scale) || scale <= 0)
            return result with { FailureReason = "degenerate-axis-pose" };
        tx = bx - scale * ax; ty = by - scale * ay;
        var errors = indexes.Select(i => Distance(scale * a[i].X + tx - b[i].X,
            scale * a[i].Y + ty - b[i].Y)).Order().ToArray();
        var median = Percentile(errors, .5);
        var ratio = (double)indexes.Length / matches.Count;
        var centerX = source.Width / 2d; var centerY = source.Height / 2d;
        var transform = new MapOverlayTransform
        {
            ScaleX = scale, ScaleY = scale, OffsetX = viewport.X + tx, OffsetY = viewport.Y + ty,
            ReferenceWidth = source.Width, ReferenceHeight = source.Height,
            ReferenceCenterX = centerX, ReferenceCenterY = centerY,
            ScreenCenterX = viewport.X + tx + centerX * scale,
            ScreenCenterY = viewport.Y + ty + centerY * scale,
            OrientationDegrees = 0, AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = errors[^1]
        };
        return result with
        {
            HasPose = true, FailureReason = string.Empty, Transform = transform,
            Inliers = indexes.Length, AxisAlignedInliers = errors.Count(e => e <= 5), InlierRatio = ratio,
            MedianError = median, P90Error = Percentile(errors, .9),
            SimilarityMedianError = Percentile(similarityErrors, .5),
            SimilarityP90Error = Percentile(similarityErrors, .9),
            RotationDegrees = Math.Atan2(ba, aa) * 180 / Math.PI,
            HullFraction = Math.Abs(Cv2.ContourArea(Cv2.ConvexHull(inB))) / (query.Width * (double)query.Height),
            SpanX = (inB.Max(p => p.X) - inB.Min(p => p.X)) / query.Width,
            SpanY = (inB.Max(p => p.Y) - inB.Min(p => p.Y)) / query.Height,
            OccupiedTiles = inB.Select(p => Math.Clamp((int)(p.X * 4 / query.Width), 0, 3)
                + 4 * Math.Clamp((int)(p.Y * 4 / query.Height), 0, 3)).Distinct().Count(),
            Score = indexes.Length + 10 * ratio + Math.Min(matches.Count, 60) * .07 - Math.Min(median, 12) * .18
        };
    }

    private static SIFT CreateSift(bool reference = false) =>
        SIFT.Create(nFeatures: reference ? 0 : 900, contrastThreshold: .025);

    private static Features Extract(Mat image, SIFT sift, bool balanceReference = false)
    {
        var scale = Math.Min(1d, 1000d / Math.Max(image.Width, image.Height));
        using var resized = new Mat();
        if (scale < 1) Cv2.Resize(image, resized,
            new Size((int)Math.Round(image.Width * scale), (int)Math.Round(image.Height * scale)),
            interpolation: InterpolationFlags.Area);
        else image.CopyTo(resized);
        using var gray = new Mat();
        switch (resized.Channels())
        {
            case 1: resized.CopyTo(gray); break;
            case 4: Cv2.CvtColor(resized, gray, ColorConversionCodes.BGRA2GRAY); break;
            default: Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY); break;
        }
        using var enhanced = new Mat();
        using var clahe = Cv2.CreateCLAHE(2, new Size(8, 8));
        clahe.Apply(gray, enhanced);
        var descriptors = new Mat();
        try
        {
            KeyPoint[] points;
            if (balanceReference)
            {
                // Detect without a global response cap: unseen dense regions must
                // not evict every descriptor from a different, visible region.
                points = SelectReferencePoints(sift.Detect(enhanced), enhanced.Width, enhanced.Height);
                if (points.Length > 0) sift.Compute(enhanced, ref points, descriptors);
            }
            else sift.DetectAndCompute(enhanced, null, out points, descriptors);
            return new Features(points, descriptors, resized.Width / (double)image.Width,
                resized.Height / (double)image.Height, image.Width, image.Height);
        }
        catch { descriptors.Dispose(); throw; }
    }

    private static KeyPoint[] SelectReferencePoints(KeyPoint[] detected, int width, int height)
    {
        const int grid = 4;
        const int maximum = 900;
        var cells = Enumerable.Range(0, grid * grid).Select(_ => new Queue<KeyPoint[]>()).ToArray();
        // Multiple orientations at one physical point remain a single group.
        // Keeping just one orientation would make the result depend on tie order.
        foreach (var group in detected.GroupBy(point => Pixel(point.Pt))
                     .Select(points => points.OrderByDescending(p => p.Response)
                         .ThenBy(p => p.Size).ThenBy(p => p.Angle).ToArray())
                     .OrderByDescending(points => points[0].Response)
                     .ThenBy(points => points[0].Pt.Y).ThenBy(points => points[0].Pt.X))
        {
            var point = group[0].Pt;
            var cell = Math.Clamp((int)(point.X * grid / width), 0, grid - 1)
                + grid * Math.Clamp((int)(point.Y * grid / height), 0, grid - 1);
            cells[cell].Enqueue(group);
        }
        var selected = new List<KeyPoint>(maximum);
        var counts = new int[cells.Length];
        while (selected.Count < maximum)
        {
            // Equal descriptor quotas with refill: exhausted cells surrender
            // their capacity, while each populated cell keeps response priority.
            var cell = Enumerable.Range(0, cells.Length)
                .Where(i => cells[i].Count > 0 && cells[i].Peek().Length <= maximum - selected.Count)
                .OrderBy(i => counts[i]).ThenByDescending(i => cells[i].Peek()[0].Response)
                .ThenBy(i => i).FirstOrDefault(-1);
            if (cell < 0) break;
            var group = cells[cell].Dequeue();
            selected.AddRange(group);
            counts[cell] += group.Length;
        }
        return selected.ToArray();
    }

    private static (string, string) Key(string mapClass, string floorKey) =>
        (mapClass.Trim().ToUpperInvariant(), floorKey.Trim().ToLowerInvariant());
    private static (int, int) Pixel(Point2f p) => ((int)Math.Round(p.X), (int)Math.Round(p.Y));
    private static Point2f OriginalPoint(Features feature, int index) => new(
        (float)((feature.Points[index].Pt.X + .5) / feature.ScaleX - .5),
        (float)((feature.Points[index].Pt.Y + .5) / feature.ScaleY - .5));
    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
    private static double Percentile(double[] values, double quantile)
    {
        var position = (values.Length - 1) * quantile;
        var lower = (int)position; var upper = Math.Min(lower + 1, values.Length - 1);
        return values[lower] + (values[upper] - values[lower]) * (position - lower);
    }

    public void Dispose()
    {
        Task<ReferenceGroup>[] tasks;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            tasks = _groups.Values.ToArray();
        }
        // Builders use only Task.Run and never require the caller's dispatcher.
        // Cancellation is checked between references; wait before freeing native Mats.
        try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
        catch (Exception) when (tasks.Any(t => t.IsCanceled || t.IsFaulted)) { }
        lock (_useGate)
        {
            foreach (var task in tasks.Where(t => t.IsCompletedSuccessfully))
                foreach (var reference in task.Result.Entries) reference.Feature?.Dispose();
            _shutdown.Dispose();
        }
    }

    private sealed record ReferenceEntry(MapRecord Map, string Floor, Features? Feature, string FailureReason);
    private sealed record ReferenceGroup(ReferenceEntry[] Entries, bool Complete, string FailureReason);
    private sealed record Features(KeyPoint[] Points, Mat Descriptors, double ScaleX, double ScaleY,
        int Width, int Height) : IDisposable
    {
        public void Dispose() => Descriptors.Dispose();
    }
}
