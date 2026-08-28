using System.Text.Json;
using IDVBuff.RealCLI.Cli;
using IDVBuff.RealCLI.Output;
using IDVBuff.RealCLI.Stubs;
using Microsoft.UI.Dispatching;

namespace IDVBuff.RealCLI;

internal static class MapOpenReplayCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        DispatcherQueue dispatcher)
    {
        string? manifestPath = null;
        string? outputPath = null;
        string? settingsOverride = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--manifest":
                case "-m":
                    manifestPath = args[++i]; break;
                case "--out":
                case "-o":
                    outputPath = args[++i]; break;
                case "--settings":
                case "-s":
                    settingsOverride = args[++i]; break;
            }
        }

        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            Console.Error.WriteLine("错误：缺少 --manifest <path> 参数。");
            return 1;
        }
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"错误：manifest 不存在 —— {manifestPath}");
            return 1;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<MapOpenReplayManifest>(
                await File.ReadAllTextAsync(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null || manifest.Cases.Count == 0)
            {
                Console.Error.WriteLine("错误：manifest 必须包含至少一个 cases 项。");
                return 1;
            }
            if (manifest.SchemaVersion is < 1 or > 1)
            {
                Console.Error.WriteLine(
                    $"错误：不支持的 mapopen-replay schemaVersion={manifest.SchemaVersion}。");
                return 1;
            }

            var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
                ?? Environment.CurrentDirectory;
            var cases = new List<MapOpenReplayCaseResult>(manifest.Cases.Count);
            for (var i = 0; i < manifest.Cases.Count; i++)
            {
                var replayCase = manifest.Cases[i];
                var scanImage = ResolvePath(root, replayCase.ScanImage);
                var reopenImage = ResolvePath(
                    root,
                    string.IsNullOrWhiteSpace(replayCase.ReopenImage)
                        ? replayCase.ScanImage
                        : replayCase.ReopenImage!);
                if (!File.Exists(scanImage) || !File.Exists(reopenImage))
                {
                    var missingFloorPosition = replayCase.FloorPosition
                        ?? manifest.FloorPosition;
                    var missing = !File.Exists(scanImage) ? scanImage : reopenImage;
                    Console.Error.WriteLine(
                        $"[{i + 1}/{manifest.Cases.Count}] 图片不存在，跳过：{missing}");
                    cases.Add(new MapOpenReplayCaseResult
                    {
                        Name = replayCase.Name,
                        ScanImage = scanImage,
                        ReopenImage = reopenImage,
                        FloorPosition = missingFloorPosition,
                        Expected = replayCase.Expected,
                        Result = new RealCliSessionResult
                        {
                            ImagePath = scanImage,
                            Succeeded = false,
                            AlignmentSucceeded = false,
                            StatusMessage = "回放图片不存在。",
                            FailureReason = missing
                        }
                    });
                    continue;
                }

                Console.Error.WriteLine(
                    $"[{i + 1}/{manifest.Cases.Count}] {replayCase.Name ?? Path.GetFileName(scanImage)}");
                var settingsRoot = settingsOverride
                    ?? ResolveOptionalPath(root, manifest.SettingsRoot);
                var floorPosition = replayCase.FloorPosition
                    ?? manifest.FloorPosition;
                var orchestrator = OrchestratorFactory.BuildOrchestrator(
                    dispatcher,
                    scanImage,
                    settingsRoot,
                    out var overlay,
                    out var capture);
                var result = await MapOpenCommand.RunMapOpenScenarioAsync(
                    orchestrator,
                    overlay,
                    scanImage,
                    replayCase.Candidate ?? manifest.Candidate,
                    reopenImage,
                    capture,
                    floorPosition);
                cases.Add(new MapOpenReplayCaseResult
                {
                    Name = replayCase.Name,
                    ScanImage = scanImage,
                    ReopenImage = reopenImage,
                    FloorPosition = floorPosition,
                    Expected = replayCase.Expected,
                    Result = result,
                    ExpectedMatched = MatchExpected(result, replayCase.Expected)
                });
            }

            var output = new MapOpenReplayOutput
            {
                SchemaVersion = 1,
                ManifestPath = Path.GetFullPath(manifestPath),
                Total = cases.Count,
                Succeeded = cases.Count(item => item.Result.AlignmentSucceeded
                    && item.ExpectedMatched != false),
                Cases = cases
            };
            if (outputPath is not null)
                await RealCliOutputWriter.WriteObjectAsync(output, outputPath);
            else
                Console.WriteLine(JsonSerializer.Serialize(
                    output,
                    new JsonSerializerOptions { WriteIndented = true }));
            return output.Succeeded == output.Total ? 0 : 1;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"manifest JSON 无效：{ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal 异常：{ex}");
            return 2;
        }
    }

    private static string ResolvePath(string root, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(root, path));

    private static string? ResolveOptionalPath(string root, string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : ResolvePath(root, path);

    private static bool? MatchExpected(
        RealCliSessionResult result,
        MapOpenReplayExpectation? expected)
    {
        if (expected is null)
            return null;
        if (!result.AlignmentSucceeded || result.Recognition is null)
            return false;
        if (!string.IsNullOrWhiteSpace(expected.MapId)
            && !string.Equals(expected.MapId, result.Recognition.MapId,
                StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(expected.Floor)
            && !string.Equals(expected.Floor, result.Recognition.Floor,
                StringComparison.OrdinalIgnoreCase))
            return false;
        var transform = result.Recognition.Transform;
        if (transform is null)
            return false;
        return (!expected.Scale.HasValue
                || Math.Abs(transform.ScaleX - expected.Scale.Value)
                    <= Math.Max(0.0001d, expected.ScaleTolerance))
            && (!expected.OffsetX.HasValue
                || Math.Abs(transform.OffsetX - expected.OffsetX.Value)
                    <= Math.Max(0.1d, expected.OffsetTolerancePixels))
            && (!expected.OffsetY.HasValue
                || Math.Abs(transform.OffsetY - expected.OffsetY.Value)
                    <= Math.Max(0.1d, expected.OffsetTolerancePixels));
    }
}

internal sealed class MapOpenReplayManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string? SettingsRoot { get; init; }
    public int? Candidate { get; init; }
    public int? FloorPosition { get; init; }
    public List<MapOpenReplayCase> Cases { get; init; } = [];
}

internal sealed class MapOpenReplayCase
{
    public string? Name { get; init; }
    public string ScanImage { get; init; } = string.Empty;
    public string? ReopenImage { get; init; }
    public int? Candidate { get; init; }
    public int? FloorPosition { get; init; }
    public MapOpenReplayExpectation? Expected { get; init; }
}

internal sealed class MapOpenReplayExpectation
{
    public string? MapId { get; init; }
    public string? Floor { get; init; }
    public double? Scale { get; init; }
    public double? OffsetX { get; init; }
    public double? OffsetY { get; init; }
    public double ScaleTolerance { get; init; } = 0.015d;
    public double OffsetTolerancePixels { get; init; } = 8d;
}

internal sealed class MapOpenReplayCaseResult
{
    public string? Name { get; init; }
    public string ScanImage { get; init; } = string.Empty;
    public string ReopenImage { get; init; } = string.Empty;
    public int? FloorPosition { get; init; }
    public MapOpenReplayExpectation? Expected { get; init; }
    public bool? ExpectedMatched { get; init; }
    public RealCliSessionResult Result { get; init; } = new();
}

internal sealed class MapOpenReplayOutput
{
    public int SchemaVersion { get; init; }
    public string ManifestPath { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public List<MapOpenReplayCaseResult> Cases { get; init; } = [];
}
