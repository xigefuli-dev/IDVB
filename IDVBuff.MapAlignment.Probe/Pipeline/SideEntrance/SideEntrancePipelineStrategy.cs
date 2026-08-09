using System.Diagnostics;
using IDVBuff.Features.Maps;
using IDVBuff.MapAlignment.Probe.Output;
using OpenCvSharp;

namespace IDVBuff.MapAlignment.Probe.Pipeline.SideEntrance;

/// <summary>
/// 侧门扫描管线：对输入图片运行侧门特征匹配，返回 top-N 候选。
/// 对应原 Program.cs 中的 side-scan 命令。
/// </summary>
public sealed class SideEntrancePipelineStrategy : IPipelineStrategy
{
    public string StrategyName => "side-entrance";

    public async Task<ProbeResult> RunAsync(ProbeContext context, CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();

        using var image = Cv2.ImRead(context.ImagePath, ImreadModes.Color);
        if (image.Empty())
            return Fail("无法读取侧门扫描图片。", totalTimer);
        double loadMs = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        var repository = new MapRepository();
        var maps = await repository.GetMapsAsync();
        double catalogMs = phase.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();

        var templates = new List<(MapRecord Map, string FloorKey, Mat FeatureTemplate)>();
        try
        {
            foreach (var map in maps)
            {
                if (context.SideScanMapId.HasValue && map.Id != context.SideScanMapId.Value)
                    continue;

                foreach (var floor in MapFloorRules.GetOrderedFloors(map))
                {
                    var profile = MapFloorRules.GetFloorProfile(map, floor.Key);
                    if (profile is null
                        || string.IsNullOrWhiteSpace(profile.SideEntranceFeatureFileName))
                        continue;

                    var path = repository.GetSideEntranceFeaturePath(map, floor.Key);
                    if (!File.Exists(path))
                        continue;

                    var template = Cv2.ImRead(path, ImreadModes.Grayscale);
                    if (template.Empty())
                    {
                        template.Dispose();
                        continue;
                    }
                    templates.Add((Map: map, FloorKey: floor.Key, FeatureTemplate: template));
                }
            }

            phase.Restart();
            var pipeline = new SideEntranceScanPipeline();
            var scanCandidates = pipeline.RunScan(image, templates, context.SideScanTop);
            double scanMs = phase.Elapsed.TotalMilliseconds;

            totalTimer.Stop();
            var candidates = scanCandidates.Select(c => new CandidateInfo
            {
                MapId = c.Map.Id.ToString(),
                MapDisplayName = c.Map.DisplayName,
                FloorKey = c.FloorKey,
                Score = c.MatchScore,
                EstimatedScaleX = c.MatchScale,
                EstimatedScaleY = c.MatchScale
            }).ToList();

            var result = new ProbeResult
            {
                Strategy = StrategyName,
                Command = "run",
                Succeeded = candidates.Count > 0,
                Confidence = candidates.Count > 0 ? candidates.Max(c => c.Score) : 0d,
                MapId = candidates.FirstOrDefault()?.MapId,
                MapDisplayName = candidates.FirstOrDefault()?.MapDisplayName,
                Candidates = candidates,
                Phases = new PhaseTimings
                {
                    LoadMs = loadMs,
                    CatalogLoadMs = catalogMs,
                    GeometryRankMs = scanMs,
                    TotalWallMs = totalTimer.Elapsed.TotalMilliseconds
                },
                ImageWidth = image.Width,
                ImageHeight = image.Height,
                Extra = new { TemplateCount = templates.Count }
            };

            JsonOutputWriter.WriteLine(result);
            if (context.OutputPath is not null)
                await JsonOutputWriter.WriteAsync(result, context.OutputPath);

            return result;
        }
        finally
        {
            foreach (var (_, _, template) in templates)
                template.Dispose();
        }
    }

    private static ProbeResult Fail(string reason, Stopwatch timer)
    {
        timer.Stop();
        return new ProbeResult
        {
            Strategy = "side-entrance",
            Command = "run",
            Succeeded = false,
            FailureReason = reason,
            Phases = new PhaseTimings { TotalWallMs = timer.Elapsed.TotalMilliseconds }
        };
    }
}
