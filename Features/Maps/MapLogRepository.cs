using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Persistent storage for one map diagnostic session.
/// </summary>
public sealed class MapLogRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly string _logDirectory;

    public MapLogRepository(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "Logs");
    }

    public string LogDirectory => _logDirectory;

    public string CreateSessionPath()
    {
        Directory.CreateDirectory(_logDirectory);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(_logDirectory, $"scan-log-{timestamp}-{suffix}.json");
    }

    /// <summary>
    /// Merges a new batch into the session file and replaces it atomically.
    /// Keeping the on-disk format as a JSON array makes the files easy to inspect.
    /// </summary>
    public Task FlushAsync(
        string sessionPath,
        IReadOnlyList<MapLogEntry> entries,
        CancellationToken cancellationToken = default) =>
        MergeAndWriteAsync(sessionPath, entries, ".tmp", cancellationToken);

    public Task FinalizeAsync(
        string sessionPath,
        IReadOnlyList<MapLogEntry> entries,
        CancellationToken cancellationToken = default) =>
        MergeAndWriteAsync(sessionPath, entries, ".final.tmp", cancellationToken);

    public async Task<IReadOnlyList<MapLogEntry>> ReadSessionAsync(
        string sessionPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
            return [];

        await using var stream = File.OpenRead(sessionPath);
        return await JsonSerializer.DeserializeAsync<List<MapLogEntry>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    public void DeleteSession(string sessionPath)
    {
        try
        {
            foreach (var path in new[]
            {
                sessionPath,
                sessionPath + ".tmp",
                sessionPath + ".final.tmp"
            })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    public void CleanupOldSessions(int keepCount = 20)
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
                return;

            var files = Directory.GetFiles(_logDirectory, "scan-log-*.json")
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .ToArray();
            foreach (var file in files.Skip(Math.Max(0, keepCount)))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Skip files that cannot be deleted.
                }
            }

            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var tempFile in Directory.GetFiles(_logDirectory, "*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(tempFile) < cutoff)
                        File.Delete(tempFile);
                }
                catch
                {
                    // Skip locked temporary files.
                }
            }
        }
        catch
        {
            // Cleanup is non-critical and must not stop log collection.
        }
    }

    private async Task MergeAndWriteAsync(
        string sessionPath,
        IReadOnlyList<MapLogEntry> entries,
        string temporarySuffix,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return;

        Directory.CreateDirectory(_logDirectory);
        var merged = new List<MapLogEntry>();
        if (File.Exists(sessionPath))
        {
            await using var existingStream = File.OpenRead(sessionPath);
            var existing = await JsonSerializer.DeserializeAsync<List<MapLogEntry>>(
                existingStream,
                JsonOptions,
                cancellationToken);
            if (existing is not null)
                merged.AddRange(existing);
        }

        var knownSequences = merged.Select(entry => entry.Sequence).ToHashSet();
        foreach (var entry in entries)
        {
            if (knownSequences.Add(entry.Sequence))
                merged.Add(entry);
        }
        merged.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));

        var tempPath = sessionPath + temporarySuffix;
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    merged,
                    JsonOptions,
                    cancellationToken);
            }
            File.Move(tempPath, sessionPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best effort cleanup.
            }
            throw;
        }
    }
}
/*
 * 文件职责：MapLogRepository。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
