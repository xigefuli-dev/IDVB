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
using System.Text;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var access = await IDVBuff.Features.Accounts.VersionAccessClient.CheckAsync(BuildVersionInfo.ProductVersion);
if (!access.Allowed) { Console.Error.WriteLine(access.Message); return 1; }
_ = IDVBuff.Features.Accounts.VersionAccessClient.MonitorHeadlessAsync(BuildVersionInfo.ProductVersion);

if (!IDVBuff.Lifecycle.UsageNotice.IsAccepted())
{
    Console.Error.WriteLine("请先正常启动 Identity Vision Bridge，阅读并确认软件性质及使用责任声明。");
    return 1;
}

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
    "bench" or "benchmark" => await ScanBenchmarkCommand.RunAsync(args[1..], dispatcher),
    "batch" => await RunBatchAsync(args[1..], dispatcher),
    "mapopen" => await MapOpenCommand.RunAsync(args[1..], dispatcher),
    "mapopen-replay" => await MapOpenReplayCommand.RunAsync(args[1..], dispatcher),
    "model-train" => await ModelTrainCommand.RunAsync(args[1..]),
    "model-replay" => await ModelReplayCommand.RunAsync(args[1..], dispatcher),
    "model-device" => await ModelDeviceCommand.RunAsync(args[1..]),
    "model-memory-test" => await ModelMemoryTestCommand.RunAsync(args[1..]),
    "map-package" => await MapPackageCommand.RunAsync(args[1..]),
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
    string? expectedMap = null;
    string? mapClass = null;
    var prewarmVpsg3 = false;
    var prewarmTimeoutSec = 15;
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
            case "--expected":
            case "-e":
                expectedMap = args[++i]; break;
            case "--mapclass":
            case "-c":
                mapClass = args[++i]; break;
            case "--prewarm-vpsg3":
                prewarmVpsg3 = true; break;
            case "--prewarm-timeout":
                prewarmTimeoutSec = int.Parse(args[++i]); break;
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
        var result = await RealCliRunner.RunRecognitionAsync(
            orchestrator,
            overlay,
            imagePath,
            consume: consume,
            expectedMap: expectedMap,
            mapClass: mapClass,
            prewarmVpsg3: prewarmVpsg3,
            prewarmTimeout: TimeSpan.FromSeconds(prewarmTimeoutSec));

        // 控制台诊断摘要输出到 stderr（保证 stdout 纯净输出结构化 JSON）
        var statusStr = result.IsMatchCorrect ? "验证匹配成功" : result.Succeeded ? "识别完成" : "识别失败";
        Console.Error.WriteLine($"[RealCLI] 扫描诊断：{statusStr} · 耗时 {result.TotalWallMs:F1}ms");
        if (result.Recognition is { } rec)
        {
            Console.Error.WriteLine($"  实际识别: {rec.MapDisplayName} (楼层: {rec.Floor}, 置信度: {rec.Confidence:P1})");
        }
        if (result.ExpectedMap is { } exp)
        {
            Console.Error.WriteLine($"  预期地图: {exp.MapDisplayName ?? exp.MapId} | 校验结论: {result.VerificationResult}");
        }
        if (result.Vpsg3Diagnostics is { Attempted: true } vpsg)
        {
            Console.Error.WriteLine($"  VPSG 3.0: {(vpsg.IsAccepted ? "快速对齐通过" : $"对齐降级 ({vpsg.FallbackReason ?? "未达标"})")} (点数: {vpsg.SparsePointCount}, HitsK5: {vpsg.HitsK5}, Scale: {vpsg.Scale:F3})");
        }
        if (result.CodeExecutionPath is { Count: > 0 } path)
        {
            Console.Error.WriteLine($"  执行路径: {string.Join(" -> ", path.Select(p => p.Split(' ')[0]))}");
        }

        if (outputPath is not null)
            await RealCliOutputWriter.WriteAsync(result, outputPath);
        else
            RealCliOutputWriter.WriteLine(result);

        if (!string.IsNullOrWhiteSpace(expectedMap))
            return result.IsMatchCorrect ? 0 : 1;

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

static Task<RealCliSessionResult> RunRecognitionAsync(
    SessionOrchestrator orchestrator,
    RecordingOverlayWindow overlay,
    string imagePath,
    bool consume = false) =>
    RealCliRunner.RunRecognitionAsync(orchestrator, overlay, imagePath, consume);

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
    Console.Error.WriteLine("可用命令：run | bench | batch | mapopen | mapopen-replay | model-train | model-replay | model-device | model-memory-test | map-package | survey");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        IDVB.RealCLI — 真正驱动 IDVB 的集成测试与诊断 CLI

        用法：
          IDVB.RealCLI.exe run --image <path> [--expected <map>] [--out <path>] [--settings <path>]
          IDVB.RealCLI.exe bench --dir <directory> [--expected <map>] [--infer-map] [--out <path>]
          IDVB.RealCLI.exe bench --manifest <manifest.json> [--out <path>]
          IDVB.RealCLI.exe batch --files <glob> [--parallel N] [--out <path>]
          IDVB.RealCLI.exe mapopen --image <path> [--candidate N] [--out <path>] [--settings <path>]
          IDVB.RealCLI.exe mapopen-replay --manifest <path> [--out <path>] [--settings <path>]
          IDVB.RealCLI.exe model-train --manifest <path> --repository <path> [--out <path>]
          IDVB.RealCLI.exe model-replay --manifest <path> --repository <path> --mode <mode> [--out <path>]
          IDVB.RealCLI.exe map-package --manifest <portable-manifest.json> --out <fresh-output-root>

        run 命令：
          --image, -i <path>    输入截图路径（必需）
          --expected, -e <map>  预期地图名称/序号/Guid（用于自动校验 Ground Truth）
          --out, -o <path>      输出 JSON 路径（可选，默认 stdout）
          --settings, -s <path> 自定义 settings.json 目录（可选）
          --mapclass, -c <pool> 指定段位地图池（可选）
          --prewarm-vpsg3       等待 VPSG 3.0 空间索引构建完毕后再扫描
          --consume             后台扫描完成后立即消费（仅识别→对齐提交 E2E）

        bench 命令（批量扫描诊断与"烤机"测试）：
          --dir, -d <path>      待测试截图目录（支持递归查找所有子目录）
          --manifest, -m <path> 测试清单 JSON 文件
          --expected, -e <map>  覆盖指定目录所有截图的预期地图
          --infer-map           自动从文件夹或文件名推导预期地图（如 "红教堂_01.png"）
          --out, -o <path>      保存基准测试汇总 JSON 报告
          --prewarm-timeout N   VPSG 3.0 索引就绪等待超时秒数（默认 20s）
          --fresh, --isolated   每个样本使用独立 DI 容器（默认常驻会话模式）

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
          IDVB.RealCLI.exe run --image screenshot.png --expected 红教堂
          IDVB.RealCLI.exe bench --dir ./recordings --infer-map --out bench_report.json
          IDVB.RealCLI.exe bench --manifest ./dataset.json --out bench_report.json
        """);
}
