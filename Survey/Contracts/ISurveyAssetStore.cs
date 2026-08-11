using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Contracts;

public interface ISurveyAssetStore
{
    Task<SurveyAssetReference> PutAsync(
        Guid projectId,
        SurveyEncodedFrame frame,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(
        Guid projectId,
        SurveyAssetReference asset,
        CancellationToken cancellationToken = default);

    Task<SurveyAssetReference> PutStreamAsync(
        Guid projectId,
        Stream source,
        string fileExtension,
        string mediaType,
        int pixelWidth,
        int pixelHeight,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default);

    string ResolveAbsolutePath(Guid projectId, SurveyAssetReference asset);
}
