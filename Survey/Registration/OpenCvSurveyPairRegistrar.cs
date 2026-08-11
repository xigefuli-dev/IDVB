using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using OpenCvSharp;

namespace IDVBuff.Survey.Registration.OpenCv;

public sealed class OpenCvSurveyPairRegistrar : ISurveyPairRegistrar
{
    public const string AlgorithmId = "orb-affine-partial";
    public const string AlgorithmVersion = "1.0.0";
    private readonly ISurveyAssetStore _assets;
    private readonly SurveyRegistrationTuning _tuning;

    public OpenCvSurveyPairRegistrar(
        ISurveyAssetStore assets,
        SurveyRegistrationTuning tuning)
    {
        _assets = assets;
        _tuning = tuning;
        _tuning.Validate();
    }

    public async Task<SurveyRegistrationResult> RegisterAsync(
        SurveyRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var source = await ReadGrayAsync(
            request.SourceObservation,
            request.SourceImageAsset,
            cancellationToken);
        using var target = await ReadGrayAsync(
            request.TargetObservation,
            request.TargetImageAsset,
            cancellationToken);
        using var orb = ORB.Create(nFeatures: 5000, fastThreshold: 7);
        using var sourceDescriptors = new Mat();
        using var targetDescriptors = new Mat();
        orb.DetectAndCompute(source, null, out var sourcePoints, sourceDescriptors);
        orb.DetectAndCompute(target, null, out var targetPoints, targetDescriptors);
        if (sourceDescriptors.Empty() || targetDescriptors.Empty())
            return Reject("no ORB descriptors");

        using var matcher = new BFMatcher(NormTypes.Hamming);
        var matches = matcher.KnnMatch(sourceDescriptors, targetDescriptors, 2)
            .Where(group => group.Length >= 2
                && group[0].Distance < group[1].Distance * _tuning.RatioTest)
            .Select(group => group[0])
            .OrderBy(item => item.Distance)
            .DistinctBy(item => item.TrainIdx)
            .Take(1000)
            .ToArray();
        if (matches.Length < _tuning.MinimumMatches)
            return Reject($"only {matches.Length} ratio matches");
        cancellationToken.ThrowIfCancellationRequested();

        var sourceCoordinates = matches.Select(item => sourcePoints[item.QueryIdx].Pt).ToArray();
        var targetCoordinates = matches.Select(item => targetPoints[item.TrainIdx].Pt).ToArray();
        using var sourceInput = Mat.FromArray(sourceCoordinates);
        using var targetInput = Mat.FromArray(targetCoordinates);
        using var inlierMask = new Mat();
        using var affine = Cv2.EstimateAffinePartial2D(
            sourceInput,
            targetInput,
            inlierMask,
            RobustEstimationAlgorithms.RANSAC,
            _tuning.MaximumResidualPixels,
            3000,
            0.995,
            20);
        if (affine is null || affine.Empty())
            return Reject("RANSAC did not produce a similarity transform");
        var inliers = Cv2.CountNonZero(inlierMask);
        var inlierRatio = inliers / (double)matches.Length;
        var a = affine.At<double>(0, 0);
        var b = affine.At<double>(1, 0);
        var scale = Math.Sqrt((a * a) + (b * b));
        var rotation = Math.Atan2(b, a) * 180d / Math.PI;
        var transform = new SurveyLayerTransform(
            affine.At<double>(0, 2),
            affine.At<double>(1, 2),
            rotation,
            scale,
            scale);
        var residual = CalculateResidual(
            sourceCoordinates,
            targetCoordinates,
            inlierMask,
            transform);
        var accepted = inliers >= _tuning.MinimumInliers
            && inlierRatio >= _tuning.MinimumInlierRatio
            && residual <= _tuning.MaximumResidualPixels
            && scale >= _tuning.MinimumScale
            && scale <= _tuning.MaximumScale;
        var confidence = Math.Clamp(
            (inlierRatio * 0.65d)
            + ((1d - Math.Min(1d, residual / _tuning.MaximumResidualPixels)) * 0.35d),
            0d,
            1d);
        return new SurveyRegistrationResult(
            accepted,
            transform,
            confidence,
            residual,
            inliers,
            AlgorithmId,
            AlgorithmVersion,
            accepted
                ? null
                : $"quality gate rejected: {inliers} inliers, {inlierRatio:P1}, {residual:F2}px, scale {scale:F3}");
    }

    private async Task<Mat> ReadGrayAsync(
        SurveyObservation observation,
        SurveyAssetReference? selectedAsset,
        CancellationToken cancellationToken)
    {
        await using var stream = await _assets.OpenReadAsync(
            observation.ProjectId,
            selectedAsset ?? observation.DisplayAsset ?? observation.SourceAsset,
            cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Cv2.ImDecode(memory.ToArray(), ImreadModes.Grayscale);
    }

    private static double CalculateResidual(
        IReadOnlyList<Point2f> source,
        IReadOnlyList<Point2f> target,
        Mat inlierMask,
        SurveyLayerTransform transform)
    {
        var errors = new List<double>();
        for (var index = 0; index < source.Count; index++)
        {
            if (inlierMask.At<byte>(index) == 0)
                continue;
            var projected = transform.Transform(new SurveyWorldPoint(source[index].X, source[index].Y));
            var dx = projected.X - target[index].X;
            var dy = projected.Y - target[index].Y;
            errors.Add(Math.Sqrt((dx * dx) + (dy * dy)));
        }
        return errors.Count == 0 ? double.PositiveInfinity : errors.Average();
    }

    private static SurveyRegistrationResult Reject(string reason) => new(
        false,
        SurveyLayerTransform.Identity,
        0d,
        double.PositiveInfinity,
        0,
        AlgorithmId,
        AlgorithmVersion,
        reason);
}
