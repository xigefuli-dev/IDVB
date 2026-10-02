using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

// Read-only reference audit using the same optional capture/catalog inputs as runtime replay.
[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class ScanReferenceReplayTests
{
    [ObservationRuntimeReplayTests.ReplayFact]
    public async Task CompareSavedEvidenceAndRegeneratedReference()
    {
        var root = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPLAY")!;
        var mapsRoot = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_MAPS")!;
        var output = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPORT")!;
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scan.json")));
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapsRoot, "maps.json")));
        var maps = catalog.RootElement.GetProperty("Maps").Deserialize<MapRecord[]>()!;
        var candidate = saved.RootElement.GetProperty("candidates")[0];
        var map = maps.Single(m => m.SequenceNumber == candidate.GetProperty("SequenceNumber").GetInt32()
            && m.Class == candidate.GetProperty("Class").GetString());
        var floor = candidate.GetProperty("FloorKey").GetString()!;
        var definition = map.Floors.Single(f => f.Key == floor);
        var directory = Path.Combine(mapsRoot, map.Id.ToString("N"));
        var sourcePath = Path.Combine(directory, definition.RecognitionFileName);
        using var source = Cv2.ImDecode(File.ReadAllBytes(sourcePath), ImreadModes.Color);
        using var line = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(directory,
            definition.PrebuiltStructureLine!.FileName)), ImreadModes.Grayscale);
        var engine = new IdvaStructureLineEngine();
        var algorithm = await engine.LoadAsync(Path.Combine(directory, definition.PrebuiltStructureLine.AlgorithmFileName));
        using var regenerated = engine.Execute(algorithm, source);
        using var difference = new Mat();
        Cv2.Absdiff(line, regenerated, difference);
        File.WriteAllBytes(Path.ChangeExtension(output, ".regenerated.png"), regenerated.ImEncode(".png"));

        using var observed = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(root, "observed-edges.png")), ImreadModes.Grayscale);
        using var points = observed.FindNonZero();
        var scale = candidate.GetProperty("MatchScale").GetDouble();
        var location = candidate.GetProperty("MatchLocation").Deserialize<MapScreenRect>();
        var index = ScanStructureIndex.Get(line);
        var distances = Enumerable.Range(0, (int)points.Total()).Select(i =>
        {
            var p = points.At<Point>(i);
            return index.Distance((p.X - location.X) / scale, (p.Y - location.Y) / scale, scale);
        }).ToArray();
        var stages = new List<object>();
        for (var count = 1; count < algorithm.Pipeline.Count; count++)
        {
            using var partial = engine.Execute(algorithm with
                { Pipeline = algorithm.Pipeline.Take(count).Append(algorithm.Pipeline[^1]).ToArray() }, source);
            var path = Path.ChangeExtension(output, $".stage-{count}.png");
            File.WriteAllBytes(path, partial.ImEncode(".png"));
            stages.Add(new { stage = algorithm.Pipeline[count - 1].GetProperty("stage").GetString(), path });
        }
        // Parameter probes are diagnostic copies only; never overwrite a package.
        var probes = new List<object>();
        var viewport = saved.RootElement.GetProperty("ViewportBounds").Deserialize<MapScreenRect>();
        var gate = candidate.GetProperty("hypotheses")[0].GetProperty("AssociatedGate").Deserialize<GateDetection>()!;
        using var capture = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(root, "viewport.png")), ImreadModes.Color);
        using var frame = new ScanFrameEvidence(capture, viewport, [gate], ScanExecutionPolicy.For(ScanPerformanceMode.Balanced));
        using var context = ScanExecutionContext.Enter(ScanPerformanceMode.Quality);
        context.CompleteAutomaticPhase();
        foreach (var threshold in new[] { 12, 16, 20 })
        {
            var parameters = JsonNode.Parse(algorithm.Parameters.GetRawText())!;
            parameters["lab_distance_threshold"] = threshold;
            using var json = JsonDocument.Parse(parameters.ToJsonString());
            using var variant = engine.Execute(algorithm with { Parameters = json.RootElement }, source);
            var anchor = MapScanFloorRules.GetScanFeatureAnchor(map, floor)!.Bounds!;
            Cv2.Rectangle(variant, new Rect((int)Math.Floor(anchor.X * variant.Width), (int)Math.Floor(anchor.Y * variant.Height),
                (int)Math.Ceiling(anchor.Width * variant.Width), (int)Math.Ceiling(anchor.Height * variant.Height)), Scalar.Black, -1);
            var variantIndex = ScanStructureIndex.Get(variant).WithScanAnchor(map, floor);
            var transform = new MapOverlayTransform { ScaleX = scale, ScaleY = scale,
                OffsetX = viewport.X + location.X, OffsetY = viewport.Y + location.Y };
            var evidence = ScanIdentityVerifier.Verify(frame, variantIndex, transform, viewport, context);
            var refined = SideEntranceScanPipeline.RefineIdentityPose(new SideEntranceScanCandidate
            {
                Map = map, FloorKey = floor, MatchScale = scale, StructureIndex = variantIndex,
                MatchLocation = location, AssociatedGate = gate
            }, frame, viewport, context);
            File.WriteAllBytes(Path.ChangeExtension(output, $".probe-{threshold}.png"), variant.ImEncode(".png"));
            probes.Add(new { threshold, evidence, refinedEvidence = refined?.IdentityEvidence });
        }
        var report = new
        {
            map.SequenceNumber, map.Id, floor, sourcePath,
            sourceHashMatchesMetadata = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)))
                .Equals(definition.PrebuiltStructureLine.SourceSha256, StringComparison.OrdinalIgnoreCase),
            regenerationPixelDifferences = Cv2.CountNonZero(difference),
            saved = candidate.GetProperty("IdentityEvidence"),
            localReferenceAtSavedPose = new { tested = distances.Length, mean = distances.Average(),
                support = distances.Count(d => d <= ScanIdentityVerifier.SupportTolerancePixels) / (double)distances.Length },
            stages, probes
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(source.Size(), regenerated.Size());
    }
}
