using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace IDVBuff.Survey.Preprocessing.OpenCv;

public sealed class OpenCvSurveyPreprocessor : ISurveyPreprocessor
{
    private readonly ISurveyAssetStore _assets;
    private readonly SurveyPreprocessingTuning _tuning;

    public OpenCvSurveyPreprocessor(
        ISurveyAssetStore assets,
        SurveyPreprocessingTuning tuning)
    {
        _assets = assets;
        _tuning = tuning;
        _tuning.Validate();
    }

    public async Task<SurveyPreprocessResult> ProcessAsync(
        SurveyPreprocessRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await _assets.OpenReadAsync(
            request.ProjectId,
            request.Observation.SourceAsset,
            cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var image = Cv2.ImDecode(memory.ToArray(), ImreadModes.Color);
        if (image.Empty())
            return new SurveyPreprocessResult(null, null, 0d, false, "source image cannot be decoded");
        using var gray = new Mat();
        using var visibleMask = new OpenCvSurveyMapShapeExtractor(_tuning).Extract(image);
        using var edges = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Canny(gray, edges, _tuning.EdgeLowThreshold, _tuning.EdgeHighThreshold);
        Cv2.BitwiseAnd(edges, visibleMask, edges);
        using var orb = ORB.Create(_tuning.MaximumFeatureCount);
        using var descriptors = new Mat();
        orb.DetectAndCompute(gray, visibleMask, out var keyPoints, descriptors);
        var visiblePixels = Cv2.CountNonZero(visibleMask);
        var edgeRatio = visiblePixels == 0
            ? 0d
            : Cv2.CountNonZero(edges) / (double)visiblePixels;
        var featureQuality = Math.Clamp(keyPoints.Length / 500d, 0d, 1d);
        var edgeQuality = Math.Clamp(edgeRatio / 0.08d, 0d, 1d);
        var quality = (featureQuality * 0.65d) + (edgeQuality * 0.35d);
        var visibleFraction = visiblePixels / (double)(visibleMask.Rows * visibleMask.Cols);
        var usable = keyPoints.Length >= 12 && edgeRatio >= 0.002d && visibleFraction >= 0.005d;
        var structureAsset = await WritePngAssetAsync(
            request.ProjectId,
            edges,
            request.Observation.Capture,
            cancellationToken).ConfigureAwait(false);
        var visibleMaskAsset = await WritePngAssetAsync(
            request.ProjectId,
            visibleMask,
            request.Observation.Capture,
            cancellationToken).ConfigureAwait(false);
        using var display = CreateDisplayImage(image, visibleMask);
        var displayAsset = await WritePngAssetAsync(
            request.ProjectId,
            display,
            request.Observation.Capture,
            cancellationToken).ConfigureAwait(false);
        var featureAsset = await WriteFeatureAssetAsync(
            request,
            keyPoints,
            descriptors,
            cancellationToken).ConfigureAwait(false);
        return new SurveyPreprocessResult(
            structureAsset,
            featureAsset,
            quality,
            usable,
            usable
                ? null
                : $"insufficient visible structure: {keyPoints.Length} features, {edgeRatio:P2} edges, {visibleFraction:P1} visible",
            displayAsset,
            visibleMaskAsset);
    }

    private static Mat CreateDisplayImage(Mat image, Mat visibleMask)
    {
        using var visibleBgr = new Mat(image.Size(), image.Type(), Scalar.Black);
        image.CopyTo(visibleBgr, visibleMask);
        var display = new Mat();
        Cv2.CvtColor(visibleBgr, display, ColorConversionCodes.BGR2BGRA);
        var channels = Cv2.Split(display);
        try
        {
            visibleMask.CopyTo(channels[3]);
            Cv2.Merge(channels, display);
            return display;
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    private async Task<SurveyAssetReference> WritePngAssetAsync(
        Guid projectId,
        Mat image,
        SurveyCaptureContext capture,
        CancellationToken cancellationToken)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        return await _assets.PutAsync(
            projectId,
            new SurveyEncodedFrame(
                bytes,
                ".png",
                "image/png",
                image.Width,
                image.Height,
                capture),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SurveyAssetReference?> WriteFeatureAssetAsync(
        SurveyPreprocessRequest request,
        IReadOnlyList<KeyPoint> keyPoints,
        Mat descriptors,
        CancellationToken cancellationToken)
    {
        if (descriptors.Empty() || descriptors.Rows <= 0 || descriptors.Cols <= 0)
            return null;
        using var contiguous = descriptors.Clone();
        var byteCount = checked((int)(contiguous.Total() * contiguous.ElemSize()));
        var descriptorBytes = new byte[byteCount];
        Marshal.Copy(contiguous.Data, descriptorBytes, 0, byteCount);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1);
            writer.Write(keyPoints.Count);
            writer.Write(contiguous.Rows);
            writer.Write(contiguous.Cols);
            writer.Write(contiguous.Type().Value);
            foreach (var point in keyPoints)
            {
                writer.Write(point.Pt.X);
                writer.Write(point.Pt.Y);
                writer.Write(point.Size);
                writer.Write(point.Angle);
                writer.Write(point.Response);
                writer.Write(point.Octave);
                writer.Write(point.ClassId);
            }
            writer.Write(descriptorBytes.Length);
            writer.Write(descriptorBytes);
        }
        stream.Position = 0;
        return await _assets.PutStreamAsync(
            request.ProjectId,
            stream,
            ".orb",
            "application/vnd.idvb.survey-orb",
            contiguous.Cols,
            contiguous.Rows,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
