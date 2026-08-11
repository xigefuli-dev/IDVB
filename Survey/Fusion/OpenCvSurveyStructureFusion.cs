using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using OpenCvSharp;

namespace IDVBuff.Survey.Fusion.OpenCv;

public sealed class OpenCvSurveyStructureFusion : ISurveyStructureFusion
{
    private readonly SurveyFusionAssetWriter _assets;
    private readonly SurveyFusionTuning _tuning;

    public OpenCvSurveyStructureFusion(ISurveyAssetStore assets, SurveyFusionTuning tuning)
    {
        _assets = new SurveyFusionAssetWriter(assets);
        _tuning = tuning;
        _tuning.Validate();
    }

    public async Task<SurveyRenderedAsset> FuseAsync(
        SurveyProjectSnapshot project,
        string floorKey,
        CancellationToken cancellationToken = default)
    {
        var floor = project.Floors.Single(item =>
            string.Equals(item.FloorKey, floorKey, StringComparison.OrdinalIgnoreCase));
        var observations = project.Observations.ToDictionary(item => item.ObservationId);
        // Visual visibility, opacity and Z-order intentionally do not participate.
        var layers = project.Layers
            .Where(item => item.FloorId == floor.FloorId && !item.IsDeleted)
            .ToArray();
        if (layers.Length == 0)
            throw new InvalidOperationException("The floor has no active observations to fuse.");
        var layout = SurveyFusionGeometry.Calculate(layers, observations, _tuning.MaximumOutputPixels);
        using var evidence = new Mat(layout.CanvasSize, MatType.CV_32FC1, Scalar.Black);
        using var coverage = new Mat(layout.CanvasSize, MatType.CV_32FC1, Scalar.Black);
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = observations[layer.ObservationId];
            using var source = await _assets.ReadAsync(
                project.Project.ProjectId,
                observation.StructureAsset ?? observation.SourceAsset,
                ImreadModes.Grayscale,
                cancellationToken);
            using var edges = new Mat();
            if (observation.StructureAsset is null)
                Cv2.Canny(source, edges, 50d, 150d);
            else
                source.CopyTo(edges);
            using var normalized = new Mat();
            edges.ConvertTo(normalized, MatType.CV_32FC1, 1d / 255d);
            using var matrix = SurveyFusionGeometry.CreateAffine(layer.EffectiveTransform, layout.Origin);
            using var warped = new Mat(layout.CanvasSize, MatType.CV_32FC1, Scalar.Black);
            using var maskSource = await ReadVisibleMaskAsync(
                project.Project.ProjectId,
                observation,
                source.Size(),
                cancellationToken);
            if (layer.HiddenMaskAsset is { } hiddenAsset)
            {
                using var hidden = await _assets.ReadAsync(
                    project.Project.ProjectId,
                    hiddenAsset,
                    ImreadModes.Grayscale,
                    cancellationToken);
                using var hiddenFloat = new Mat();
                hidden.ConvertTo(hiddenFloat, MatType.CV_32FC1, 1d / 255d);
                using var visible = Scalar.All(1d) - hiddenFloat;
                Cv2.Multiply(normalized, visible, normalized);
                Cv2.Multiply(maskSource, visible, maskSource);
            }
            using var mask = new Mat(layout.CanvasSize, MatType.CV_32FC1, Scalar.Black);
            Cv2.WarpAffine(normalized, warped, matrix, layout.CanvasSize, InterpolationFlags.Linear);
            Cv2.WarpAffine(maskSource, mask, matrix, layout.CanvasSize, InterpolationFlags.Nearest);
            var registrationWeight = observation.State == SurveyObservationState.Registered ? 1d : 0.35d;
            var weight = Math.Max(0.05d, observation.Quality) * registrationWeight;
            Cv2.Add(evidence, warped * weight, evidence);
            Cv2.Add(coverage, mask * weight, coverage);
        }
        using var probability = new Mat();
        using var denominator = new Mat();
        Cv2.Add(coverage, new Scalar(1e-6d), denominator);
        Cv2.Divide(evidence, denominator, probability);
        using var binaryFloat = new Mat();
        Cv2.Threshold(
            probability,
            binaryFloat,
            _tuning.StructureBinaryThreshold,
            255d,
            ThresholdTypes.Binary);
        using var binary = new Mat();
        binaryFloat.ConvertTo(binary, MatType.CV_8UC1);
        var capture = observations[layers[0].ObservationId].Capture;
        var asset = await _assets.WritePngAsync(
            project.Project.ProjectId,
            binary,
            capture,
            cancellationToken);
        return new SurveyRenderedAsset(asset, layout.Bounds, layout.Origin);
    }

    private async Task<Mat> ReadVisibleMaskAsync(
        Guid projectId,
        SurveyObservation observation,
        Size size,
        CancellationToken cancellationToken)
    {
        if (observation.VisibleMaskAsset is null)
            return new Mat(size, MatType.CV_32FC1, Scalar.All(1d));
        using var encoded = await _assets.ReadAsync(
            projectId,
            observation.VisibleMaskAsset,
            ImreadModes.Grayscale,
            cancellationToken);
        var normalized = new Mat();
        encoded.ConvertTo(normalized, MatType.CV_32FC1, 1d / 255d);
        return normalized;
    }
}
