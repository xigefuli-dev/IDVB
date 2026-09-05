using System.Text.Json;
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
        if (string.IsNullOrEmpty(root)) return;
        var rows = new List<object>();
        foreach (var path in Directory.GetFiles(Path.Combine(root, "samples"), "sample.json", SearchOption.AllDirectories).Order())
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var sample = json.RootElement;
            var dir = Path.GetDirectoryName(path)!;
            using var reference = Cv2.ImRead(Path.GetFullPath(Path.Combine(dir, sample.GetProperty("referencePath").GetString()!)), ImreadModes.Grayscale);
            using var live = Cv2.ImRead(Path.Combine(dir, "live.png"));
            var bounds = sample.GetProperty("bounds").Deserialize<MapScreenRect>();
            using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, sample.GetProperty("key").Deserialize<Vpsg3IndexCacheKey>());
            using var observation = Vpsg3FastLiveExtractor.Extract(live, bounds);
            var result = Vpsg3FastBootstrapSolver.TrySolve(observation, floor);
            var id = sample.GetProperty("sampleId").GetString();
            Cv2.ImWrite(Path.Combine(dir, "replay-observed.png"), observation.ObservedEdges);
            rows.Add(new { id, referenceWidth = reference.Width, referenceHeight = reference.Height, floor.ScalePrior, result });
            output.WriteLine($"{id} ref={reference.Width}x{reference.Height} pitch={floor.ScalePrior.ReferencePitch} edges={observation.EdgePixelCount} accepted={result.IsAccepted} scale={result.Scale:F5} x={result.OffsetX:F2} y={result.OffsetY:F2} ms={result.Timing.TotalMs:F2} {result.FallbackReason}");
        }
        Assert.NotEmpty(rows);
        File.WriteAllText(Environment.GetEnvironmentVariable("VPSG3_REPLAY_OUTPUT") ?? Path.Combine(root, "replay.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}
