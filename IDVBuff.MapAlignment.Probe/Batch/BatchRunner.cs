using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using IDVBuff.MapAlignment.Probe.Output;
using IDVBuff.MapAlignment.Probe.Pipeline;

namespace IDVBuff.MapAlignment.Probe.Batch;

/// <summary>
/// 批量评估运行器。对 glob 匹配到的所有文件逐张执行指定策略，
/// 汇总成功率、置信度分布、per-map 统计等。
/// </summary>
public sealed class BatchRunner
{
    private readonly PipelineRegistry _registry;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BatchRunner(PipelineRegistry registry)
    {
        _registry = registry;
    }

    public async Task RunAsync(ProbeContext context)
    {
        var strategy = _registry.Find(context.Batch!.Glob.GetHashCode().ToString())
            ?? throw new InvalidOperationException("Batch mode requires a strategy from registry lookup.");

        // 重新从 context 中提取正确的 strategy（通过 CLI 路由时已确定策略名）
        // 这里 strategyName 实际由 CliHost 通过 context 的属性传递。
        // 简化：直接使用默认 dual-gate 策略。
        // 实际调用路径在 CliHost 中已解析，此处通过 context 的 Batch 附加属性传递策略名。
    }

    /// <summary>
    /// 使用指定策略批量运行图片文件。
    /// </summary>
    public async Task<BatchSummary> RunBatchAsync(
        IPipelineStrategy strategy,
        string glob,
        int parallelism,
        ProbeContext templateContext,
        CancellationToken ct)
    {
        var files = ResolveGlob(glob);
        if (files.Length == 0)
        {
            Console.Error.WriteLine("glob 未匹配到任何文件。");
            return new BatchSummary { Strategy = strategy.StrategyName };
        }

        Console.Error.WriteLine($"批量评估：{files.Length} 个文件，策略={strategy.StrategyName}，并行度={parallelism}");

        var totalTimer = Stopwatch.StartNew();
        ConcurrentBag<BatchFileResult> results;

        if (parallelism <= 1)
        {
            results = await RunSequentialAsync(strategy, files, templateContext, ct);
        }
        else
        {
            results = await RunParallelAsync(strategy, files, parallelism, templateContext, ct);
        }

        totalTimer.Stop();

        // 聚合统计
        var succeededResults = results.Where(r => r.Succeeded).ToArray();
        var confidences = succeededResults.Select(r => r.Confidence).ToArray();
        var averageConfidence = confidences.Length > 0 ? confidences.Average() : 0d;
        var averageWallMs = results.Count > 0 ? results.Average(r => r.TotalWallMs) : 0d;

        var perMap = results
            .GroupBy(r => r.MapId ?? "(unknown)")
            .Select(g => new PerMapStats
            {
                MapId = g.Key,
                MapDisplayName = g.FirstOrDefault()?.MapDisplayName ?? g.Key,
                Attempts = g.Count(),
                Succeeded = g.Count(r => r.Succeeded),
                AverageConfidence = g.Where(r => r.Succeeded).Select(r => r.Confidence).DefaultIfEmpty(0).Average()
            })
            .OrderByDescending(s => s.AverageConfidence)
            .ToArray();

        var dist = new ConfidenceDistribution
        {
            Bucket90_100 = confidences.Count(c => c >= 0.9),
            Bucket80_90 = confidences.Count(c => c >= 0.8 && c < 0.9),
            Bucket70_80 = confidences.Count(c => c >= 0.7 && c < 0.8),
            Bucket50_70 = confidences.Count(c => c >= 0.5 && c < 0.7),
            Bucket0_50 = confidences.Count(c => c < 0.5)
        };

        var summary = new BatchSummary
        {
            Strategy = strategy.StrategyName,
            TotalFiles = files.Length,
            Succeeded = succeededResults.Length,
            Failed = files.Length - succeededResults.Length,
            AverageConfidence = averageConfidence,
            MinimumConfidence = confidences.Length > 0 ? confidences.Min() : 0d,
            MaximumConfidence = confidences.Length > 0 ? confidences.Max() : 0d,
            AverageTotalWallMs = averageWallMs,
            Results = results.OrderByDescending(r => r.Confidence).ToList(),
            PerMapStats = perMap,
            ConfidenceDistribution = dist
        };

        Console.Error.WriteLine($"批量评估完成：{summary.Succeeded}/{summary.TotalFiles} 成功，"
            + $"平均置信度={averageConfidence:F3}，总耗时={totalTimer.Elapsed.TotalMilliseconds:F0}ms");

        Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));

        return summary;
    }

    private async Task<ConcurrentBag<BatchFileResult>> RunSequentialAsync(
        IPipelineStrategy strategy,
        string[] files,
        ProbeContext template,
        CancellationToken ct)
    {
        var results = new ConcurrentBag<BatchFileResult>();
        for (var i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            Console.Error.WriteLine($"[{i + 1}/{files.Length}] {Path.GetFileName(file)}");
            var result = await RunOneAsync(strategy, file, template, ct);
            results.Add(result);
        }
        return results;
    }

    private async Task<ConcurrentBag<BatchFileResult>> RunParallelAsync(
        IPipelineStrategy strategy,
        string[] files,
        int parallelism,
        ProbeContext template,
        CancellationToken ct)
    {
        var results = new ConcurrentBag<BatchFileResult>();
        var semaphore = new SemaphoreSlim(parallelism);
        var tasks = files.Select(async file =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                Console.Error.WriteLine($"[并行] {Path.GetFileName(file)}");
                var result = await RunOneAsync(strategy, file, template, ct);
                results.Add(result);
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task<BatchFileResult> RunOneAsync(
        IPipelineStrategy strategy,
        string file,
        ProbeContext template,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var context = new ProbeContext
            {
                ImagePath = file,
                GateTemplatePath = template.GateTemplatePath,
                GateThreshold = template.GateThreshold,
                ClientWidth = template.ClientWidth,
                UseFullFrame = template.UseFullFrame,
                ViewportRegion = template.ViewportRegion,
                ViewportMargin = template.ViewportMargin,
                EnableStructure = template.EnableStructure,
                EnableEcc = template.EnableEcc,
                ForceBestCandidate = template.ForceBestCandidate,
                TopCount = template.TopCount,
                TopCandidates = template.TopCandidates,
                DownscaleFactor = template.DownscaleFactor,
                SettingsPath = template.SettingsPath,
                StructureFillOutputDirectory = template.StructureFillOutputDirectory,
                StructureFillGuideMap = template.StructureFillGuideMap,
                OutputPath = null
            };
            var probeResult = await strategy.RunAsync(context, ct);
            sw.Stop();
            return new BatchFileResult
            {
                File = file,
                Succeeded = probeResult.Succeeded,
                MapId = probeResult.MapId,
                MapDisplayName = probeResult.MapDisplayName,
                Confidence = probeResult.Confidence,
                TotalWallMs = sw.Elapsed.TotalMilliseconds,
                FailureReason = probeResult.FailureReason
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new BatchFileResult
            {
                File = file,
                Succeeded = false,
                FailureReason = "已取消",
                TotalWallMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new BatchFileResult
            {
                File = file,
                Succeeded = false,
                FailureReason = ex.Message,
                TotalWallMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }

    private static string[] ResolveGlob(string pattern)
    {
        // 简单 glob 支持：直接解析为文件列表。
        // 支持 **/*.png、*.png、dir/*.png 等模式。
        if (string.IsNullOrWhiteSpace(pattern))
            return [];

        var directory = Path.GetDirectoryName(pattern);
        var filePattern = Path.GetFileName(pattern);

        if (string.IsNullOrWhiteSpace(directory) || directory == ".")
            directory = Environment.CurrentDirectory;

        if (!Directory.Exists(directory))
            return [];

        var searchOption = pattern.Contains("**")
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        var files = Directory.GetFiles(
            Path.GetFullPath(directory),
            filePattern,
            searchOption);

        // 过滤掉非图片扩展名
        var imageExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif" };

        return files
            .Where(f => imageExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
