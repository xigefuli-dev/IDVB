using System.Text.Json;
using System.Security.Cryptography;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3RealFrameReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void ReplayCapturedFrames()
    {
        var root = Environment.GetEnvironmentVariable("VPSG3_REPLAY_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(root), "Set VPSG3_REPLAY_ROOT to a nonempty frozen certification dataset.");
        var samples = Path.Combine(root!, "samples");
        Assert.True(Directory.Exists(samples), $"Replay samples directory missing: {samples}");
        var paths = Directory.GetFiles(samples, "sample.json", SearchOption.AllDirectories).Order().ToArray();
        Assert.NotEmpty(paths);
        var precision = Environment.GetEnvironmentVariable("VPSG3_PRECISION_SHADOW") == "1";
        var rows = new List<object>();
        var certifiedWithGroundTruth = 0;
        foreach (var path in paths)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var sample = json.RootElement;
            var dir = Path.GetDirectoryName(path)!;
            var referencePath = Path.GetFullPath(Path.Combine(dir, sample.GetProperty("referencePath").GetString()!));
            var livePath = Path.Combine(dir, "live.png");
            Assert.Equal(sample.GetProperty("referenceSha256").GetString(),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(referencePath))).ToLowerInvariant());
            Assert.Equal(sample.GetProperty("liveSha256").GetString(),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(livePath))).ToLowerInvariant());
            using var reference = Cv2.ImRead(referencePath, ImreadModes.Grayscale);
            using var live = Cv2.ImRead(livePath);
            Assert.False(reference.Empty(), $"Invalid reference: {referencePath}");
            Assert.False(live.Empty(), $"Invalid frame: {livePath}");
            var bounds = sample.GetProperty("bounds").Deserialize<MapScreenRect>();
            using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, sample.GetProperty("key").Deserialize<Vpsg3IndexCacheKey>(), preparePrecision: precision);
            using var observation = Vpsg3FastLiveExtractor.Extract(live, bounds);
            var result = Vpsg3FastBootstrapSolver.TrySolve(observation, floor);
            var id = sample.GetProperty("sampleId").GetString();
            var shadow = precision ? Vpsg3PrecisionShadow.Evaluate(observation, floor, result) : null;
            rows.Add(new { id, referenceWidth = reference.Width, referenceHeight = reference.Height,
                floor.ScalePrior, result, shadow, floor.MemoryBytes, tuning = Vpsg3TuningConfig.Default,
                knownScaleSeed = (double?)null, replayRoute = "current-cold-solver",
                groundTruth = sample.GetProperty("groundTruth").Clone() });

            if (sample.TryGetProperty("groundTruth", out var gt) && gt.ValueKind == JsonValueKind.Object)
            {
                certifiedWithGroundTruth++;
                if (gt.TryGetProperty("expectedAccepted", out var ea))
                {
                    Assert.Equal(ea.GetBoolean(), result.IsAccepted);
                }
                if (result.IsAccepted && gt.TryGetProperty("trueScale", out var ts))
                {
                    var maxScaleErr = gt.TryGetProperty("scaleTolerance", out var st) ? st.GetDouble() : 0.035d;
                    Assert.True(Math.Abs(result.Scale - ts.GetDouble()) <= maxScaleErr, $"{id}: Scale error too large (actual={result.Scale:F5}, expected={ts.GetDouble():F5})");
                }
                if (result.IsAccepted && gt.TryGetProperty("trueOffsetX", out var tox) && gt.TryGetProperty("trueOffsetY", out var toy))
                {
                    var maxTransErr = gt.TryGetProperty("translationTolerance", out var tt) ? tt.GetDouble() : 4.0d;
                    var err = Math.Sqrt(Math.Pow(result.OffsetX - tox.GetDouble(), 2) + Math.Pow(result.OffsetY - toy.GetDouble(), 2));
                    Assert.True(err <= maxTransErr, $"{id}: Translation error too large (actual=({result.OffsetX:F2},{result.OffsetY:F2}), expected=({tox.GetDouble():F2},{toy.GetDouble():F2}), err={err:F2}px)");
                }
            }

            output.WriteLine($"{id} ref={reference.Width}x{reference.Height} pitch={floor.ScalePrior.ReferencePitch} edges={observation.EdgePixelCount} accepted={result.IsAccepted} scale={result.Scale:F5} x={result.OffsetX:F2} y={result.OffsetY:F2} ms={result.Timing.TotalMs:F2} {result.FallbackReason}");
        }
        Assert.NotEmpty(rows);
        Assert.True(certifiedWithGroundTruth >= 50, $"Expected at least 50 certified samples with independent ground truth, but only {certifiedWithGroundTruth} were verified.");
        var outputPath = Environment.GetEnvironmentVariable("VPSG3_REPLAY_OUTPUT") ?? Path.Combine(root, "replay.json");
        try
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Ignore write errors in read-only replay root
        }
    }
}
