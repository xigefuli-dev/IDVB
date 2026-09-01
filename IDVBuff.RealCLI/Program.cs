// IDVB Real CLI — 真正驱动 IDVB 的集成测试 CLI
//
// 核心原则：Real CLI 只做数据搬运工——投喂截图 → 触发 IDVB → 收集结果。
// 绝不模仿任何 IDVB 内部逻辑，绝不复刻管线。
//
// 与 Probe CLI 的本质区别：
//   Probe: 绕过 SessionOrchestrator，自己拼装算法组件 → 谎言测试
//   Real:  通过 DI 容器 + Stub IO 接口，走完整的 SessionOrchestrator → 真实测试

using IDVBuff;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using IDVBuff.Infrastructure.Configuration;
using IDVBuff.Pipeline;
using IDVBuff.RealCLI.Cli;
using IDVBuff.RealCLI.Output;
using IDVBuff.RealCLI.Stubs;
using IDVBuff.RealCLI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using System.Diagnostics;

// ── DispatcherQueue 初始化 ──
// 控制台应用没有 WinUI 消息泵，使用 DispatcherQueueController 创建同步调度器。
// SessionOrchestrator 仅通过 _dispatcher.TryEnqueue() 派发 IGlobalInput 事件回调；
// NoopGlobalInput 永不触发事件，因此无需真正的消息循环。
var controller = DispatcherQueueController.CreateOnCurrentThread();
var dispatcher = controller.DispatcherQueue;

// ── CLI 参数解析 ──
if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

var command = args[0].ToLowerInvariant();
return command switch
{
    "run" => await RunSingleAsync(args[1..], dispatcher),
    "batch" => await RunBatchAsync(args[1..], dispatcher),
    "mapopen" => await MapOpenCommand.RunAsync(args[1..], dispatcher),
    "mapopen-replay" => await MapOpenReplayCommand.RunAsync(args[1..], dispatcher),
    "model-train" => await ModelTrainCommand.RunAsync(args[1..]),
    "model-replay" => await ModelReplayCommand.RunAsync(args[1..], dispatcher),
    "model-device" => await ModelDeviceCommand.RunAsync(args[1..]),
    "model-memory-test" => await ModelMemoryTestCommand.RunAsync(args[1..]),
    "survey" => await SurveyReplayCommand.RunAsync(args[1..]),
    _ => UnknownCommand(command)
};

// ════════════════════════════════════════════════════════════════
// run 命令：单张截图识别
// ════════════════════════════════════════════════════════════════

static async Task<int> RunSingleAsync(string[] args, DispatcherQueue dispatcher)
{
    string? imagePath = null;
    string? outputPath = null;
    string? settingsRoot = null;
    var consume = false;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--image":
            case "-i":
                imagePath = args[++i]; break;
            case "--out":
            case "-o":
                outputPath = args[++i]; break;
            case "--settings":
            case "-s":
                settingsRoot = args[++i]; break;
            case "--consume":
                consume = true; break;
        }
    }

    if (string.IsNullOrWhiteSpace(imagePath))
    {
        Console.Error.WriteLine("错误：缺少 --image <path> 参数。");
        return 1;
    }
    if (!File.Exists(imagePath))
    {
        Console.Error.WriteLine($"错误：文件不存在 —— {imagePath}");
        return 1;
    }

    try
    {
        var orchestrator = OrchestratorFactory.BuildOrchestrator(dispatcher, imagePath, settingsRoot, out var overlay);
        var result = await RunRecognitionAsync(orchestrator, overlay, imagePath, consume);

        if (outputPath is not null)
            await RealCliOutputWriter.WriteAsync(result, outputPath);
        else
            RealCliOutputWriter.WriteLine(result);

        return result.Succeeded ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Fatal 异常：{ex}");
        return 2;
    }
}

// ════════════════════════════════════════════════════════════════
// batch 命令：批量截图识别
// ════════════════════════════════════════════════════════════════

static async Task<int> RunBatchAsync(string[] args, DispatcherQueue dispatcher)
{
    string? glob = null;
    string? outputPath = null;
    string? settingsRoot = null;
    var parallel = 1;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--files":
            case "-f":
                glob = args[++i]; break;
            case "--out":
            case "-o":
                outputPath = args[++i]; break;
            case "--settings":
            case "-s":
                settingsRoot = args[++i]; break;
            case "--parallel":
            case "-p":
                parallel = int.Parse(args[++i]); break;
        }
    }

    if (string.IsNullOrWhiteSpace(glob))
    {
        Console.Error.WriteLine("错误：缺少 --files <glob> 参数。");
        return 1;
    }

    var files = ResolveGlob(glob);
    if (files.Length == 0)
    {
        Console.Error.WriteLine("glob 未匹配到任何文件。");
        return 1;
    }

    Console.Error.WriteLine($"Real CLI 批量评估：{files.Length} 个文件，并行度={parallel}");

    var sw = Stopwatch.StartNew();
    var results = new List<RealCliSessionResult>(files.Length);

    if (parallel <= 1)
    {
        for (var i = 0; i < files.Length; i++)
        {
            var file = files[i];
            Console.Error.WriteLine($"[{i + 1}/{files.Length}] {Path.GetFileName(file)}");
            var orchestrator = OrchestratorFactory.BuildOrchestrator(dispatcher, file, settingsRoot, out var overlay);
            var result = await RunRecognitionAsync(orchestrator, overlay, file);
            results.Add(result);
        }
    }
    else
    {
        var semaphore = new SemaphoreSlim(parallel);
        var tasks = files.Select(async file =>
        {
            await semaphore.WaitAsync();
            try
            {
                Console.Error.WriteLine($"[并行] {Path.GetFileName(file)}");
                var orchestrator = OrchestratorFactory.BuildOrchestrator(dispatcher, file, settingsRoot, out var overlay);
                return await RunRecognitionAsync(orchestrator, overlay, file);
            }
            finally { semaphore.Release(); }
        });
        results.AddRange(await Task.WhenAll(tasks));
    }

    sw.Stop();

    var succeeded = results.Count(r => r.Succeeded);
    var confidences = results.Where(r => r.Recognition is not null).Select(r => r.Recognition!.Confidence).ToArray();
    var avgConfidence = confidences.Length > 0 ? confidences.Average() : 0d;

    var summary = new RealCliBatchSummary
    {
        TotalFiles = files.Length,
        Succeeded = succeeded,
        Failed = files.Length - succeeded,
        AverageConfidence = avgConfidence,
        AverageWallMs = results.Count > 0 ? results.Average(r => r.TotalWallMs) : 0d,
        Results = results
    };

    if (outputPath is not null)
    {
        await RealCliOutputWriter.WriteBatchAsync(summary, outputPath);
        Console.Error.WriteLine($"汇总已保存：{outputPath}");
    }
    else
    {
        RealCliOutputWriter.WriteLine(results.FirstOrDefault()!);
    }

    Console.Error.WriteLine($"Real CLI 批量完成：{succeeded}/{files.Length} 成功，"
        + $"平均置信度={avgConfidence:F3}，总耗时={sw.Elapsed.TotalMilliseconds:F0}ms");

    return succeeded == files.Length ? 0 : 1;
}

// ════════════════════════════════════════════════════════════════
// 核心：驱动 SessionOrchestrator 执行完整识别管线
// ════════════════════════════════════════════════════════════════

static async Task<RealCliSessionResult> RunRecognitionAsync(
    SessionOrchestrator orchestrator,
    RecordingOverlayWindow overlay,
    string imagePath,
    bool consume = false)
{
    var sw = Stopwatch.StartNew();

    try
    {
        // 初始化：加载设置、预热缓存、检查完整性
        await orchestrator.InitializeAsync();
        // Real CLI 也遵循产品生命周期：扫描必须发生在进入对局之后。
        // S1 是 MapMatchSession 的兼容默认分组；截图仍由 CLI 的文件捕获器提供。
        await orchestrator.BeginMatchAsync(
            orchestrator.Settings.LastSelectedMapClass
            ?? "S0 厄运之女 · 噩梦（爱吃醋）");

        // 🔥 这就是真实的 IDVB 识别管线
        // SessionOrchestrator.RunQuickScanAsync() 内部调用：
        //   RunRecognitionPipelineAsync()
        //     → RunRecognitionPipelineCoreAsync()
        //       → IGameWindowCapture.TryCaptureViewport()  ← FileBasedCapture（来自文件）
        //       → PipelineFactory.CreateScanPipeline()    ← 真实 ScanPipeline
        //       → MapCvAlignmentService.AlignSelectedCore() ← 真实对齐引擎
        //       → IOverlayWindow.UpdateMap()              ← RecordingOverlayWindow（记录）
        //       → IOverlayWindow.Show()                   ← RecordingOverlayWindow（记录）
        await orchestrator.RunQuickScanAsync();

        // 后台扫描 E2E：若 --consume，且后台扫描已完成未消费，则公开缝合点
        // 消费结果（headless 下候选窗自动选可靠项、PlayerDecidesScale 默认 false 跳过缩放）。
        // 消费缝合点要求游戏地图处于打开状态（模拟玩家按下地图键）——后台扫描
        // 完成后不再预置地图为打开，CLI 需显式同步。
        if (consume)
        {
            orchestrator.SynchronizeExternalGameMapState(true);
            await orchestrator.ConsumeBackgroundScanAsync();
        }

        sw.Stop();

        // 收集结果
        var result = ExtractResult(orchestrator, overlay, imagePath, sw.Elapsed.TotalMilliseconds, null);
        await orchestrator.EndMatchAsync();
        return result;
    }
    catch (Exception ex)
    {
        sw.Stop();
        return new RealCliSessionResult
        {
            ImagePath = imagePath,
            Succeeded = false,
            StatusMessage = $"Fatal 异常：{ex.Message}",
            FatalError = ex.ToString(),
            TotalWallMs = sw.Elapsed.TotalMilliseconds
        };
    }
    finally
    {
        await orchestrator.DisposeAsync();
    }
}

// ════════════════════════════════════════════════════════════════
// 从 SessionOrchestrator 提取结果（只读属性访问，不走任何识别逻辑）
// ════════════════════════════════════════════════════════════════

static RealCliSessionResult ExtractResult(
    SessionOrchestrator orchestrator,
    RecordingOverlayWindow overlay,
    string imagePath,
    double totalMs,
    string? error)
{
    var rec = orchestrator.LastRecognition;

    // 扫描管线各阶段耗时
    var scanPhaseTimings = orchestrator.LastScanPhaseTimings?
        .ToDictionary(kv => kv.Key, kv => kv.Value);

    return new RealCliSessionResult
    {
        ImagePath = imagePath,
        Succeeded = rec is not null,
        StatusMessage = orchestrator.StatusMessage,
        Recognition = SessionResultBuilder.BuildRecognition(orchestrator),
        FailureReason = rec is null ? (orchestrator.StatusMessage ?? "识别失败：无结果") : null,
        BackgroundScanStatus = orchestrator.BackgroundScanStatus.ToString(),
        IsBackgroundScanCompleted = orchestrator.IsBackgroundScanCompleted,
        OverlayEvents = overlay.Events.ToList(),
        AlignmentSession = SessionResultBuilder.BuildAlignmentSession(orchestrator),
        ScanPhaseTimings = scanPhaseTimings,
        Diagnostics = SessionResultBuilder.BuildDiagnostics(orchestrator),
        LogEntries = SessionResultBuilder.BuildLogEntries(orchestrator),
        TotalWallMs = totalMs,
        FatalError = error
    };
}

// ════════════════════════════════════════════════════════════════
// 工具函数
// ════════════════════════════════════════════════════════════════

static string[] ResolveGlob(string pattern)
{
    if (string.IsNullOrWhiteSpace(pattern)) return [];

    var directory = Path.GetDirectoryName(pattern);
    var filePattern = Path.GetFileName(pattern);

    if (string.IsNullOrWhiteSpace(directory) || directory == ".")
        directory = Environment.CurrentDirectory;

    if (!Directory.Exists(directory)) return [];

    var searchOption = pattern.Contains("**")
        ? SearchOption.AllDirectories
        : SearchOption.TopDirectoryOnly;

    var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif" };

    return Directory.GetFiles(Path.GetFullPath(directory), filePattern, searchOption)
        .Where(f => imageExtensions.Contains(Path.GetExtension(f)))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"未知命令：{command}");
    Console.Error.WriteLine("可用命令：run | batch | mapopen | mapopen-replay | model-train | model-replay | model-device | model-memory-test | survey");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        IDVB.RealCLI — 真正驱动 IDVB 的集成测试 CLI

        用法：
          IDVB.RealCLI.exe run --image <path> [--out <path>] [--settings <path>]
          IDVB.RealCLI.exe batch --files <glob> [--parallel N] [--out <path>]
          IDVB.RealCLI.exe mapopen --image <path> [--candidate N] [--out <path>] [--settings <path>]
          IDVB.RealCLI.exe mapopen-replay --manifest <path> [--out <path>] [--settings <path>]
          IDVB.RealCLI.exe model-train --manifest <path> --repository <path> [--out <path>]
          IDVB.RealCLI.exe model-replay --manifest <path> --repository <path> --mode <mode> [--out <path>]

        run 命令：
          --image, -i <path>    输入截图路径（必需）
          --out, -o <path>      输出 JSON 路径（可选，默认 stdout）
          --settings, -s <path> 自定义 settings.json 目录（可选）
          --consume             后台扫描完成后立即消费（仅识别→对齐提交 E2E）

        batch 命令：
          --files, -f <glob>    文件匹配模式（必需，如 "samples/**/*.png"）
          --parallel, -p N      并行度（默认 1）
          --out, -o <path>      汇总 JSON 输出路径
          --settings, -s <path> 自定义 settings.json 目录

        mapopen 命令（仅对齐 E2E：先锁定 → 关图 → 重开 → 仅对齐）：
          --image, -i <path>    输入截图路径（必需）
          --candidate, -c N     强制选择第 N 个候选（1-based，可选）
          --out, -o <path>      输出 JSON 路径（可选，默认 stdout）
          --settings, -s <path> 自定义 settings.json 目录（可选）

        mapopen-replay 命令（manifest 驱动的多案例完整 SessionOrchestrator E2E）：
          --manifest, -m <path> manifest 路径（必需，图片路径可相对 manifest）
          --out, -o <path>      输出 JSON 路径（可选，默认 stdout）
          --settings, -s <path> 覆盖 manifest 中的 settingsRoot（可选）

        示例：
          IDVB.RealCLI.exe run --image screenshot.png
          IDVB.RealCLI.exe run --image screenshot.png --out result.json
          IDVB.RealCLI.exe batch --files "samples/**/*.png" --parallel 4 --out summary.json
          IDVB.RealCLI.exe mapopen --image two_gate.png --candidate 1 --out mapopen.json
        """);
}
