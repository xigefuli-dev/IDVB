using OpenCvSharp;
using System.Text.Json;
using System.Threading.Channels;

namespace IDVBuff.Features.Maps;
/// <summary>
/// 独立的研究数据采集器。将每次对齐的图像和诊断数据按
/// session/map/floor/outcome 分类保存，便于离线复现和批量分析。
/// </summary>
public sealed partial class MapAlignmentResearchCollector : IAsyncDisposable
{

    // ═════════════════════════════════════════════════════════════
    // 后台写入器
    // ═════════════════════════════════════════════════════════════

    private async Task RunWriterAsync(
        ChannelReader<WriteRequest> reader,
        string sessionDirectory)
    {
        var attemptsPath = Path.Combine(sessionDirectory, "attempts.jsonl");
        await using var stream = new FileStream(
            attemptsPath, FileMode.Append, FileAccess.Write, FileShare.Read,
            32 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream);
        var written = 0;

        await foreach (var request in reader.ReadAllAsync())
        {
            try
            {
                // 写 JSONL（始终追加）
                var line = string.IsNullOrEmpty(request.AttemptJsonLine)
                    ? request.ManifestJson
                    : request.AttemptJsonLine;
                if (!string.IsNullOrEmpty(line))
                {
                    await writer.WriteLineAsync(line);
                    await writer.FlushAsync();
                }
                written++;
                if (written % 50 == 0)
                    CleanupSessions();

                // PNG 编码在这里完成，不占用对齐发布路径。
                var artifacts = request.Artifacts;
                if (request.Encode is { } encode)
                    artifacts = EncodeCase(encode.Attempt, encode.Viewport);

                // 写 case 图片
                if (!string.IsNullOrEmpty(request.CaseDirectory)
                    && artifacts.Count > 0)
                {
                    Directory.CreateDirectory(request.CaseDirectory);

                    if (!string.IsNullOrEmpty(request.ManifestJson))
                    {
                        await File.WriteAllTextAsync(
                            Path.Combine(request.CaseDirectory, "manifest.json"),
                            request.ManifestJson);
                    }

                    foreach (var (name, bytes) in artifacts)
                    {
                        await File.WriteAllBytesAsync(
                            Path.Combine(request.CaseDirectory, name), bytes);
                    }
                }
            }
            catch (Exception exception)
            {
                Warn(exception);
            }
            finally
            {
                request.Encode?.Viewport.Dispose();
            }
        }
    }

    // ═════════════════════════════════════════════════════════════
    // 参考图缓存
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// 将指定 map+floor 的参考图及其预处理结果缓存到当前 session 目录。
    /// 每个 session 内每个 map+floor 只保存一次。
    /// </summary>
    public void CacheReferenceImage(
        string referenceImagePath, Guid mapId, string floorKey)
    {
        var sessionDir = Volatile.Read(ref _sessionDirectory);
        if (sessionDir is null || !File.Exists(referenceImagePath))
            return;

        var mapShort = mapId.ToString("N")[..8];
        var key = $"{mapShort}:{floorKey}";
        lock (_gate)
        {
            if (!_referenceSaved.Add(key))
                return;
        }

        try
        {
            var refDir = Path.Combine(sessionDir, mapShort, floorKey);
            Directory.CreateDirectory(refDir);

            using var reference = Cv2.ImRead(
                referenceImagePath, ImreadModes.Unchanged);
            if (reference.Empty())
                return;

            Cv2.ImWrite(Path.Combine(refDir, "reference.png"), reference);

            if (_preprocessor is not null)
            {
                using var features = _preprocessor.ProcessReference(
                    reference, ignoreRegions: null);
                if (features.Edges is not null && !features.Edges.Empty())
                    Cv2.ImWrite(
                        Path.Combine(refDir, "reference-edges.png"),
                        features.Edges);
                if (features.StructureMask is not null
                    && !features.StructureMask.Empty())
                    Cv2.ImWrite(
                        Path.Combine(refDir, "reference-structure.png"),
                        features.StructureMask);
            }
        }
        catch (Exception exception)
        {
            Warn(exception);
            lock (_gate)
                _referenceSaved.Remove(key);
        }
    }

    // ═════════════════════════════════════════════════════════════
    // 辅助
    // ═════════════════════════════════════════════════════════════

    private static void WriteSessionManifest(string sessionDirectory)
    {
        try
        {
            var manifest = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 2,
                ["createdAt"] = DateTimeOffset.UtcNow,
                ["sessionDirectory"] = sessionDirectory
            };
            File.WriteAllText(
                Path.Combine(sessionDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, SerializerOptions));
        }
        catch { /* 非关键 */ }
    }

    private void CleanupSessions()
    {
        try
        {
            var sessionsRoot = Path.Combine(RootDirectory, "sessions");
            if (!Directory.Exists(sessionsRoot))
                return;

            var sessions = Directory.GetDirectories(sessionsRoot)
                .Select(dir => new DirectoryInfo(dir))
                .OrderBy(d => d.CreationTimeUtc)
                .ToList();

            var cutoff = DateTime.UtcNow - _retention;
            foreach (var expired in sessions
                .Where(d => d.CreationTimeUtc < cutoff).ToArray())
            {
                if (!string.Equals(
                    expired.FullName, _sessionDirectory,
                    StringComparison.OrdinalIgnoreCase))
                {
                    expired.Delete(recursive: true);
                }
                sessions.Remove(expired);
            }

            long totalBytes = sessions.Sum(GetDirectorySize);
            foreach (var oldest in sessions)
            {
                if (totalBytes <= _maximumBytes)
                    break;
                if (string.Equals(
                    oldest.FullName, _sessionDirectory,
                    StringComparison.OrdinalIgnoreCase))
                    continue;
                var bytes = GetDirectorySize(oldest);
                oldest.Delete(recursive: true);
                totalBytes -= bytes;
            }
        }
        catch (Exception exception)
        {
            Warn(exception);
        }
    }

    private static long GetDirectorySize(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles(
                "*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0L; }
    }

    private static void Warn(Exception exception) =>
        MapLogCollector.Instance.Append(
            MapLogCategory.System,
            MapLogLevel.Warning,
            $"研究采集失败: {exception.Message}");

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        if (IsEnabled)
            await SetEnabledAsync(false);
        lock (_gate)
            _disposed = true;
    }
}
