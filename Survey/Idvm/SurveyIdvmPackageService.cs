using System.Text.Json;
using System.Text.Json.Serialization;
using IDVBuff.Survey.Contracts;

namespace IDVBuff.Survey.Idvm;

public sealed partial class SurveyIdvmPackageService : ISurveyPackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 96,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISurveyProjectRepository _projects;
    private readonly ISurveyAssetStore _assets;

    public SurveyIdvmPackageService(
        ISurveyProjectRepository projects,
        ISurveyAssetStore assets)
    {
        _projects = projects;
        _assets = assets;
    }

    public Task ExportProjectAsync(
        Guid projectId,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        ExportCoreAsync(projectId, destination, cancellationToken);

    public Task<IDVBuff.Survey.Domain.SurveyProjectSnapshot> ImportProjectAsync(
        Stream source,
        CancellationToken cancellationToken = default) =>
        ImportCoreAsync(source, cancellationToken);
}
