using System.Diagnostics;
using IDVBuff.Features.Maps;
using IDVBuff.MapAlignment.Probe.Output;
using OpenCvSharp;

namespace IDVBuff.MapAlignment.Probe.Pipeline.Gates;

/// <summary>
/// 仅门检测管线：对输入图片运行门模板匹配，返回检测到的门列表及耗时。
/// 对应原 Program.cs 中的 gates-image 命令。
/// </summary>
public sealed class GateDetectionPipelineStrategy : IPipelineStrategy
{
    public string StrategyName => "gates";

    public Task<ProbeResult> RunAsync(ProbeContext context, CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();

        using var image = Cv2.ImRead(context.ImagePath, ImreadModes.Unchanged);
        if (image.Empty())
        {
            totalTimer.Stop();
            return Task.FromResult(new ProbeResult
            {
                Strategy = StrategyName,
                Command = "run",
                Succeeded = false,
                FailureReason = "无法读取门检测图片。",
                Phases = new PhaseTimings { TotalWallMs = totalTimer.Elapsed.TotalMilliseconds }
            });
        }
        double loadMs = phase.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();

        var gatePath = string.IsNullOrWhiteSpace(context.GateTemplatePath)
            ? Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png")
            : context.GateTemplatePath;
        using var detector = new GateTemplateDetector(gatePath);

        phase.Restart();
        using var matchImage = GateTemplateDetector.CreateMatchImage(image);
        double createMatchMs = phase.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();

        phase.Restart();
        var viewport = new MapScreenRect(0d, 0d, image.Width, image.Height);
        var gates = detector.Detect(matchImage, viewport, context.ClientWidth, context.GateThreshold);
        double detectMs = phase.Elapsed.TotalMilliseconds;

        totalTimer.Stop();
        var result = new ProbeResult
        {
            Strategy = StrategyName,
            Command = "run",
            Succeeded = gates.Count >= 2,
            Confidence = gates.Count >= 2 ? 1.0 : gates.Count / 2.0,
            Phases = new PhaseTimings
            {
                LoadMs = loadMs,
                GateCreateMatchImageMs = createMatchMs,
                GateDetectMs = detectMs,
                TotalWallMs = totalTimer.Elapsed.TotalMilliseconds
            },
            ImageWidth = image.Width,
            ImageHeight = image.Height,
            Candidates = gates.Select(g => new CandidateInfo
            {
                Score = g.Score,
                EstimatedScaleX = g.Scale,
                EstimatedScaleY = g.Scale,
                MainGate = new GateInfo
                {
                    Score = g.Score,
                    Scale = g.Scale,
                    Bounds = g.ScreenBounds.IsValid
                        ? new GateBoundsInfo
                        {
                            X = g.ScreenBounds.X,
                            Y = g.ScreenBounds.Y,
                            Width = g.ScreenBounds.Width,
                            Height = g.ScreenBounds.Height
                        }
                        : null
                }
            }).ToList(),
            Extra = new { GateCount = gates.Count }
        };

        JsonOutputWriter.WriteLine(result);
        if (context.OutputPath is not null)
            JsonOutputWriter.WriteAsync(result, context.OutputPath).GetAwaiter().GetResult();

        return Task.FromResult(result);
    }
}
