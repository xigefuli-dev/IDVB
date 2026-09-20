// IDVB Real CLI — 扫描基准烤机报告生成与数据加载辅助（ScanBenchmarkCommand.Reporting）

using System.Diagnostics;
using System.Text.Json;
using IDVBuff.RealCLI.Cli;
using IDVBuff.RealCLI.Output;

namespace IDVBuff.RealCLI;

internal static partial class ScanBenchmarkCommand
{
    private static void PrintProgressRow(int index, int total, string fileName, RealCliBenchmarkCaseResult c)
    {
        var mark = c.VerificationResult switch
        {
            "Matched" => "[OK  ]",
            "Mismatched" => "[FAIL]",
            _ => "[MISS]"
        };

        var expStr = !string.IsNullOrWhiteSpace(c.ExpectedMap) ? c.ExpectedMap : "(未指定)";
        var actStr = !string.IsNullOrWhiteSpace(c.RecognizedMap)
            ? $"{c.RecognizedMap}({c.RecognizedFloor}, {c.Confidence:P0})"
            : "未识别";

        var vpsgStr = c.Vpsg3Attempted
            ? (c.Vpsg3Accepted ? "VPSG3:命中" : $"VPSG3:降级({c.Vpsg3FallbackReason ?? "未达标"})")
            : "VPSG3:未触发";

        var pathSummary = c.CodeExecutionPath.Count > 0
            ? string.Join("->", c.CodeExecutionPath.Take(3).Select(p => p.Split(' ')[0].Trim('[', ']')))
            : "直接";

        Console.Error.WriteLine(
            $"[{index,3}/{total}] {mark} {fileName,-22} | 预期: {expStr,-8} -> 实际: {actStr,-16} | {c.TotalWallMs,6:F1}ms | {vpsgStr} | 路径: {pathSummary}");
    }

    private static void PrintBenchmarkSummary(RealCliBenchmarkReport r)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("================================================================================");
        Console.Error.WriteLine("                          IDVB 扫描诊断与基准烤机报告                          ");
        Console.Error.WriteLine("================================================================================");
        Console.Error.WriteLine($"总样本数: {r.TotalCases} | 通过匹配: {r.MatchedCount} ({r.MatchAccuracy:P1}) | 误识: {r.MismatchedCount} | 未能识别: {r.UnrecognizedCount}");
        Console.Error.WriteLine($"耗时统计: 平均 {r.AverageWallMs:F1}ms | 最低 {r.MinWallMs:F1}ms | 最高 {r.MaxWallMs:F1}ms | P50 {r.P50WallMs:F1}ms | P90 {r.P90WallMs:F1}ms");
        Console.Error.WriteLine($"VPSG 3.0: 尝试 {r.Vpsg3AttemptedCount} 次 | 快速对齐通过 {r.Vpsg3AcceptedCount} 次 (成功率: {r.Vpsg3SuccessRate:P1}) | 平均耗时: {r.Vpsg3AverageWallMs:F1}ms");

        if (r.AveragePhaseTimings.Count > 0)
        {
            Console.Error.WriteLine("\n[环节平均耗时分解]");
            foreach (var (phase, ms) in r.AveragePhaseTimings.OrderByDescending(kv => kv.Value))
            {
                Console.Error.WriteLine($"  - {phase,-30}: {ms,6:F1} ms");
            }
        }

        if (r.FailedCases.Count > 0)
        {
            Console.Error.WriteLine($"\n[异常/未匹配案例清单 ({r.FailedCases.Count} 项)]");
            foreach (var f in r.FailedCases)
            {
                var cause = f.SessionResult.FailureReason
                    ?? f.SessionResult.IdvbStatus?.Message
                    ?? f.Vpsg3FallbackReason
                    ?? "无明确报错";
                Console.Error.WriteLine($"  #{f.Index,2} {Path.GetFileName(f.ImagePath)}: 预期 '{f.ExpectedMap}' vs 实际 '{f.RecognizedMap ?? "(空)"}' ({f.TotalWallMs:F0}ms)");
                Console.Error.WriteLine($"     原因: {cause}");
                if (f.CodeExecutionPath.Count > 0)
                {
                    Console.Error.WriteLine($"     路径: {string.Join(" -> ", f.CodeExecutionPath)}");
                }
            }
        }
        else
        {
            Console.Error.WriteLine("\n[恭喜] 所有测试样本全部 100% 预期匹配通过！");
        }
        Console.Error.WriteLine("================================================================================");
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;
        var realIndex = percentile * (sortedValues.Length - 1);
        var index = (int)realIndex;
        var frac = realIndex - index;
        if (index + 1 < sortedValues.Length)
            return sortedValues[index] * (1 - frac) + sortedValues[index + 1] * frac;
        return sortedValues[index];
    }

    private static List<BenchmarkCaseInput> DiscoverDirectoryCases(
        string dirPath,
        string? pattern,
        bool recursive,
        string? expectedOverride,
        bool inferMap)
    {
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*.*" : pattern;

        var files = Directory.GetFiles(dirPath, searchPattern, opt)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cases = new List<BenchmarkCaseInput>(files.Count);
        foreach (var file in files)
        {
            var expected = expectedOverride;
            if (string.IsNullOrWhiteSpace(expected) && inferMap)
            {
                expected = InferMapFromPath(file);
            }

            cases.Add(new BenchmarkCaseInput
            {
                ImagePath = Path.GetFullPath(file),
                ExpectedMap = expected
            });
        }
        return cases;
    }

    private static string? InferMapFromPath(string fullPath)
    {
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fullPath);
        var parentDirName = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? string.Empty);

        // 已知经典地图关键词库（涵盖主要常用简称与全称）
        var knownMaps = new[]
        {
            "军工厂", "红教堂", "圣心医院", "湖景村", "月亮河公园", "月亮河",
            "里奥的回忆", "白沙街疯人院", "疯人院", "永眠镇", "闪金石窟", "闪金洞窟", "唐人街",
            "不归林", "克雷伯格赛马场", "赛马场", "旧忆军工厂", "灰烬狂欢"
        };

        foreach (var name in knownMaps)
        {
            if (parentDirName.Contains(name, StringComparison.OrdinalIgnoreCase)
                || fileNameWithoutExt.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        // 检查 "地图 7", "地图7", "map_7", "map7"
        var textToSearch = $"{parentDirName}_{fileNameWithoutExt}";
        var match = System.Text.RegularExpressions.Regex.Match(textToSearch, @"(?:地图|map)[_\-\s]*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && match.Groups[1].Value is { } seqStr)
        {
            return $"地图 {seqStr}";
        }

        // 纯数字文件名，如 "1.png" -> "地图 1"
        if (int.TryParse(fileNameWithoutExt, out var num))
        {
            return $"地图 {num}";
        }

        return null;
    }

    private static async Task<List<BenchmarkCaseInput>> LoadManifestCasesAsync(
        string manifestPath,
        string? mapClassOverride,
        string? expectedOverride)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Environment.CurrentDirectory;
        var jsonText = await File.ReadAllTextAsync(manifestPath);

        using var doc = JsonDocument.Parse(jsonText);
        var cases = new List<BenchmarkCaseInput>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var input = ParseCaseElement(elem, root, mapClassOverride, expectedOverride);
                if (input != null) cases.Add(input);
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            string? manifestMapClass = null;
            if (doc.RootElement.TryGetProperty("mapClass", out var mcProp))
                manifestMapClass = mcProp.GetString();

            if (doc.RootElement.TryGetProperty("cases", out var casesProp) && casesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in casesProp.EnumerateArray())
                {
                    var input = ParseCaseElement(elem, root, mapClassOverride ?? manifestMapClass, expectedOverride);
                    if (input != null) cases.Add(input);
                }
            }
        }

        return cases;
    }

    private static BenchmarkCaseInput? ParseCaseElement(
        JsonElement elem,
        string root,
        string? defaultMapClass,
        string? expectedOverride)
    {
        string? imgPath = null;
        if (elem.TryGetProperty("image", out var imgProp) || elem.TryGetProperty("imagePath", out imgProp) || elem.TryGetProperty("scanImage", out imgProp))
            imgPath = imgProp.GetString();

        if (string.IsNullOrWhiteSpace(imgPath)) return null;

        var fullImgPath = Path.IsPathRooted(imgPath) ? imgPath : Path.GetFullPath(Path.Combine(root, imgPath));

        string? expected = expectedOverride;
        if (string.IsNullOrWhiteSpace(expected))
        {
            if (elem.TryGetProperty("expected", out var expProp)
                || elem.TryGetProperty("expectedMap", out expProp)
                || elem.TryGetProperty("map", out expProp))
            {
                expected = expProp.GetString();
            }
        }

        string? floor = null;
        if (elem.TryGetProperty("floor", out var flProp))
            floor = flProp.GetString();

        string? mapClass = defaultMapClass;
        if (elem.TryGetProperty("mapClass", out var mcProp))
            mapClass = mcProp.GetString() ?? defaultMapClass;

        return new BenchmarkCaseInput
        {
            ImagePath = fullImgPath,
            ExpectedMap = expected,
            ExpectedFloor = floor,
            MapClass = mapClass
        };
    }

    private static void PrintBenchmarkUsage()
    {
        Console.WriteLine("""
            IDVB.RealCLI bench — 扫描诊断与基准烤机工具

            用法：
              IDVB.RealCLI.exe bench --dir <directory> [--expected <name>] [--infer-map] [--out <path>]
              IDVB.RealCLI.exe bench --manifest <manifest.json> [--out <path>]

            参数：
              --dir, -d <path>         待测试截图所在目录
              --manifest, -m <path>    测试清单 JSON 文件路径
              --pattern, -p <glob>     截图匹配通配符（默认 *.*，支持 png/jpg/bmp 等）
              --no-recursive           不递归遍历子目录（默认递归）
              --expected, -e <name>    预期地图名称/序号/Guid（覆盖目录中所有样本）
              --infer-map              自动从文件或目录名称中推导预期地图（如 "红教堂_01.png"）
              --consume                后台扫描完成后模拟开图消费（执行完整对齐管线与 VPSG 3.0）
              --mapclass, -c <class>   指定排位/段位地图池（默认从配置读取或 S0）
              --out, -o <path>         输出完整基准测试报告 JSON 文件
              --settings, -s <path>    自定义 settings.json 根目录
              --prewarm-timeout <sec>  VPSG 3.0 空间索引预热超时秒数（默认 20 秒）
              --no-prewarm             跳过 VPSG 3.0 空间索引预热
              --fresh, --isolated      每个样本创建全新独立会话（默认保持常驻会话）
            """);
    }

    private sealed class BenchmarkCaseInput
    {
        public string ImagePath { get; init; } = string.Empty;
        public string? ExpectedMap { get; init; }
        public string? ExpectedFloor { get; init; }
        public string? MapClass { get; init; }
    }
}
