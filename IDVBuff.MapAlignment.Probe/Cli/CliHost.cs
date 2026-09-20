using System.Diagnostics;
using IDVBuff.Features.Maps;
using IDVBuff.MapAlignment.Probe.Batch;
using IDVBuff.MapAlignment.Probe.Config;
using IDVBuff.MapAlignment.Probe.Output;
using IDVBuff.MapAlignment.Probe.Pipeline;
using IDVBuff.MapAlignment.Probe.Pipeline.DualGate;
using IDVBuff.MapAlignment.Probe.Pipeline.Floor;
using IDVBuff.MapAlignment.Probe.Pipeline.Gates;
using IDVBuff.MapAlignment.Probe.Pipeline.SideEntrance;
using IDVBuff.MapAlignment.Probe.Pipeline.StructureFill;

namespace IDVBuff.MapAlignment.Probe.Cli;

/// <summary>
/// CLI 命令分发器。解析 args[0] 为策略名、args[1] 为命令名，
/// 路由到对应的 IPipelineStrategy 或批处理 / config 命令。
/// </summary>
public static class CliHost
{
    private static readonly PipelineRegistry Registry = new([
        new DualGatePipelineStrategy(),
        new SideEntrancePipelineStrategy(),
        new GateDetectionPipelineStrategy(),
        new FloorRecognitionPipelineStrategy(),
        new StructureFillPipelineStrategy()
    ]);

    public static async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var firstArg = args[0].ToLowerInvariant();

            // batch 模式：idvb-probe.exe batch <strategy> --files <glob> [...]
            if (firstArg == "batch")
                return await HandleBatchAsync(args);

            // config 模式（占位符）
            if (firstArg == "config")
                return await HandleConfigAsync(args);

            if (firstArg == "structure-artifacts")
                return HandleStructureArtifacts(args);

            // 策略 + 命令模式：idvb-probe.exe <strategy> <command> [...]
            if (args.Length < 2)
            {
                Console.Error.WriteLine($"用法：idvb-probe.exe {firstArg} <command> [options]");
                Console.Error.WriteLine("可用命令：run | detect | rank");
                return 1;
            }

            var strategy = Registry.Find(firstArg);
            if (strategy is null)
            {
                Console.Error.WriteLine($"未知策略：{firstArg}");
                Console.Error.WriteLine($"可用策略：{string.Join(" | ", Registry.StrategyNames)}");
                return 1;
            }

            var command = args[1].ToLowerInvariant();
            return command switch
            {
                "run" => await HandleRunAsync(strategy, args),
                "detect" => HandleNotImplemented("detect", firstArg),
                "rank" => HandleNotImplemented("rank", firstArg),
                _ => HandleNotImplemented(command, firstArg)
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<int> HandleRunAsync(
        IPipelineStrategy strategy,
        string[] allArgs)
    {
        // 跳过策略名和命令名，仅解析选项
        var cliOptions = CliOptions.Parse(allArgs[2..]);
        return await ExecuteStrategyAsync(strategy, cliOptions, CancellationToken.None);
    }

    private static async Task<int> ExecuteStrategyAsync(
        IPipelineStrategy strategy,
        CliOptions cliOptions,
        CancellationToken ct)
    {
        // 验证必需参数
        if (string.IsNullOrWhiteSpace(cliOptions.Image))
        {
            Console.Error.WriteLine("缺少 --image（输入图片路径）。");
            return 1;
        }
        if (!File.Exists(cliOptions.Image))
        {
            Console.Error.WriteLine($"图片不存在：{cliOptions.Image}");
            return 1;
        }

        var context = new ProbeContext
        {
            ImagePath = Path.GetFullPath(cliOptions.Image),
            OutputPath = cliOptions.Out is not null ? Path.GetFullPath(cliOptions.Out) : null,
            StructureFillOutputPath = cliOptions.MaskOut is not null
                ? Path.GetFullPath(cliOptions.MaskOut)
                : strategy.StrategyName.Equals(
                    "structure-fill",
                    StringComparison.OrdinalIgnoreCase)
                    && cliOptions.Out is not null
                    && Path.GetExtension(cliOptions.Out).Equals(
                        ".png",
                        StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFullPath(cliOptions.Out)
                        : null,
            StructureFillOutputDirectory = cliOptions.MaskDirectory is not null
                ? Path.GetFullPath(cliOptions.MaskDirectory)
                : null,
            StructureFillGuideMap = cliOptions.GuideMap,
            SettingsPath = cliOptions.Settings,
            GateTemplatePath = cliOptions.Gate ?? string.Empty,
            ViewportMargin = cliOptions.ViewportMargin,
            UseFullFrame = cliOptions.Full,
            EnableStructure = cliOptions.Structure,
            EnableEcc = cliOptions.Ecc,
            ForceBestCandidate = cliOptions.ForceBest,
            TopCount = cliOptions.Top,
            TopCandidates = cliOptions.TopCandidates,
            DownscaleFactor = cliOptions.Downscale,
            ClientWidth = cliOptions.ClientWidth,
            GateThreshold = cliOptions.Threshold > 0d
                ? cliOptions.Threshold
                : MapRecognitionTuning.DefaultGateTemplateThreshold,
            SideScanTop = cliOptions.SideTop,
            SideScanMapId = cliOptions.SideMapId,
            FirstFloorTemplatePath = cliOptions.First ?? string.Empty,
            SecondFloorTemplatePath = cliOptions.Second ?? string.Empty
        };

        // 从 settings.json 合并配置
        await TomlConfigLoader.ApplyToContextAsync(context, cliOptions.Settings);

        // 解析 --viewport (x,y,w,h)
        if (!string.IsNullOrWhiteSpace(cliOptions.Viewport))
        {
            var parts = cliOptions.Viewport.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 4
                && double.TryParse(parts[0], out var vx)
                && double.TryParse(parts[1], out var vy)
                && double.TryParse(parts[2], out var vw)
                && double.TryParse(parts[3], out var vh))
            {
                context.ViewportRegion = new NormalizedRectangle
                    { X = vx, Y = vy, Width = vw, Height = vh };
            }
        }

        var result = await strategy.RunAsync(context, ct);
        return result.Succeeded ? 0 : 1;
    }

    private static async Task<int> HandleBatchAsync(string[] args)
    {
        // 格式：idvb-probe.exe batch <strategy> --files <glob> [options]
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：idvb-probe.exe batch <strategy> --files <glob> [options]");
            Console.Error.WriteLine("示例：idvb-probe.exe batch dual-gate --files \"samples/**/*.png\" --structure");
            return 1;
        }

        var strategyName = args[1].ToLowerInvariant();
        var strategy = Registry.Find(strategyName);
        if (strategy is null)
        {
            Console.Error.WriteLine($"未知策略：{strategyName}");
            Console.Error.WriteLine($"可用策略：{string.Join(" | ", Registry.StrategyNames)}");
            return 1;
        }

        var cliOptions = CliOptions.Parse(args[2..]);
        if (string.IsNullOrWhiteSpace(cliOptions.Files))
        {
            Console.Error.WriteLine("缺少 --files <glob>（文件匹配模式）。");
            return 1;
        }

        var templateContext = new ProbeContext
        {
            GateTemplatePath = cliOptions.Gate ?? string.Empty,
            GateThreshold = cliOptions.Threshold > 0d
                ? cliOptions.Threshold
                : MapRecognitionTuning.DefaultGateTemplateThreshold,
            ClientWidth = cliOptions.ClientWidth,
            UseFullFrame = cliOptions.Full,
            ViewportRegion = null,
            ViewportMargin = cliOptions.ViewportMargin,
            EnableStructure = cliOptions.Structure,
            EnableEcc = cliOptions.Ecc,
            ForceBestCandidate = cliOptions.ForceBest,
            TopCount = cliOptions.Top,
            TopCandidates = cliOptions.TopCandidates,
            DownscaleFactor = cliOptions.Downscale,
            SettingsPath = cliOptions.Settings,
            OutputPath = cliOptions.Out is not null ? Path.GetFullPath(cliOptions.Out) : null,
            StructureFillOutputDirectory = cliOptions.MaskDirectory is not null
                ? Path.GetFullPath(cliOptions.MaskDirectory)
                : null,
            StructureFillGuideMap = cliOptions.GuideMap
        };

        await TomlConfigLoader.ApplyToContextAsync(templateContext, cliOptions.Settings);

        var runner = new BatchRunner(Registry);
        var summary = await runner.RunBatchAsync(
            strategy,
            cliOptions.Files,
            cliOptions.Parallel,
            templateContext,
            CancellationToken.None);

        // 输出 summary.json
        if (cliOptions.Out is not null)
        {
            var summaryPath = Path.GetFullPath(cliOptions.Out);
            var dir = Path.GetDirectoryName(summaryPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            var json = System.Text.Json.JsonSerializer.Serialize(
                summary,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });
            await File.WriteAllTextAsync(summaryPath, json);
            Console.Error.WriteLine($"汇总已保存：{summaryPath}");
        }

        return summary.Succeeded == summary.TotalFiles ? 0 : 1;
    }

    private static Task<int> HandleConfigAsync(string[] args)
    {
        Console.Error.WriteLine("config 命令尚未实现（占位符）。");
        Console.Error.WriteLine("可通过 --settings 路径加载运行设置，无需独立 config 命令。");
        return Task.FromResult(0);
    }

    private static int HandleStructureArtifacts(string[] args)
    {
        var options = CliOptions.Parse(args[1..]);
        if (string.IsNullOrWhiteSpace(options.Out))
        {
            Console.Error.WriteLine(
                "用法：idvb-probe.exe structure-artifacts --out <directory> "
                + "[--research <session-directory>] [--map-root <map-directory>]");
            return 1;
        }

        var result = StructureGenerationArtifactRunner.Run(
            options.Out,
            options.ResearchSession,
            options.MapRoot);
        Console.WriteLine($"结构图前后产物已保存：{Path.GetFullPath(options.Out)}");
        return result;
    }

    private static int HandleNotImplemented(string command, string strategy)
    {
        Console.Error.WriteLine($"{strategy} {command} 命令尚未实现。");
        Console.Error.WriteLine($"可用命令：run");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            IDVBuff.MapAlignment.Probe — 模块化 CLI 测试工具

            命令格式：
              idvb-probe.exe <strategy> <command> [options]
              idvb-probe.exe batch <strategy> --files <glob> [options]
              idvb-probe.exe structure-artifacts --out <directory>

            策略：
              dual-gate       双门对齐 + 几何排名 + 可选结构配准
              side-entrance   侧门特征扫描
              gates           仅门模板匹配检测
              floor           仅楼层指示器识别 (1F/2F)

            命令：
              run             完整识别管线（已实现）
              detect          仅检测阶段（待实现）
              rank            仅排名阶段（待实现）

            batch 示例：
              idvb-probe.exe batch dual-gate --files "samples/**/*.png" --structure --out summary.json

            dual-gate 示例：
              idvb-probe.exe dual-gate run --image screenshot.png --structure
              idvb-probe.exe dual-gate run --image screenshot.png --viewport 0.1,0.05,0.8,0.85 --top 3

            side-entrance 示例：
              idvb-probe.exe side-entrance run --image screenshot.png --top 10

            gates 示例：
              idvb-probe.exe gates run --image screenshot.png --threshold 0.72

            floor 示例：
              idvb-probe.exe floor run --image screenshot.png

            通用选项：
              --out <path>           输出 JSON 文件路径
              --settings <path>      settings.json 路径（可选，用于视口自动裁剪）
              --gate <path>          Gate.png 模板路径（默认 Assets/Gate.png）
              --client-width <n>     客户端宽度（默认 2560）
              --full                 使用全帧（跳过视口裁剪）
              --viewport x,y,w,h     归一化视口区域
              --viewport-margin <n>  视口边缘膨胀（默认 0.20）

            结构图前后产物：
              --out <directory>     输出目录
              --research <path>     AlignmentResearch 会话目录（可选）
              --map-root <path>     本机地图目录（可选）

            dual-gate 专属选项：
              --structure            启用结构配准复核
              --ecc                  启用 ECC 精修（需 --structure）
              --force-best           强制输出最佳候选
              --top <n>              排名 top N（默认 1）
              --top-candidates <n>   结构配准 top 候选数（默认 6）
              --downscale <n>        结构配准降采样（默认 0.5）
              --threshold <n>        门检测阈值
            """);
    }
}
