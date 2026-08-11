using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using OpenCvSharp;

namespace IDVBuff.Survey.Fusion.OpenCv;

public sealed class OpenCvSurveyVisualComposer : ISurveyVisualComposer
{
    private readonly SurveyFusionAssetWriter _assets;
    private readonly SurveyFusionTuning _tuning;

    public OpenCvSurveyVisualComposer(ISurveyAssetStore assets, SurveyFusionTuning tuning)
    {
        _assets = new SurveyFusionAssetWriter(assets);
        _tuning = tuning;
        _tuning.Validate();
    }

    public async Task<SurveyRenderedAsset> ComposeAsync(
        SurveyProjectSnapshot project,
        string floorKey,
        CancellationToken cancellationToken = default)
    {
        var floor = project.Floors.Single(item =>
            string.Equals(item.FloorKey, floorKey, StringComparison.OrdinalIgnoreCase));
        var observations = project.Observations.ToDictionary(item => item.ObservationId);
        var layers = project.Layers
            .Where(item => item.FloorId == floor.FloorId && !item.IsDeleted && item.IsVisible)
            .OrderBy(item => item.ZOrder)
            .ToArray();
        if (layers.Length == 0)
            throw new InvalidOperationException("The floor has no visible survey layers to compose.");
        var layout = SurveyFusionGeometry.Calculate(layers, observations, _tuning.MaximumOutputPixels);
        using var canvas = new Mat(layout.CanvasSize, MatType.CV_8UC3, Scalar.Black);
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = observations[layer.ObservationId];
            using var encoded = await _assets.ReadAsync(
                project.Project.ProjectId,
                layer.UsesCleanedDisplay && observation.DisplayAsset is not null
                    ? observation.DisplayAsset
                    : observation.SourceAsset,
                ImreadModes.Unchanged,
                cancellationToken);
            using var source = new Mat();
            using var maskSource = new Mat();
            if (encoded.Channels() == 4)
            {
                Cv2.CvtColor(encoded, source, ColorConversionCodes.BGRA2BGR);
                Cv2.ExtractChannel(encoded, maskSource, 3);
            }
            else if (encoded.Channels() == 2)
            {
                var channels = Cv2.Split(encoded);
                try
                {
                    Cv2.CvtColor(channels[0], source, ColorConversionCodes.GRAY2BGR);
                    channels[1].CopyTo(maskSource);
                }
                finally
                {
                    foreach (var channel in channels)
                        channel.Dispose();
                }
            }
            else
            {
                if (encoded.Channels() == 1)
                    Cv2.CvtColor(encoded, source, ColorConversionCodes.GRAY2BGR);
                else if (encoded.Channels() == 3)
                    encoded.CopyTo(source);
                else
                    throw new InvalidDataException(
                        $"Unsupported survey visual channel count: {encoded.Channels()}.");
                maskSource.Create(encoded.Size(), MatType.CV_8UC1);
                maskSource.SetTo(Scalar.White);
            }
            if (layer.HiddenMaskAsset is { } hiddenAsset)
            {
                using var hidden = await _assets.ReadAsync(
                    project.Project.ProjectId,
                    hiddenAsset,
                    ImreadModes.Grayscale,
                    cancellationToken);
                using var visible = new Mat();
                Cv2.BitwiseNot(hidden, visible);
                Cv2.BitwiseAnd(maskSource, visible, maskSource);
            }
            using var matrix = SurveyFusionGeometry.CreateAffine(layer.EffectiveTransform, layout.Origin);
            using var warped = new Mat(layout.CanvasSize, MatType.CV_8UC3, Scalar.Black);
            using var mask = new Mat(layout.CanvasSize, MatType.CV_8UC1, Scalar.Black);
            Cv2.WarpAffine(source, warped, matrix, layout.CanvasSize, InterpolationFlags.Linear);
            Cv2.WarpAffine(maskSource, mask, matrix, layout.CanvasSize, InterpolationFlags.Nearest);
            if (layer.Opacity >= 0.999d)
            {
                warped.CopyTo(canvas, mask);
                continue;
            }
            using var blended = new Mat();
            Cv2.AddWeighted(warped, layer.Opacity, canvas, 1d - layer.Opacity, 0d, blended);
            blended.CopyTo(canvas, mask);
        }
        var capture = observations[layers[0].ObservationId].Capture;
        var asset = await _assets.WritePngAsync(
            project.Project.ProjectId,
            canvas,
            capture,
            cancellationToken);
        return new SurveyRenderedAsset(asset, layout.Bounds, layout.Origin);
    }
}
