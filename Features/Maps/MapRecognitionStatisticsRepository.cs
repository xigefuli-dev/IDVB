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
/*
 * 文件职责：MapRecognitionStatisticsRepository。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
