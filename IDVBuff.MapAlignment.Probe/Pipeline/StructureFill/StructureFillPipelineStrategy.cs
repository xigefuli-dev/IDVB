using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using IDVBuff.Features.Maps;
using IDVBuff.MapAlignment.Probe.Output;
using OpenCvSharp;

namespace IDVBuff.MapAlignment.Probe.Pipeline.StructureFill;

/// <summary>
/// One-image structure filling. The output PNG is a white-on-black filled
/// silhouette; no reference image or feature matching is involved.
/// </summary>
public sealed class StructureFillPipelineStrategy : IPipelineStrategy
{
    public string StrategyName => "structure-fill";

    public Task<ProbeResult> RunAsync(ProbeContext context, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        using var source = Cv2.ImRead(context.ImagePath, ImreadModes.Unchanged);
        if (source.Empty())
        {
            timer.Stop();
            return Task.FromResult(new ProbeResult
            {
                Strategy = StrategyName,
                Command = "run",
                Succeeded = false,
                FailureReason = "无法读取结构填充输入图像。",
                Phases = new PhaseTimings { TotalWallMs = timer.Elapsed.TotalMilliseconds }
            });
        }

        ct.ThrowIfCancellationRequested();
        var input = source;
        Mat? cropped = null;
        try
        {
            if (!context.UseFullFrame && context.ViewportRegion is not null)
            {
                var region = context.ViewportRegion;
                var left = Math.Clamp(
                    (int)Math.Floor(region.X * source.Width),
                    0,
                    source.Width - 1);
                var top = Math.Clamp(
                    (int)Math.Floor(region.Y * source.Height),
                    0,
                    source.Height - 1);
                var right = Math.Clamp(
                    (int)Math.Ceiling((region.X + region.Width) * source.Width),
                    left + 1,
                    source.Width);
                var bottom = Math.Clamp(
                    (int)Math.Ceiling((region.Y + region.Height) * source.Height),
                    top + 1,
                    source.Height);
                cropped = new Mat(source, new Rect(left, top, right - left, bottom - top));
                input = cropped;
            }

            var analysisTimer = Stopwatch.StartNew();
            using var result = new MapStructureFiller().Analyze(
                input,
                new StructureFillOptions
                {
                    ApplyGuideMapTone = context.StructureFillGuideMap
                });
            analysisTimer.Stop();
            var outputPath = ResolveOutputPath(context);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (outputDirectory is not null)
                    Directory.CreateDirectory(outputDirectory);
                if (!Cv2.ImWrite(outputPath, result.Mask))
                    throw new IOException($"无法写入结构填充图：{outputPath}");
            }

            timer.Stop();
            var probeResult = new ProbeResult
            {
                Strategy = StrategyName,
                Command = "run",
                Succeeded = true,
                Confidence = result.HasStructure ? 1d : 0d,
                Phases = new PhaseTimings
                {
                    StructureWallMs = analysisTimer.Elapsed.TotalMilliseconds,
                    TotalWallMs = timer.Elapsed.TotalMilliseconds
                },
                ImageWidth = input.Width,
                ImageHeight = input.Height,
                Extra = new
                {
                    OutputPath = outputPath,
                    HasStructure = result.HasStructure,
                    result.ForegroundPixels,
                    result.ComponentCount,
                    result.OtsuThreshold,
                    result.EffectiveThreshold,
                    Bounds = new
                    {
                        result.Bounds.X,
                        result.Bounds.Y,
                        result.Bounds.Width,
                        result.Bounds.Height
                    },
                    AlgorithmVersion = MapStructureFiller.AlgorithmVersion,
                    GuideMapToneApplied = context.StructureFillGuideMap,
                    AnalysisMs = analysisTimer.Elapsed.TotalMilliseconds
                }
            };

            JsonOutputWriter.WriteLine(probeResult);
            if (context.OutputPath is not null
                && !string.Equals(
                    context.OutputPath,
                    outputPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                JsonOutputWriter.WriteAsync(probeResult, context.OutputPath)
                    .GetAwaiter()
                    .GetResult();
            }
            return Task.FromResult(probeResult);
        }
        finally
        {
            cropped?.Dispose();
        }
    }

    private static string? ResolveOutputPath(ProbeContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.StructureFillOutputPath))
            return Path.GetFullPath(context.StructureFillOutputPath);
        if (string.IsNullOrWhiteSpace(context.StructureFillOutputDirectory))
            return null;

        var directory = Path.GetFullPath(context.StructureFillOutputDirectory);
        var inputName = Path.GetFileNameWithoutExtension(context.ImagePath);
        var sourceKey = Path.GetFullPath(context.ImagePath);
        var suffix = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey)))
            .Substring(0, 12)
            .ToLowerInvariant();
        return Path.Combine(directory, $"{inputName}-{suffix}.structure.png");
    }
}
