using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Pure frame-to-frame ORB tracker. Rejected observations never replace the
/// trusted keyframe or mutate the accumulated absolute transform.
/// </summary>
public sealed partial class MapOrbTracker : IDisposable
{
    private readonly MapOrbTrackingOptions _options;
    private readonly double _baselineScale;
    private MapOrbFrameFeatures _anchor;
    private MapOverlayTransform _currentTransform;
    private MapOverlayTransform _submittedTransform;
    private MapScreenRect _viewportBounds;
    private bool _disposed;

    public MapOrbTracker(
        Mat initialFrame,
        MapScreenRect viewportBounds,
        MapOverlayTransform initialTransform,
        MapOrbTrackingOptions options)
    {
        ArgumentNullException.ThrowIfNull(initialFrame);
        ArgumentNullException.ThrowIfNull(initialTransform);
        ArgumentNullException.ThrowIfNull(options);
        if (initialFrame.Empty())
            throw new ArgumentException("The ORB seed frame is empty.", nameof(initialFrame));
        if (!viewportBounds.IsValid)
            throw new ArgumentException("The ORB seed viewport is invalid.", nameof(viewportBounds));

        _options = options;
        _baselineScale = UniformScale(initialTransform);
        if (!double.IsFinite(_baselineScale) || _baselineScale <= 0)
            throw new ArgumentException("The ORB seed transform is invalid.", nameof(initialTransform));
        _anchor = Extract(initialFrame);
        if (_anchor.KeyPoints.Length == 0 || _anchor.Descriptors.Empty())
        {
            _anchor.Dispose();
            throw new InvalidOperationException("The ORB seed frame contains no usable descriptors.");
        }
        _viewportBounds = viewportBounds;
        _currentTransform = initialTransform;
        _submittedTransform = initialTransform;
    }

    public MapOverlayTransform CurrentTransform => _currentTransform;

    public MapOrbTrackingResult Track(
        Mat frame,
        MapScreenRect viewportBounds,
        TimeSpan actualInterval)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty())
            return MapOrbTrackingResult.Reject(_submittedTransform, "empty frame");
        if (!SameViewport(_viewportBounds, viewportBounds)
            || frame.Width != _anchor.Gray.Width
            || frame.Height != _anchor.Gray.Height)
        {
            return MapOrbTrackingResult.Reject(_submittedTransform, "viewport changed");
        }

        MapOrbFrameFeatures? current = null;
        var extractionMilliseconds = 0d;
        var matchingMilliseconds = 0d;
        var ransacMilliseconds = 0d;
        try
        {
            var stageTimer = System.Diagnostics.Stopwatch.StartNew();
            current = Extract(frame);
            stageTimer.Stop();
            extractionMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
            if (current.KeyPoints.Length == 0 || current.Descriptors.Empty())
                return MapOrbTrackingResult.Reject(_submittedTransform, "no descriptors");

            stageTimer.Restart();
            var matches = FindMutualRatioMatches(_anchor.Descriptors, current.Descriptors);
            stageTimer.Stop();
            matchingMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
            if (matches.Count < _options.MinimumMatches)
            {
                return MapOrbTrackingResult.Reject(
                    _submittedTransform,
                    $"only {matches.Count} mutual ratio matches",
                    matches.Count);
            }

            var source = matches
                .Select(match => _anchor.KeyPoints[match.QueryIdx].Pt)
                .ToArray();
            var destination = matches
                .Select(match => current.KeyPoints[match.TrainIdx].Pt)
                .ToArray();
            using var sourceInput = Mat.FromArray(source);
            using var destinationInput = Mat.FromArray(destination);
            using var inlierMask = new Mat();
            stageTimer.Restart();
            using var affine = Cv2.EstimateAffinePartial2D(
                sourceInput,
                destinationInput,
                inlierMask,
                RobustEstimationAlgorithms.RANSAC,
                _options.MaximumMedianReprojectionErrorPixels,
                2000,
                0.99,
                10);
            stageTimer.Stop();
            ransacMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
            if (affine is null || affine.Empty() || affine.Rows != 2 || affine.Cols != 3)
                return MapOrbTrackingResult.Reject(_submittedTransform, "RANSAC produced no affine fit", matches.Count);

            var inlierIndexes = Enumerable.Range(0, matches.Count)
                .Where(index => ReadMask(inlierMask, index))
                .ToArray();
            var inlierCount = inlierIndexes.Length;
            var inlierRatio = (double)inlierCount / matches.Count;
            if (inlierCount < _options.MinimumRansacInliers
                || inlierRatio < _options.MinimumInlierRatio)
            {
                return MapOrbTrackingResult.Reject(
                    _submittedTransform,
                    "insufficient RANSAC consensus",
                    matches.Count,
                    inlierCount,
                    inlierRatio);
            }

            var a = affine.At<double>(0, 0);
            var b = affine.At<double>(1, 0);
            var rotation = Math.Atan2(b, a) * 180d / Math.PI;
            if (!double.IsFinite(rotation)
                || Math.Abs(rotation) > _options.MaximumRotationDegrees)
            {
                return MapOrbTrackingResult.Reject(
                    _submittedTransform,
                    "rotation exceeds limit",
                    matches.Count,
                    inlierCount,
                    inlierRatio,
                    rotation: rotation);
            }

            if (!TryFitRotationFreeSimilarity(
                    source,
                    destination,
                    inlierIndexes,
                    out var stepScale,
                    out var translationX,
                    out var translationY,
                    out var medianError))
            {
                return MapOrbTrackingResult.Reject(
                    _submittedTransform,
                    "rotation-free fit failed",
                    matches.Count,
                    inlierCount,
                    inlierRatio,
                    rotation: rotation);
            }
            if (medianError > _options.MaximumMedianReprojectionErrorPixels)
            {
                return MapOrbTrackingResult.Reject(
                    _submittedTransform,
                    "median reprojection error exceeds limit",
                    matches.Count,
                    inlierCount,
                    inlierRatio,
                    medianError,
                    rotation,
                    stepScale);
            }
            if (!double.IsFinite(stepScale)
                || Math.Abs(stepScale - 1d) > _options.MaximumStepScaleChangeRatio)
            {
                return MapOrbTrackingResult.Reject(
                    _submittedTransform,
                    "step scale exceeds limit",
                    matches.Count,
                    inlierCount,
                    inlierRatio,
                    medianError,
                    rotation,
                    stepScale);
            }

            var translation = Math.Sqrt(
                (translationX * translationX) + (translationY * translationY));
            var elapsedSeconds = Math.Max(0, actualInterval.TotalSeconds);
            var translationLimit = Math.Max(
                _options.MinimumTranslationLimitPixels,
                _options.MaximumTranslationPixelsPerSecond * elapsedSeconds);
            if (!double.IsFinite(translation) || translation > translationLimit)
            {
                return MapOrbTrackingResult.Reject(
                    _submittedTransform,
                    "translation exceeds limit",
                    matches.Count,
                    inlierCount,
                    inlierRatio,
                    medianError,
                    rotation,
                    stepScale,
                    translation);
            }

            var candidate = Compose(
                _currentTransform,
                _viewportBounds,
                stepScale,
                translationX,
                translationY);
            var candidateScale = UniformScale(candidate);
            if (Math.Abs((candidateScale / _baselineScale) - 1d)
                > _options.MaximumBaselineScaleChangeRatio)
            {
                return MapOrbTrackingResult.Reject(
                    _submittedTransform,
                    "baseline scale exceeds limit",
                    matches.Count,
                    inlierCount,
                    inlierRatio,
                    medianError,
                    rotation,
                    stepScale,
                    translation);
            }

            _currentTransform = candidate;
            var submittedScale = UniformScale(_submittedTransform);
            var submittedTranslation = Math.Sqrt(
                Math.Pow(candidate.OffsetX - _submittedTransform.OffsetX, 2)
                + Math.Pow(candidate.OffsetY - _submittedTransform.OffsetY, 2));
            var submittedScaleChange = Math.Abs(
                (candidateScale / submittedScale) - 1d);
            var shouldCommit = submittedTranslation >= _options.TranslationDeadbandPixels
                || submittedScaleChange >= _options.ScaleDeadbandRatio;
            if (shouldCommit)
                _submittedTransform = candidate;

            _anchor.Dispose();
            _anchor = current;
            current = null;
            return new MapOrbTrackingResult(
                true,
                shouldCommit,
                _submittedTransform,
                string.Empty,
                matches.Count,
                inlierCount,
                inlierRatio,
                medianError,
                rotation,
                stepScale,
                translation,
                extractionMilliseconds,
                matchingMilliseconds,
                ransacMilliseconds);
        }
        catch (OpenCVException exception)
        {
            return MapOrbTrackingResult.Reject(
                _submittedTransform,
                $"OpenCV failure: {exception.Message}");
        }
        finally
        {
            current?.Dispose();
        }
    }

    public void Reanchor(
        Mat frame,
        MapScreenRect viewportBounds,
        MapOverlayTransform transform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var replacement = Extract(frame);
        if (replacement.KeyPoints.Length == 0 || replacement.Descriptors.Empty())
        {
            replacement.Dispose();
            throw new InvalidOperationException("The recovery frame contains no usable ORB descriptors.");
        }
        _anchor.Dispose();
        _anchor = replacement;
        _viewportBounds = viewportBounds;
        _currentTransform = transform;
        _submittedTransform = transform;
    }

    public static MapOverlayTransform Compose(
        MapOverlayTransform absolute,
        MapScreenRect viewportBounds,
        double stepScale,
        double localTranslationX,
        double localTranslationY)
    {
        var scale = UniformScale(absolute) * stepScale;
        var offsetX = viewportBounds.X
            + (stepScale * (absolute.OffsetX - viewportBounds.X))
            + localTranslationX;
        var offsetY = viewportBounds.Y
            + (stepScale * (absolute.OffsetY - viewportBounds.Y))
            + localTranslationY;
        return new MapOverlayTransform
        {
            ScaleX = scale,
            ScaleY = scale,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceCenterX = absolute.ReferenceCenterX,
            ReferenceCenterY = absolute.ReferenceCenterY,
            ScreenCenterX = offsetX + (absolute.ReferenceCenterX * scale),
            ScreenCenterY = offsetY + (absolute.ReferenceCenterY * scale),
            ReferenceWidth = absolute.ReferenceWidth,
            ReferenceHeight = absolute.ReferenceHeight,
            OrientationDegrees = absolute.OrientationDegrees,
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = absolute.MaximumResidualPixels,
            UsedDegenerateAxisFallback = absolute.UsedDegenerateAxisFallback
        };
    }

    private MapOrbFrameFeatures Extract(Mat source)
    {
        using var bgr = new Mat();
        switch (source.Channels())
        {
            case 4:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
                break;
            case 3:
                source.CopyTo(bgr);
                break;
            default:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
                break;
        }
        using var rawGray = new Mat();
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, rawGray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var gray = new Mat();
        using (var clahe = Cv2.CreateCLAHE(2d, new Size(8, 8)))
            clahe.Apply(rawGray, gray);

        var channels = Cv2.Split(hsv);
        try
        {
            using var saturated = new Mat();
            using var bright = new Mat();
            var nuisance = new Mat();
            Cv2.Threshold(channels[1], saturated, 105, 255, ThresholdTypes.Binary);
            Cv2.Threshold(channels[2], bright, 70, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(saturated, bright, nuisance);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            Cv2.Dilate(nuisance, nuisance, kernel);
            ApplyIgnoreRegions(nuisance);
            using var validMask = new Mat();
            Cv2.BitwiseNot(nuisance, validMask);
            var border = Math.Max(1, (int)Math.Round(Math.Min(source.Width, source.Height) * 0.02));
            Cv2.Rectangle(validMask, new Rect(0, 0, validMask.Width, validMask.Height), Scalar.Black, border);

            using var orb = ORB.Create(
                nFeatures: _options.FeatureCount,
                scaleFactor: 1.2f,
                nLevels: 8,
                edgeThreshold: 31,
                firstLevel: 0,
                scoreType: ORBScoreType.Harris,
                patchSize: 31,
                fastThreshold: 20);
            var descriptors = new Mat();
            orb.DetectAndCompute(gray, validMask, out var keyPoints, descriptors);
            return new MapOrbFrameFeatures(gray, nuisance, keyPoints, descriptors);
        }
        catch
        {
            gray.Dispose();
            throw;
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    private void ApplyIgnoreRegions(Mat mask)
    {
        foreach (var region in _options.IgnoreRegions.Where(region => region.IsValid))
        {
            var x = Math.Clamp((int)Math.Floor(region.X * mask.Width), 0, mask.Width);
            var y = Math.Clamp((int)Math.Floor(region.Y * mask.Height), 0, mask.Height);
            var right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * mask.Width), 0, mask.Width);
            var bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * mask.Height), 0, mask.Height);
            if (right > x && bottom > y)
                Cv2.Rectangle(mask, new Rect(x, y, right - x, bottom - y), Scalar.White, -1);
        }
    }

    private IReadOnlyList<DMatch> FindMutualRatioMatches(Mat anchor, Mat current)
    {
        using var matcher = new BFMatcher(NormTypes.Hamming);
        var forward = matcher.KnnMatch(anchor, current, 2)
            .Where(group => group.Length >= 2
                && group[0].Distance < group[1].Distance * _options.RatioThreshold)
            .Select(group => group[0])
            .ToArray();
        var reverse = matcher.KnnMatch(current, anchor, 2)
            .Where(group => group.Length >= 2
                && group[0].Distance < group[1].Distance * _options.RatioThreshold)
            .Select(group => group[0])
            .GroupBy(match => match.QueryIdx)
            .ToDictionary(group => group.Key, group => group.OrderBy(match => match.Distance).First());
        return forward
            .Where(match => reverse.TryGetValue(match.TrainIdx, out var back)
                && back.TrainIdx == match.QueryIdx)
            .OrderBy(match => match.Distance)
            .ToArray();
    }

    private static bool ReadMask(Mat mask, int index) => mask.Rows == 1
        ? mask.At<byte>(0, index) != 0
        : mask.At<byte>(index, 0) != 0;

    private static double UniformScale(MapOverlayTransform transform) =>
        (transform.ScaleX + transform.ScaleY) / 2d;

    private static bool SameViewport(MapScreenRect left, MapScreenRect right) =>
        right.IsValid
        && Math.Abs(left.X - right.X) <= 0.5
        && Math.Abs(left.Y - right.Y) <= 0.5
        && Math.Abs(left.Width - right.Width) <= 0.5
        && Math.Abs(left.Height - right.Height) <= 0.5;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _anchor.Dispose();
    }

    private sealed class MapOrbFrameFeatures(
        Mat gray,
        Mat nuisanceMask,
        KeyPoint[] keyPoints,
        Mat descriptors) : IDisposable
    {
        public Mat Gray { get; } = gray;
        public Mat NuisanceMask { get; } = nuisanceMask;
        public KeyPoint[] KeyPoints { get; } = keyPoints;
        public Mat Descriptors { get; } = descriptors;

        public void Dispose()
        {
            Gray.Dispose();
            NuisanceMask.Dispose();
            Descriptors.Dispose();
        }
    }
}
