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
    /// Appends a new batch to the session JSON array. The collector supplies
    /// monotonically increasing, de-duplicated sequences, so persistence does
    /// not need to deserialize and rewrite the entire growing session once per
    /// second.
    /// </summary>
    public Task FlushAsync(
        string sessionPath,
        IReadOnlyList<MapLogEntry> entries,
        CancellationToken cancellationToken = default) =>
        AppendBatchAsync(sessionPath, entries, cancellationToken);

    public Task FinalizeAsync(
        string sessionPath,
        IReadOnlyList<MapLogEntry> entries,
        CancellationToken cancellationToken = default) =>
        AppendBatchAsync(sessionPath, entries, cancellationToken);

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

    /// <summary>
    /// Removes all persisted map-log files and temporary files from the log
    /// directory. The directory itself is retained for the next session.
    /// </summary>
    public void ClearData()
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
                return;

            var paths = Directory.EnumerateFiles(_logDirectory, "scan-log-*.json*")
                .Concat(Directory.EnumerateFiles(_logDirectory, "flush-errors.log"));
            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                    // A log may still be open. Continue clearing the rest.
                }
            }
        }
        catch
        {
            // Cleanup is best effort and must not stop the runtime.
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

    private async Task AppendBatchAsync(
        string sessionPath,
        IReadOnlyList<MapLogEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return;

        Directory.CreateDirectory(_logDirectory);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
        if (!File.Exists(sessionPath))
        {
            await File.WriteAllBytesAsync(
                sessionPath,
                encoded,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var stream = new FileStream(
            sessionPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var closingBracket = FindClosingArrayBracket(stream);
        var previousToken = FindPreviousNonWhitespace(stream, closingBracket - 1);
        if (previousToken < 0)
            throw new InvalidDataException("Map log session is not a JSON array.");

        var hasExistingEntries = ReadByteAt(stream, previousToken) != (byte)'[';
        stream.SetLength(previousToken + 1);
        stream.Position = previousToken + 1;
        if (hasExistingEntries)
            await stream.WriteAsync(new byte[] { (byte)',' }, cancellationToken)
                .ConfigureAwait(false);

        // Strip the batch's outer '[' and ']'. Its leading/trailing whitespace
        // is retained so the final session remains readable indented JSON.
        await stream.WriteAsync(
            encoded.AsMemory(1, encoded.Length - 2),
            cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)']' }, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long FindClosingArrayBracket(FileStream stream)
    {
        var position = FindPreviousNonWhitespace(stream, stream.Length - 1);
        if (position < 0 || ReadByteAt(stream, position) != (byte)']')
            throw new InvalidDataException("Map log session has no closing JSON array bracket.");
        return position;
    }

    private static long FindPreviousNonWhitespace(FileStream stream, long position)
    {
        for (; position >= 0; position--)
        {
            var value = ReadByteAt(stream, position);
            if (value != (byte)' '
                && value != (byte)'\t'
                && value != (byte)'\r'
                && value != (byte)'\n')
                return position;
        }
        return -1;
    }

    private static byte ReadByteAt(FileStream stream, long position)
    {
        stream.Position = position;
        var value = stream.ReadByte();
        if (value < 0)
            throw new EndOfStreamException();
        return (byte)value;
    }
}
/*
 * 文件职责：MapLogRepository。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
