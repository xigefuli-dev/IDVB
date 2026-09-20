using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using IDVBuff.Features.Maps;

namespace IDVBuff.Lifecycle;

public static class ModelImprovementUploadService
{
    internal static readonly Uri UploadEndpoint = new(
        "https://idvb.xgflee.com/api/model-improvement/training-packages");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task TryUploadDailyAsync(
        MainProgramPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        if (!preferences.HelpImproveModels)
            return;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var statePath = Path.Combine(
                AppDataPaths.RootDirectory,
                "ModelImprovement",
                "upload-state.json");
            var state = await LoadStateAsync(statePath, cancellationToken);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (state.LastAttemptDateUtc == today)
                return;

            state.LastAttemptDateUtc = today;
            await SaveStateAsync(statePath, state, cancellationToken);

            var queueDirectory = Path.Combine(
                AppDataPaths.RootDirectory,
                "ModelImprovement",
                "UploadQueue");
            Directory.CreateDirectory(queueDirectory);
            var packagePath = Path.Combine(
                queueDirectory,
                $"IDVB-training-{today:yyyyMMdd}-{Guid.NewGuid():N}.zip");
            try
            {
                var repository = new MapLearningRepository();
                await repository.ExportAsync(packagePath, cancellationToken);
                var validation = MapLearningExportValidator.Validate(packagePath);
                if (!validation.IsValid)
                    throw new InvalidDataException(validation.Message);
                if (validation.SampleCount == 0)
                {
                    state.LastFailure = null;
                    return;
                }
                if (string.Equals(
                        state.LastSuccessfulDatasetFingerprint,
                        validation.DatasetFingerprint,
                        StringComparison.Ordinal))
                {
                    state.LastFailure = null;
                    return;
                }

                string packageSha256;
                await using (var hashStream = File.OpenRead(packagePath))
                {
                    packageSha256 = Convert.ToHexString(
                            await SHA256.HashDataAsync(hashStream, cancellationToken))
                        .ToLowerInvariant();
                }

                using var client = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(5)
                };
                await using var package = File.OpenRead(packagePath);
                using var content = new StreamContent(package);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                content.Headers.ContentLength = package.Length;
                content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileNameStar = Path.GetFileName(packagePath)
                };
                using var request = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint)
                {
                    Content = content
                };
                request.Headers.TryAddWithoutValidation(
                    "X-IDVB-Build-Version",
                    BuildVersionInfo.BuildVersion);
                request.Headers.TryAddWithoutValidation(
                    "X-IDVB-Dataset-Fingerprint",
                    validation.DatasetFingerprint);
                request.Headers.TryAddWithoutValidation(
                    "X-IDVB-Package-Sha256",
                    packageSha256);
                request.Headers.TryAddWithoutValidation(
                    "X-IDVB-Sample-Count",
                    validation.SampleCount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                var receipt = JsonSerializer.Deserialize<UploadReceipt>(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (receipt is null
                    || !string.Equals(receipt.DatasetFingerprint,
                        validation.DatasetFingerprint,
                        StringComparison.Ordinal)
                    || !string.Equals(receipt.PackageSha256,
                        packageSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("训练包服务返回了不匹配的上传回执。");
                }
                state.LastSuccessfulUploadDateUtc = today;
                state.LastSuccessfulDatasetFingerprint =
                    validation.DatasetFingerprint;
                state.LastFailure = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                state.LastFailure = exception.Message;
            }
            finally
            {
                try
                {
                    if (File.Exists(packagePath))
                        File.Delete(packagePath);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Unable to remove uploaded training package: {exception.Message}");
                }
                await SaveStateAsync(statePath, state, CancellationToken.None);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<ModelImprovementUploadState> LoadStateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<ModelImprovementUploadState>(
                    await File.ReadAllTextAsync(path, cancellationToken)) ?? new();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to load model-improvement upload state: {exception.Message}");
        }
        return new ModelImprovementUploadState();
    }

    private static async Task SaveStateAsync(
        string path,
        ModelImprovementUploadState state,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(state, JsonOptions),
            cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class ModelImprovementUploadState
    {
        public DateOnly? LastAttemptDateUtc { get; set; }
        public DateOnly? LastSuccessfulUploadDateUtc { get; set; }
        public string? LastSuccessfulDatasetFingerprint { get; set; }
        public string? LastFailure { get; set; }
    }

    private sealed class UploadReceipt
    {
        public string DatasetFingerprint { get; set; } = string.Empty;
        public string PackageSha256 { get; set; } = string.Empty;
    }
}
