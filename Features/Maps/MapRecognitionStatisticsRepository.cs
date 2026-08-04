using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed class MapRecognitionStatistics
{
    public int SchemaVersion { get; init; } = 1;
    public long TotalAttempts { get; init; }
    public long SuccessfulAttempts { get; init; }

    public double SuccessRate => TotalAttempts == 0
        ? 0d
        : (double)SuccessfulAttempts / TotalAttempts;
}

/// <summary>
/// Persists the small, product-facing recognition counters independently of
/// optional diagnostic and research collection.
/// </summary>
public sealed class MapRecognitionStatisticsRepository
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _statisticsPath;

    public MapRecognitionStatisticsRepository(string? statisticsPath = null)
    {
        _statisticsPath = statisticsPath ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "recognition-statistics.json");
    }

    public async Task<MapRecognitionStatistics> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task RecordAttemptStartedAsync(
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            current => new MapRecognitionStatistics
            {
                TotalAttempts = current.TotalAttempts + 1,
                SuccessfulAttempts = current.SuccessfulAttempts
            },
            cancellationToken);

    public Task RecordAlignmentProducedAsync(
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            current => new MapRecognitionStatistics
            {
                TotalAttempts = Math.Max(
                    current.TotalAttempts,
                    current.SuccessfulAttempts + 1),
                SuccessfulAttempts = current.SuccessfulAttempts + 1
            },
            cancellationToken);

    private async Task UpdateAsync(
        Func<MapRecognitionStatistics, MapRecognitionStatistics> update,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var updated = Normalize(update(await ReadAsync(cancellationToken)));
            var directory = Path.GetDirectoryName(_statisticsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temporaryPath = _statisticsPath + ".tmp";
            try
            {
                await using (var stream = File.Create(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        updated,
                        JsonOptions,
                        cancellationToken);
                }
                File.Move(temporaryPath, _statisticsPath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup must not hide the original failure.
                }
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<MapRecognitionStatistics> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_statisticsPath))
            return new MapRecognitionStatistics();

        try
        {
            await using var stream = File.OpenRead(_statisticsPath);
            var statistics = await JsonSerializer.DeserializeAsync<MapRecognitionStatistics>(
                stream,
                JsonOptions,
                cancellationToken);
            return Normalize(statistics ?? new MapRecognitionStatistics());
        }
        catch (JsonException)
        {
            return new MapRecognitionStatistics();
        }
    }

    private static MapRecognitionStatistics Normalize(
        MapRecognitionStatistics statistics)
    {
        var successful = Math.Max(0, statistics.SuccessfulAttempts);
        var total = Math.Max(successful, statistics.TotalAttempts);
        return new MapRecognitionStatistics
        {
            TotalAttempts = total,
            SuccessfulAttempts = successful
        };
    }
}
