using System.Diagnostics;
using IDVBuff.Features.Maps;
using IDVBuff.MapAlignment.Probe.Output;
using OpenCvSharp;

namespace IDVBuff.MapAlignment.Probe.Pipeline.Floor;

/// <summary>
/// 仅楼层识别管线：对输入图片运行 1F/2F 楼层指示器识别。
/// 对应原 Program.cs 中的 floor-image 命令。
/// </summary>
public sealed class FloorRecognitionPipelineStrategy : IPipelineStrategy
{
    public string StrategyName => "floor";

    public Task<ProbeResult> RunAsync(ProbeContext context, CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();

        using var source = Cv2.ImRead(context.ImagePath, ImreadModes.Unchanged);
        if (source.Empty())
        {
            totalTimer.Stop();
            return Task.FromResult(new ProbeResult
            {
                Strategy = StrategyName,
                Command = "run",
                Succeeded = false,
                FailureReason = "无法读取楼层识别图片。",
                Phases = new PhaseTimings { TotalWallMs = totalTimer.Elapsed.TotalMilliseconds }
            });
        }
        double loadMs = phase.Elapsed.TotalMilliseconds;

        Mat? cropped = null;
        var input = source;
        try
        {
            if (!context.UseFullFrame && context.ViewportRegion is not null)
            {
                var region = context.ViewportRegion;
                var left = Math.Clamp((int)Math.Floor(region.X * source.Width), 0, source.Width - 1);
                var top = Math.Clamp((int)Math.Floor(region.Y * source.Height), 0, source.Height - 1);
                var right = Math.Clamp(
                    (int)Math.Ceiling((region.X + region.Width) * source.Width),
                    left + 1, source.Width);
                var bottom = Math.Clamp(
                    (int)Math.Ceiling((region.Y + region.Height) * source.Height),
                    top + 1, source.Height);
                cropped = new Mat(source, new Rect(left, top, right - left, bottom - top));
                input = cropped;
            }

            var firstPath = string.IsNullOrWhiteSpace(context.FirstFloorTemplatePath)
                ? Path.Combine(AppContext.BaseDirectory, "Assets", "1F.png")
                : context.FirstFloorTemplatePath;
            var secondPath = string.IsNullOrWhiteSpace(context.SecondFloorTemplatePath)
                ? Path.Combine(AppContext.BaseDirectory, "Assets", "2F.png")
                : context.SecondFloorTemplatePath;

            var recognizer = new FloorIndicatorRecognizer(firstPath, secondPath);
            phase.Restart();
            var result = recognizer.Recognize(input);
            double analysisMs = phase.Elapsed.TotalMilliseconds;

            totalTimer.Stop();
            var probeResult = new ProbeResult
            {
                Strategy = StrategyName,
                Command = "run",
                Succeeded = result.Succeeded,
                Confidence = result.Confidence,
                MapId = result.Floor?.ToString(),
                FailureReason = result.Succeeded ? null : result.FailureReason,
                Phases = new PhaseTimings
                {
                    LoadMs = loadMs,
                    TotalWallMs = totalTimer.Elapsed.TotalMilliseconds
                },
                ImageWidth = source.Width,
                ImageHeight = source.Height,
                Extra = new
                {
                    Floor = result.Floor?.ToString(),
                    result.Confidence,
                    result.LocalizationConfidence,
                    result.LocalizedRegion,
                    result.Contrast,
                    AnalysisMs = analysisMs
                }
            };

            JsonOutputWriter.WriteLine(probeResult);
            if (context.OutputPath is not null)
                JsonOutputWriter.WriteAsync(probeResult, context.OutputPath).GetAwaiter().GetResult();

            return Task.FromResult(probeResult);
        }
        finally
        {
            cropped?.Dispose();
        }
    }
}
