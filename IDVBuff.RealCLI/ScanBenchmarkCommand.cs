// IDVB Real CLI — 扫描诊断与基准烤机命令（ScanBenchmarkCommand）
//
// 专用于自动化对局截图核对与识别管线诊断测试（"烤机"）：
// 1. 支持指定预期地图（Ground Truth）进行自动化对账验证（Matched / Mismatched / Unrecognized）
// 2. 收集各阶段耗时、代码执行路径、关键事件、VPSG 3.0 诊断、双门检测与几何候选
// 3. 预热 VPSG 3.0 空间索引，避免冷启动并发构建导致的早期跳过
// 4. 支持目录模式（--dir）配合 --infer-map 自动从文件名/目录名推导预期地图，或清单模式（--manifest）

using System.Diagnostics;
using System.Text.Json;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using IDVBuff.RealCLI.Cli;
using IDVBuff.RealCLI.Output;
using IDVBuff.RealCLI.Stubs;
using Microsoft.UI.Dispatching;

namespace IDVBuff.RealCLI;

internal static partial class ScanBenchmarkCommand
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"
    };

    public static async Task<int> RunAsync(string[] args, DispatcherQueue dispatcher)
    {
        string? manifestPath = null;
        string? dirPath = null;
        string? pattern = null;
        string? expectedOverride = null;
        string? mapClassOverride = null;
        string? settingsOverride = null;
        string? outputPath = null;
        var inferMap = false;
        var recursive = true;
        var prewarmVpsg3 = true;
        var prewarmTimeoutSec = 20;
        var isolated = false;
        var consume = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--manifest":
                case "-m":
                    manifestPath = args[++i]; break;
                case "--dir":
                case "-d":
                    dirPath = args[++i]; break;
                case "--pattern":
                case "-p":
                    pattern = args[++i]; break;
                case "--expected":
                case "-e":
                    expectedOverride = args[++i]; break;
                case "--infer-map":
                    inferMap = true; break;
                case "--consume":
                    consume = true; break;
                case "--no-recursive":
                    recursive = false; break;
                case "--mapclass":
                case "-c":
                    mapClassOverride = args[++i]; break;
                case "--settings":
                case "-s":
                    settingsOverride = args[++i]; break;
                case "--out":
                case "-o":
                    outputPath = args[++i]; break;
                case "--prewarm-timeout":
                    prewarmTimeoutSec = int.Parse(args[++i]); break;
                case "--no-prewarm":
                    prewarmVpsg3 = false; break;
                case "--fresh":
                case "--isolated":
                    isolated = true; break;
            }
        }

        if (string.IsNullOrWhiteSpace(manifestPath) && string.IsNullOrWhiteSpace(dirPath))
        {
            Console.Error.WriteLine("错误：必须指定 --manifest <path> 或 --dir <path> 参数。");
            PrintBenchmarkUsage();
            return 1;
        }

        // 解析测试案例集
        List<BenchmarkCaseInput> cases;
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"错误：manifest 文件不存在 —— {manifestPath}");
                return 1;
            }
            cases = await LoadManifestCasesAsync(manifestPath, mapClassOverride, expectedOverride);
        }
        else
        {
            if (!Directory.Exists(dirPath))
            {
                Console.Error.WriteLine($"错误：目录不存在 —— {dirPath}");
                return 1;
            }
            cases = DiscoverDirectoryCases(dirPath!, pattern, recursive, expectedOverride, inferMap);
        }

        if (cases.Count == 0)
        {
            Console.Error.WriteLine("未找到可测试的截图样本。");
            return 1;
        }

        Console.Error.WriteLine("================================================================================");
        Console.Error.WriteLine($"[IDVB Benchmark] 开始扫描基准测试 · 样本总数: {cases.Count} · 模式: {(isolated ? "独立会话" : "常驻会话")}");
        Console.Error.WriteLine("================================================================================");

        var report = await ExecuteBenchmarkAsync(
            cases,
            dispatcher,
            settingsOverride,
            mapClassOverride,
            prewarmVpsg3,
            prewarmTimeoutSec,
            isolated,
            consume);

        // 控制台打印汇总报告
        PrintBenchmarkSummary(report);

        // 若指定输出文件，写入完整结构化结果
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            await RealCliOutputWriter.WriteObjectAsync(report, outputPath);
            Console.Error.WriteLine($"\n完整诊断报告已保存至：{outputPath}");
        }

        return report.MismatchedCount == 0 && report.UnrecognizedCount == 0 ? 0 : 1;
    }

    private static async Task<RealCliBenchmarkReport> ExecuteBenchmarkAsync(
        List<BenchmarkCaseInput> cases,
        DispatcherQueue dispatcher,
        string? settingsRoot,
        string? defaultMapClass,
        bool prewarmVpsg3,
        int prewarmTimeoutSec,
        bool isolated,
        bool consume)
    {
        var caseResults = new List<RealCliBenchmarkCaseResult>(cases.Count);
        var phaseTimingsTotal = new Dictionary<string, (double Sum, int Count)>(StringComparer.OrdinalIgnoreCase);

        // 如果非 isolated 模式，装配常驻 SessionOrchestrator
        SessionOrchestrator? sharedOrchestrator = null;
        FileBasedCapture? sharedCapture = null;
        RecordingOverlayWindow? sharedOverlay = null;

        try
        {
            if (!isolated)
            {
                var firstImage = cases[0].ImagePath;
                sharedOrchestrator = OrchestratorFactory.BuildOrchestrator(
                    dispatcher,
                    firstImage,
                    settingsRoot,
                    out sharedOverlay,
                    out sharedCapture);

                await sharedOrchestrator.InitializeAsync();
                await sharedOrchestrator.SetCollectLogsAsync(true);
                sharedOrchestrator.LogCollector.IsEnabled = true;

                if (prewarmVpsg3)
                {
                    Console.Error.Write($"[Benchmark] 正在预热 VPSG 3.0 空间索引 (超时: {prewarmTimeoutSec}s)... ");
                    var ready = await sharedOrchestrator.WaitForVpsg3PreparedAsync(TimeSpan.FromSeconds(prewarmTimeoutSec));
                    Console.Error.WriteLine(ready ? "已就绪" : "超时（部分或冷启动中）");
                }
            }

            var benchSw = Stopwatch.StartNew();

            for (var i = 0; i < cases.Count; i++)
            {
                var input = cases[i];
                var fileName = Path.GetFileName(input.ImagePath);
                var caseTargetMapClass = input.MapClass ?? defaultMapClass ?? "S0 厄运之女 · 噩梦（爱吃醋）";

                RealCliSessionResult sessionResult;

                if (isolated)
                {
                    var orchestrator = OrchestratorFactory.BuildOrchestrator(
                        dispatcher,
                        input.ImagePath,
                        settingsRoot,
                        out var overlay,
                        out _);

                    sessionResult = await RealCliRunner.RunRecognitionAsync(
                        orchestrator,
                        overlay,
                        input.ImagePath,
                        consume: consume,
                        expectedMap: input.ExpectedMap,
                        mapClass: caseTargetMapClass,
                        prewarmVpsg3: prewarmVpsg3 && i == 0,
                        prewarmTimeout: TimeSpan.FromSeconds(prewarmTimeoutSec));
                }
                else
                {
                    sharedCapture!.SetImagePath(input.ImagePath);

                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await sharedOrchestrator!.BeginMatchAsync(caseTargetMapClass);
                        await sharedOrchestrator.RunQuickScanAsync();
                        if (consume && sharedOrchestrator.IsBackgroundScanCompleted)
                        {
                            sharedOrchestrator.SynchronizeExternalGameMapState(true);
                            await sharedOrchestrator.ConsumeBackgroundScanAsync();
                        }
                        sw.Stop();

                        sessionResult = SessionResultBuilder.ExtractResult(
                            sharedOrchestrator,
                            sharedOverlay!,
                            input.ImagePath,
                            sw.Elapsed.TotalMilliseconds,
                            null,
                            input.ExpectedMap);

                        await sharedOrchestrator.EndMatchAsync();
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        sessionResult = new RealCliSessionResult
                        {
                            ImagePath = input.ImagePath,
                            Succeeded = false,
                            StatusMessage = $"Fatal 异常：{ex.Message}",
                            FatalError = ex.ToString(),
                            TotalWallMs = sw.Elapsed.TotalMilliseconds,
                            VerificationResult = "Unrecognized",
                            IsMatchCorrect = false
                        };
                    }
                }

                // 记录阶段耗时统计
                if (sessionResult.ScanPhaseTimings != null)
                {
                    foreach (var (phase, ms) in sessionResult.ScanPhaseTimings)
                    {
                        if (!phaseTimingsTotal.TryGetValue(phase, out var val))
                            phaseTimingsTotal[phase] = (ms, 1);
                        else
                            phaseTimingsTotal[phase] = (val.Sum + ms, val.Count + 1);
                    }
                }

                var vpsg = sessionResult.Vpsg3Diagnostics;
                var vpsgAttempted = vpsg?.Attempted ?? false;
                var vpsgAccepted = vpsg?.IsAccepted ?? false;
                var vpsgFallback = vpsg?.FallbackReason;

                var caseResult = new RealCliBenchmarkCaseResult
                {
                    Index = i + 1,
                    ImagePath = input.ImagePath,
                    ExpectedMap = input.ExpectedMap,
                    ExpectedFloor = input.ExpectedFloor,
                    RecognizedMap = sessionResult.Recognition?.MapDisplayName,
                    RecognizedFloor = sessionResult.Recognition?.Floor,
                    Confidence = sessionResult.Recognition?.Confidence ?? 0d,
                    VerificationResult = sessionResult.VerificationResult,
                    IsMatchCorrect = sessionResult.IsMatchCorrect,
                    TotalWallMs = sessionResult.TotalWallMs,
                    Vpsg3Attempted = vpsgAttempted,
                    Vpsg3Accepted = vpsgAccepted,
                    Vpsg3FallbackReason = vpsgFallback,
                    CodeExecutionPath = sessionResult.CodeExecutionPath ?? [],
                    KeyEvents = sessionResult.KeyEvents ?? [],
                    SessionResult = sessionResult
                };

                caseResults.Add(caseResult);

                // 实时打印每个案例的单行进度
                PrintProgressRow(i + 1, cases.Count, fileName, caseResult);
            }

            benchSw.Stop();

            // 计算汇总指标
            var totalCases = caseResults.Count;
            var matched = caseResults.Count(c => c.VerificationResult == "Matched");
            var mismatched = caseResults.Count(c => c.VerificationResult == "Mismatched");
            var unrecognized = caseResults.Count(c => c.VerificationResult == "Unrecognized");
            var matchAccuracy = totalCases > 0 ? (double)matched / totalCases : 0d;

            var wallTimes = caseResults.Select(c => c.TotalWallMs).OrderBy(t => t).ToArray();
            var avgWall = wallTimes.Length > 0 ? wallTimes.Average() : 0d;
            var minWall = wallTimes.Length > 0 ? wallTimes.First() : 0d;
            var maxWall = wallTimes.Length > 0 ? wallTimes.Last() : 0d;
            var p50Wall = Percentile(wallTimes, 0.50);
            var p90Wall = Percentile(wallTimes, 0.90);

            var vpsgAttempts = caseResults.Count(c => c.Vpsg3Attempted);
            var vpsgAccepts = caseResults.Count(c => c.Vpsg3Accepted);
            var vpsgSuccessRate = vpsgAttempts > 0 ? (double)vpsgAccepts / vpsgAttempts : 0d;
            var vpsgWallTimes = caseResults
                .Where(c => c.Vpsg3Attempted && c.SessionResult.Vpsg3Diagnostics?.DurationMs > 0)
                .Select(c => c.SessionResult.Vpsg3Diagnostics!.DurationMs)
                .ToArray();
            var avgVpsgWall = vpsgWallTimes.Length > 0 ? vpsgWallTimes.Average() : 0d;

            var avgPhaseTimings = phaseTimingsTotal.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Count > 0 ? kv.Value.Sum / kv.Value.Count : 0d);

            var failedCases = caseResults.Where(c => !c.IsMatchCorrect).ToList();

            return new RealCliBenchmarkReport
            {
                TotalCases = totalCases,
                MatchedCount = matched,
                MismatchedCount = mismatched,
                UnrecognizedCount = unrecognized,
                MatchAccuracy = matchAccuracy,
                AverageWallMs = avgWall,
                MinWallMs = minWall,
                MaxWallMs = maxWall,
                P50WallMs = p50Wall,
                P90WallMs = p90Wall,
                Vpsg3AttemptedCount = vpsgAttempts,
                Vpsg3AcceptedCount = vpsgAccepts,
                Vpsg3SuccessRate = vpsgSuccessRate,
                Vpsg3AverageWallMs = avgVpsgWall,
                AveragePhaseTimings = avgPhaseTimings,
                Cases = caseResults,
                FailedCases = failedCases
            };
        }
        finally
        {
            if (sharedOrchestrator != null)
                await sharedOrchestrator.DisposeAsync();
        }
    }
}
