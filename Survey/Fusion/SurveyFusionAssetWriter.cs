using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using OpenCvSharp;

namespace IDVBuff.Survey.Fusion.OpenCv;

internal sealed class SurveyFusionAssetWriter
{
    private readonly ISurveyAssetStore _assets;

    public SurveyFusionAssetWriter(ISurveyAssetStore assets) => _assets = assets;

    public async Task<Mat> ReadAsync(
        Guid projectId,
        SurveyAssetReference asset,
        ImreadModes mode,
        CancellationToken cancellationToken)
    {
        await using var stream = await _assets.OpenReadAsync(projectId, asset, cancellationToken)
            .ConfigureAwait(false);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var image = Cv2.ImDecode(memory.ToArray(), mode);
        if (image.Empty())
        {
            image.Dispose();
            throw new InvalidDataException($"测绘资产无法解码：{asset.Sha256}");
        }
        return image;
    }

    public async Task<SurveyAssetReference> WritePngAsync(
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
}
