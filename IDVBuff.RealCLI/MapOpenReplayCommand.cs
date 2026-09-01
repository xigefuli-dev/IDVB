using System.Text.Json;
using IDVBuff.Features.Maps;
using IDVBuff.RealCLI.Cli;
using IDVBuff.RealCLI.Output;
using IDVBuff.RealCLI.Stubs;
using Microsoft.UI.Dispatching;

namespace IDVBuff.RealCLI;

internal static class MapOpenReplayCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        DispatcherQueue dispatcher,
        bool readOnlyModelReplay = false)
    {
        string? manifestPath = null;
        string? outputPath = null;
        string? settingsOverride = null;
        string? repositoryOverride = null;
        string? decisionModeOverride = null;
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
                case "--repository":
                    repositoryOverride = args[++i]; break;
                case "--mode":
                    decisionModeOverride = args[++i]; break;
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
            if (manifest.SchemaVersion is < 1 or > 2)
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
                    out var capture,
                    repositoryOverride
                        ?? ResolveOptionalPath(root, manifest.ModelRepository));
                var candidateMapIdText = replayCase.CandidateMapId
                    ?? manifest.CandidateMapId;
                Guid? candidateMapId = string.IsNullOrWhiteSpace(candidateMapIdText)
                    ? null
                    : Guid.Parse(candidateMapIdText);
                var decisionMode = Enum.TryParse<MapCandidateDecisionMode>(
                    decisionModeOverride
                        ?? replayCase.DecisionMode
                        ?? manifest.DecisionMode,
                    ignoreCase: true,
                    out var parsedMode)
                        ? parsedMode
                        : MapCandidateDecisionMode.Traditional;
                var result = await MapOpenCommand.RunMapOpenScenarioAsync(
                    orchestrator,
                    overlay,
                    scanImage,
                    replayCase.Candidate ?? manifest.Candidate,
                    reopenImage,
                    capture,
                    floorPosition,
                    candidateMapId,
                    decisionMode,
                    continuousLearning: !readOnlyModelReplay
                        && candidateMapId.HasValue,
                    replayCase.ForceCandidateSelection
                        ?? manifest.ForceCandidateSelection,
                    replayCase.MapClass);
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
        if (!string.IsNullOrWhiteSpace(expected.Source)
            && !string.Equals(expected.Source,
                result.Recognition.RecognitionSource,
                StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(expected.ModelStatus)
            && !string.Equals(expected.ModelStatus,
                result.ModelStatus?.IsQualified is true
                    ? "Qualified"
                    : result.ModelStatus?.IsAvailable is true
                        ? "Experimental"
                        : "Unavailable",
                StringComparison.OrdinalIgnoreCase))
            return false;
        if (expected.FallbackOccurred.HasValue
            && expected.FallbackOccurred.Value
                != (result.ModelFallbackEvents.Count > 0))
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
    public string? DecisionMode { get; init; }
    public string? ModelRepository { get; init; }
    public bool? ForceCandidateSelection { get; init; }
    public string? CandidateMapId { get; init; }
    public List<MapOpenReplayCase> Cases { get; init; } = [];
}

internal sealed class MapOpenReplayCase
{
    public string? Name { get; init; }
    public string? MapClass { get; init; }
    public string ScanImage { get; init; } = string.Empty;
    public string? ReopenImage { get; init; }
    public int? Candidate { get; init; }
    public int? FloorPosition { get; init; }
    public string? DecisionMode { get; init; }
    public bool? ForceCandidateSelection { get; init; }
    public string? CandidateMapId { get; init; }
    public MapOpenReplayExpectation? Expected { get; init; }
}

internal sealed class MapOpenReplayExpectation
{
    public string? MapId { get; init; }
    public string? Floor { get; init; }
    public double? Scale { get; init; }
    public double? OffsetX { get; init; }
    public double? OffsetY { get; init; }
    public string? Source { get; init; }
    public string? ModelStatus { get; init; }
    public bool? FallbackOccurred { get; init; }
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
