using OpenCvSharp;
using System.Text.Json;
using System.Threading.Channels;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 独立的研究数据采集器。将每次对齐的图像和诊断数据按
/// session/map/floor/outcome 分类保存，便于离线复现和批量分析。
/// </summary>
public sealed class MapAlignmentResearchCollector : IAsyncDisposable
{
    private sealed record WriteRequest(
        string CaseDirectory,
        string ManifestJson,
        string? AttemptJsonLine,
        IReadOnlyDictionary<string, byte[]> Artifacts);

    private readonly MapStructurePreprocessor? _preprocessor;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _caseCounts = new();
    private readonly HashSet<string> _referenceSaved = new();
    private readonly Dictionary<string, int> _successSampleCounts = new();
    private int _totalCaseCount;
    private Channel<WriteRequest>? _channel;
    private Task? _worker;
    private string? _sessionDirectory;
    private long _recordCount;
    private bool _disposed;
    private readonly TimeSpan _retention;
    private readonly long _maximumBytes;

    private const int MaxSuccessHighConfPerMapFloor = 3;
    private const int MaxSuccessLowConfPerMapFloor = 5;
    private const int MaxCasesPerSession = 200;
    private const int MaxFailedPerMapFloor = 50;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string RootDirectory { get; }

    public MapAlignmentResearchCollector(
        MapStructurePreprocessor? preprocessor = null,
        string? rootDirectory = null,
        TimeSpan? retention = null,
        long maximumBytes = 2L * 1024L * 1024L * 1024L)
    {
        _preprocessor = preprocessor;
        RootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "AlignmentResearch"));
        _retention = retention ?? TimeSpan.FromDays(30);
        _maximumBytes = Math.Max(1L, maximumBytes);
    }

    public bool IsEnabled => Volatile.Read(ref _channel) is not null;
    public long RecordCount => Interlocked.Read(ref _recordCount);
    public string? CurrentSessionDirectory => _sessionDirectory;

    // ═════════════════════════════════════════════════════════════
    // 生命周期
    // ═════════════════════════════════════════════════════════════

    public async Task SetEnabledAsync(bool enabled)
    {
        Channel<WriteRequest>? channelToClose = null;
        Task? workerToWait = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (enabled == (_channel is not null))
                return;
            if (enabled)
            {
                Directory.CreateDirectory(RootDirectory);
                var sessionsRoot = Path.Combine(RootDirectory, "sessions");
                Directory.CreateDirectory(sessionsRoot);
                _sessionDirectory = Path.Combine(
                    sessionsRoot,
                    $"{DateTime.UtcNow:yyyy-MM-dd_HHmmss}--{Guid.NewGuid().ToString("N")[..8]}");
                Directory.CreateDirectory(_sessionDirectory);
                _channel = Channel.CreateUnbounded<WriteRequest>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false
                    });
                _worker = RunWriterAsync(_channel.Reader, _sessionDirectory);
                _caseCounts.Clear();
                _referenceSaved.Clear();
                _successSampleCounts.Clear();
                _totalCaseCount = 0;
                Interlocked.Exchange(ref _recordCount, 0);
                WriteSessionManifest(_sessionDirectory);
                CleanupSessions();
                return;
            }

            channelToClose = _channel;
            workerToWait = _worker;
            _channel = null;
            _worker = null;
        }

        channelToClose?.Writer.TryComplete();
        if (workerToWait is not null)
        {
            try { await workerToWait.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { Warn(ex); }
        }
    }

    // ═════════════════════════════════════════════════════════════
    // 采集入口
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// 记录一次对齐 attempt。始终写入 attempts.jsonl；如果满足采集策略则额外保存图片。
    /// </summary>
    public void RecordAttempt(
        MapAlignmentResearchAttempt attempt,
        MapRecord map,
        string floorKey,
        Mat liveViewport)
    {
        var channel = Volatile.Read(ref _channel);
        var sessionDir = Volatile.Read(ref _sessionDirectory);
        if (channel is null || sessionDir is null)
            return;
        try
        {
            // 决定 case 分类
            var outcome = DetermineOutcome(attempt);
            var mapShort = map.Id.ToString("N")[..8];
            var caseKey = $"{mapShort}:{floorKey}:{outcome}";

            // 采集策略
            if (!ShouldCaptureCase(attempt, caseKey))
            {
                // 只写 JSONL，不存图片
                var request = new WriteRequest(
                    string.Empty,
                    string.Empty,
                    JsonSerializer.Serialize(attempt, SerializerOptions),
                    new Dictionary<string, byte[]>());
                channel.Writer.TryWrite(request);
                Interlocked.Increment(ref _recordCount);
                return;
            }

            // 编码图片
            var artifacts = EncodeCase(attempt, liveViewport);

            // 路径
            var caseDir = Path.Combine(
                sessionDir, mapShort, floorKey,
                $"{GetNextCaseSeq(caseKey):D3}-{outcome}");
            if (attempt.Confidence > 0.01)
                caseDir += $"-{attempt.Confidence:P0}".Replace(" ", "");

            var manifestJson = JsonSerializer.Serialize(
                BuildCaseManifest(attempt, map, floorKey),
                SerializerOptions);

            var attemptLine = JsonSerializer.Serialize(attempt, SerializerOptions);

            var request2 = new WriteRequest(
                caseDir, manifestJson, attemptLine, artifacts);
            if (channel.Writer.TryWrite(request2))
            {
                Interlocked.Increment(ref _recordCount);
            }
        }
        catch (Exception exception)
        {
            Warn(exception);
        }
    }

    // ═════════════════════════════════════════════════════════════
    // 采集策略
    // ═════════════════════════════════════════════════════════════

    private static string DetermineOutcome(MapAlignmentResearchAttempt attempt)
    {
        if (attempt.CalibrationUpdated)
            return "calibrated";
        if (!attempt.Accepted)
            return attempt.FailureCategory switch
            {
                MapAlignmentResearchFailureCategory.WeakFit => "rejected-weakfit",
                MapAlignmentResearchFailureCategory.AmbiguousCandidates => "rejected-ambiguous",
                MapAlignmentResearchFailureCategory.InsufficientStructure => "rejected-nostructure",
                MapAlignmentResearchFailureCategory.NoVisualFeatures => "rejected-novisual",
                _ => "rejected"
            };
        return attempt.Confidence >= 0.65 ? "ok-high" : "ok-low";
    }

    private bool ShouldCaptureCase(
        MapAlignmentResearchAttempt attempt,
        string caseKey)
    {
        // 失败案例：始终保存
        if (!attempt.Accepted)
        {
            var count = GetCaseCount(caseKey);
            return count < MaxFailedPerMapFloor;
        }

        // 校准更新：始终保存
        if (attempt.CalibrationUpdated)
            return true;

        // 成功案例：采样限制
        var maxPerTier = attempt.Confidence >= 0.65
            ? MaxSuccessHighConfPerMapFloor
            : MaxSuccessLowConfPerMapFloor;

        lock (_gate)
        {
            if (_totalCaseCount >= MaxCasesPerSession)
            {
                // 超限时只接受失败案例
                return false;
            }

            var key = $"success:{caseKey}";
            if (!_successSampleCounts.TryGetValue(key, out var sampled))
                sampled = 0;
            if (sampled >= maxPerTier)
                return false;

            _successSampleCounts[key] = sampled + 1;
            _totalCaseCount++;
            return true;
        }
    }

    private int GetNextCaseSeq(string caseKey)
    {
        lock (_gate)
        {
            if (!_caseCounts.TryGetValue(caseKey, out var count))
                count = 0;
            count++;
            _caseCounts[caseKey] = count;
            return count;
        }
    }

    private int GetCaseCount(string caseKey)
    {
        lock (_gate)
        {
            _caseCounts.TryGetValue(caseKey, out var count);
            return count;
        }
    }

    // ═════════════════════════════════════════════════════════════
    // 图片编码
    // ═════════════════════════════════════════════════════════════

    private static IReadOnlyDictionary<string, byte[]> EncodeCase(
        MapAlignmentResearchAttempt attempt,
        Mat liveViewport)
    {
        var result = new Dictionary<string, byte[]>();
        if (liveViewport is null || liveViewport.Empty())
            return result;

        // 截帧原图
        Cv2.ImEncode(".png", liveViewport, out var liveBytes);
        result["viewport.png"] = liveBytes;

        // Canny 边缘
        using var edges = GateTemplateDetector.CreateEdges(liveViewport);
        Cv2.ImEncode(".png", edges, out var edgeBytes);
        result["edges.png"] = edgeBytes;

        // 有效范围 mask
        using var mask = new Mat(
            liveViewport.Size(), MatType.CV_8UC1, Scalar.Black);
        if (attempt.ValidMapBounds is { IsValid: true } bounds
            && attempt.FinalTransform is { } transform
            && attempt.WindowSignature is { } signature)
        {
            var left = (int)Math.Floor(
                bounds.X * transform.ScaleX + transform.OffsetX - signature.ViewportX);
            var top = (int)Math.Floor(
                bounds.Y * transform.ScaleY + transform.OffsetY - signature.ViewportY);
            var right = (int)Math.Ceiling(
                bounds.Right * transform.ScaleX + transform.OffsetX - signature.ViewportX);
            var bottom = (int)Math.Ceiling(
                bounds.Bottom * transform.ScaleY + transform.OffsetY - signature.ViewportY);
            left = Math.Clamp(left, 0, liveViewport.Width);
            top = Math.Clamp(top, 0, liveViewport.Height);
            right = Math.Clamp(right, left, liveViewport.Width);
            bottom = Math.Clamp(bottom, top, liveViewport.Height);
            if (right > left && bottom > top)
                Cv2.Rectangle(
                    mask,
                    new Rect(left, top, right - left, bottom - top),
                    Scalar.White,
                    -1);
        }
        else
        {
            mask.SetTo(Scalar.White);
        }
        Cv2.ImEncode(".png", mask, out var maskBytes);
        result["valid-mask.png"] = maskBytes;

        // 叠加图
        using var overlay = liveViewport.Clone();
        using (var maskPoints = new Mat())
        {
            Cv2.FindNonZero(mask, maskPoints);
            if (!maskPoints.Empty())
            {
                var projectedBounds = Cv2.BoundingRect(maskPoints);
                Cv2.Rectangle(overlay, projectedBounds, Scalar.LimeGreen, 2);
            }
        }
        var text = attempt.Accepted
            ? $"accepted {attempt.Confidence:P0}"
            : attempt.FailureCategory.ToString();
        Cv2.PutText(
            overlay, text, new Point(12, 28),
            HersheyFonts.HersheySimplex, 0.7, Scalar.Red, 2);
        Cv2.ImEncode(".png", overlay, out var overlayBytes);
        result["overlay.png"] = overlayBytes;

        return result;
    }

    // ═════════════════════════════════════════════════════════════
    // Case manifest
    // ═════════════════════════════════════════════════════════════

    private static Dictionary<string, object?> BuildCaseManifest(
        MapAlignmentResearchAttempt attempt,
        MapRecord map,
        string floorKey)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        return new Dictionary<string, object?>
        {
            ["mapId"] = attempt.MapId,
            ["mapTitle"] = map.Title,
            ["floorKey"] = floorKey,
            ["observedAt"] = attempt.ObservedAt,
            ["accepted"] = attempt.Accepted,
            ["confidence"] = attempt.Confidence,
            ["failureCategory"] = attempt.FailureCategory.ToString(),
            ["failureReason"] = attempt.FailureReason,
            ["calibrationUpdated"] = attempt.CalibrationUpdated,
            ["elapsedMs"] = attempt.ElapsedMilliseconds,
            ["scaleX"] = attempt.FinalTransform?.ScaleX,
            ["scaleY"] = attempt.FinalTransform?.ScaleY,
            ["offsetX"] = attempt.FinalTransform?.OffsetX,
            ["offsetY"] = attempt.FinalTransform?.OffsetY,
            ["edgeCoverage"] =
                attempt.ConfidenceBreakdown?.EdgeCoverage,
            ["occupancyCoverage"] =
                attempt.ConfidenceBreakdown?.OccupancyCoverage,
            ["chamferPixels"] =
                attempt.ConfidenceBreakdown?.ChamferPixels,
            ["candidateCount"] = attempt.Candidates.Count,
            ["queryEdgePixels"] = attempt.QueryEdgePixels,
            ["gateCandidateCount"] = attempt.GateCandidateCount,
            ["referenceWidth"] = attempt.ReferenceWidth,
            ["referenceHeight"] = attempt.ReferenceHeight,
            ["recognitionPixelWidth"] = profile?.RecognitionPixelWidth,
            ["recognitionPixelHeight"] = profile?.RecognitionPixelHeight,
            ["windowSignature"] = attempt.WindowSignature is { } sig
                ? new Dictionary<string, object?>
                {
                    ["clientWidth"] = sig.ClientWidth,
                    ["clientHeight"] = sig.ClientHeight,
                    ["viewportWidth"] = sig.ViewportWidth,
                    ["viewportHeight"] = sig.ViewportHeight,
                    ["dpi"] = sig.Dpi
                }
                : null
        };
    }

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

                // 写 case 图片
                if (!string.IsNullOrEmpty(request.CaseDirectory)
                    && request.Artifacts.Count > 0)
                {
                    Directory.CreateDirectory(request.CaseDirectory);

                    if (!string.IsNullOrEmpty(request.ManifestJson))
                    {
                        await File.WriteAllTextAsync(
                            Path.Combine(request.CaseDirectory, "manifest.json"),
                            request.ManifestJson);
                    }

                    foreach (var (name, bytes) in request.Artifacts)
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
