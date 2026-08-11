using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Contracts;

public sealed record SurveyRenderedAsset(
    SurveyAssetReference Asset,
    SurveyWorldRect WorldBounds,
    SurveyWorldPoint CanvasOrigin);

public sealed record SurveyDualOutput(
    Guid ProjectId,
    long ProjectRevision,
    string FloorKey,
    SurveyRenderedAsset VisualMap,
    SurveyRenderedAsset RecognitionStructure);

public interface ISurveyVisualComposer
{
    Task<SurveyRenderedAsset> ComposeAsync(
        SurveyProjectSnapshot project,
        string floorKey,
        CancellationToken cancellationToken = default);
}

public interface ISurveyStructureFusion
{
    Task<SurveyRenderedAsset> FuseAsync(
        SurveyProjectSnapshot project,
        string floorKey,
        CancellationToken cancellationToken = default);
}

public interface ISurveyPackageService
{
    Task ExportProjectAsync(
        Guid projectId,
        Stream destination,
        CancellationToken cancellationToken = default);

    Task<SurveyProjectSnapshot> ImportProjectAsync(
        Stream source,
        CancellationToken cancellationToken = default);
}
