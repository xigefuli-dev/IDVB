using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class LocalB1fAlignmentReplayTests
{
    [B1fReplayFact]
    public void BasementFastAlignmentUsesValidatedFloorScaleAndOriginalGates()
    {
        var root = Environment.GetEnvironmentVariable("IDVB_B1F_APP_ROOT")!;
        var match = Environment.GetEnvironmentVariable("IDVB_B1F_REPLAY")!;
        var report = Environment.GetEnvironmentVariable("IDVB_B1F_REPORT")!;
        var mapsRoot = Path.Combine(root, "Maps");
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapsRoot, "maps.json")));
        var map = catalog.RootElement.GetProperty("Maps").Deserialize<MapRecord[]>()!
            .Single(candidate => candidate.Id == Guid.Parse("fcecdfde-ca03-47ae-91aa-00e88aea62c8"));
        var prebuilt = map.Floors.Single(floor => floor.Key == "b1f").PrebuiltStructureLine!;
        var path = Path.Combine(mapsRoot, map.Id.ToString("N"), "prebuilt-b1f.png");
        using var reference = Cv2.ImRead(path, ImreadModes.Grayscale);
        var key = new Vpsg3IndexCacheKey(map.Id, "b1f", MapFeatureCacheRules.ComputeContentFingerprint(map),
            map.UpdatedAt, Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(prebuilt));
        var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference, key, preparePrecision: true);
        var privateRoot = Path.Combine(Path.GetDirectoryName(report)!, "fast-repository");
        var privateMapRoot = Path.Combine(privateRoot, map.Id.ToString("N"));
        Directory.CreateDirectory(privateMapRoot);
        File.Copy(path, Path.Combine(privateMapRoot, prebuilt.FileName), overwrite: true);
        using var service = new MapCvRecognitionService(new MapRepository(privateRoot));
        Assert.True(service.Vpsg3Registry.TryBeginBuild(key));
        Assert.True(service.Vpsg3Registry.TryPublishFloor(key, floor));
        var viewport = new MapScreenRect(832, 270, 1321, 1055);
        var rows = new List<object>();
        var failures = new List<string>();
        for (var index = 29; index <= 63; index++)
        {
            using var image = Cv2.ImRead(Path.Combine(match, "显示区域", $"显示区域 {index}.png"));
            using var observation = Vpsg3FastLiveExtractor.Extract(image, viewport);
            var result = Vpsg3FastBootstrapSolver.TrySolve(observation, floor, knownScaleSeed: 1.57476884614911);
            var precision = result.IsAccepted
                ? Vpsg3PrecisionRefiner.Refine(observation, floor, result.Scale, result.OffsetX,
                    result.OffsetY, Vpsg3PrecisionBudget.Start(), lockScale: true)
                : null;
            using var frame = new CapturedGameFrame(image.Clone(), new MapScreenRect(0, 0, 2560, 1600), viewport, IntPtr.Zero);
            var succeeded = service.TryAlignWithVpsg3(frame, map, "b1f", 1d,
                out var attempt, out var status, knownScaleSeed: 1.57476884614911,
                hasValidatedFloorScale: true);
            if (!succeeded || attempt is null)
                failures.Add($"{index}: {status}");
            else
            {
                Assert.False(attempt.Diagnostics.ScaleBootstrapValidated);
                Assert.True(attempt.Diagnostics.LowStructureValidatedScaleSeed);
                var evidence = LowStructureScaleEvidenceRules.ObserveAlignment(attempt);
                Assert.True(evidence.Accepted);
                Assert.False(evidence.Pending);
                Assert.Equal(0, evidence.Count);
                var classified = FloorAlignmentRecoveryRules.ClassifyAttemptResult(map.Id, "b1f",
                    MapAlignmentChannel.LowStructure, attempt, null, .80d, evidence.Accepted, evidence.Pending);
                Assert.Equal(FloorAlignmentAttemptOutcome.Accepted, classified.Outcome);
                Assert.Equal(1.57476884614911, attempt.Recognition!.Result.OverlayTransform!.ScaleX);
            }
            Assert.Equal(1.57476884614911, result.BestCandidate.Scale);
            if (result.RunnerUpCandidate is { } runner)
                Assert.Equal(1.57476884614911, runner.Scale);
            rows.Add(new { index, result.IsAccepted, result.FallbackReason, result.Scale,
                result.OffsetX, result.OffsetY, result.Confidence, result.ApertureMargin,
                result.PassedPartitions, result.Timing, precision, succeeded,
                serviceMilliseconds = attempt?.Diagnostics.TotalMilliseconds,
                committedTransform = attempt?.Recognition?.Result.OverlayTransform });
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(report)!, "prebuilt-fixed-trial.json"),
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(35, rows.Count);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        using var empty = new CapturedGameFrame(Mat.Zeros(1055, 1321, MatType.CV_8UC3).ToMat(),
            new MapScreenRect(0, 0, 2560, 1600), viewport, IntPtr.Zero);
        Assert.False(service.TryAlignWithVpsg3(empty, map, "b1f", 1d, out _, out _,
            knownScaleSeed: 1.57476884614911));
        Assert.False(service.TryAlignWithVpsg3(empty, map, "b1f", 1d, out _, out _,
            knownScaleSeed: 1.57476884614911, hasValidatedFloorScale: true));
    }
}
