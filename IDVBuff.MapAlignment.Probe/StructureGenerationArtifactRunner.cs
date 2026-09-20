using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.MapAlignment.Probe;

public static class StructureGenerationArtifactRunner
{
    private const string DefaultResearchSession =
        @"C:\Users\Thoth Crestwell\AppData\Local\IDVB\AlignmentResearch\sessions\2026-08-20_125835--3bcecb15";
    private const string DefaultMapRoot =
        @"C:\Users\Thoth Crestwell\AppData\Local\IDVB\Maps\efe3311d93fe4ed3abca5f196fb16669";
    private const string MapShortId = "efe3311d";

    private static readonly Sample[] Samples =
    [
        new("1f", "002-ok-high-84%", "ok-high-84"),
        new("1f", "005-ok-low-61%", "ok-low-61"),
        new("1f", "001-rejected-ambiguous-62%", "rejected-ambiguous-62"),
        new("2f", "002-ok-high-88%", "ok-high-88"),
        new("2f", "001-ok-low-53%", "ok-low-53"),
        new("2f", "002-rejected-49%", "rejected-49")
    ];

    public static int Run(
        string outputRoot,
        string? researchSession = null,
        string? mapRoot = null)
    {
        var resolvedOutputRoot = Path.GetFullPath(outputRoot);
        var resolvedResearchSession = Path.GetFullPath(
            researchSession ?? DefaultResearchSession);
        var resolvedMapRoot = Path.GetFullPath(mapRoot ?? DefaultMapRoot);
        Directory.CreateDirectory(resolvedOutputRoot);

        var preprocessor = new MapStructurePreprocessor();
        var generated = new List<object>();
        foreach (var sample in Samples)
        {
            var sampleDirectory = Path.Combine(
                resolvedResearchSession,
                MapShortId,
                sample.Floor,
                sample.CaseDirectory);
            var viewportPath = Path.Combine(sampleDirectory, "viewport.png");
            var referencePath = Path.Combine(
                resolvedMapRoot,
                sample.Floor == "1f"
                    ? "floor-1-recognition.png"
                    : "floor-2-recognition.png");
            if (!File.Exists(viewportPath))
                throw new FileNotFoundException("研究 Viewport 不存在。", viewportPath);
            if (!File.Exists(referencePath))
                throw new FileNotFoundException("本机地图识别图不存在。", referencePath);

            var outputDirectory = Path.Combine(
                resolvedOutputRoot,
                sample.Floor,
                sample.OutputName);
            Directory.CreateDirectory(outputDirectory);

            using var viewport = Cv2.ImRead(viewportPath, ImreadModes.Unchanged);
            using var reference = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
            if (viewport.Empty() || reference.Empty())
                throw new InvalidDataException(
                    $"无法读取样本：{sample.Floor}/{sample.CaseDirectory}");

            var legacyTuning = MapStructureGenerationTuning.CreateLegacyBaseline();
            legacyTuning.Normalize();
            var improvedTuning = new MapStructureGenerationTuning();
            improvedTuning.Normalize();

            using var beforeLive = preprocessor.ProcessLiveRoiDiagnostic(
                viewport,
                ignoreRegions: null,
                dynamicIgnoreRegions: null,
                out var beforeLiveTiming,
                profile: MapStructurePreprocessingProfile.EdgesOnly,
                generationTuning: legacyTuning);
            using var afterLive = preprocessor.ProcessLiveRoiDiagnostic(
                viewport,
                ignoreRegions: null,
                dynamicIgnoreRegions: null,
                out var afterLiveTiming,
                profile: MapStructurePreprocessingProfile.EdgesOnly,
                generationTuning: improvedTuning);
            using var beforeReference = preprocessor.ProcessReference(
                reference,
                ignoreRegions: null,
                generationTuning: legacyTuning);
            using var afterReference = preprocessor.ProcessReference(
                reference,
                ignoreRegions: null,
                generationTuning: improvedTuning);

            Cv2.ImWrite(
                Path.Combine(outputDirectory, "before-live-structure.png"),
                beforeLive.Edges);
            Cv2.ImWrite(
                Path.Combine(outputDirectory, "before-live-structure-mask.png"),
                beforeLive.StructureMask);
            Cv2.ImWrite(
                Path.Combine(outputDirectory, "after-live-structure.png"),
                afterLive.Edges);
            Cv2.ImWrite(
                Path.Combine(outputDirectory, "after-live-structure-mask.png"),
                afterLive.StructureMask);
            Cv2.ImWrite(
                Path.Combine(outputDirectory, "before-reference-structure.png"),
                beforeReference.Edges);
            Cv2.ImWrite(
                Path.Combine(outputDirectory, "after-reference-structure.png"),
                afterReference.Edges);

            var metrics = new
            {
                sample.Floor,
                sample.CaseDirectory,
                ViewportPath = viewportPath,
                ReferencePath = referencePath,
                ViewportSize = new { viewport.Width, viewport.Height },
                ReferenceSize = new { reference.Width, reference.Height },
                AlgorithmVersion = MapStructurePreprocessor.AlgorithmVersion,
                Before = new
                {
                    GenerationFingerprint = legacyTuning.CacheFingerprint,
                    Live = Describe(beforeLive, beforeLiveTiming),
                    Reference = Describe(beforeReference, beforeReference.DiagnosticTiming)
                },
                After = new
                {
                    GenerationFingerprint = improvedTuning.CacheFingerprint,
                    Live = Describe(afterLive, afterLiveTiming),
                    Reference = Describe(afterReference, afterReference.DiagnosticTiming)
                },
                LiveEdgePixelDelta =
                    Cv2.CountNonZero(afterLive.Edges)
                    - Cv2.CountNonZero(beforeLive.Edges),
                ReferenceEdgePixelDelta =
                    Cv2.CountNonZero(afterReference.Edges)
                    - Cv2.CountNonZero(beforeReference.Edges),
                LiveChangedPixelCount = ChangedPixels(
                    beforeLive.Edges,
                    afterLive.Edges),
                ReferenceChangedPixelCount = ChangedPixels(
                    beforeReference.Edges,
                    afterReference.Edges)
            };
            WriteJson(Path.Combine(outputDirectory, "metrics.json"), metrics);

            var manifest = new
            {
                GeneratedAt = DateTimeOffset.Now,
                Sample = sample.OutputName,
                Source = new
                {
                    MapId = "efe3311d-93fe-4ed3-abca-5f196fb16669",
                    sample.Floor,
                    ViewportPath = viewportPath,
                    ReferencePath = referencePath
                },
                BeforeProfile = "legacy-live-canny-only",
                AfterProfile = "default-live-gradient-and-canny",
                BeforeFingerprint = legacyTuning.CacheFingerprint,
                AfterFingerprint = improvedTuning.CacheFingerprint,
                AlgorithmVersion = MapStructurePreprocessor.AlgorithmVersion
            };
            WriteJson(Path.Combine(outputDirectory, "manifest.json"), manifest);
            generated.Add(metrics);
        }

        WriteJson(
            Path.Combine(resolvedOutputRoot, "manifest.json"),
            new
            {
                GeneratedAt = DateTimeOffset.Now,
                AlgorithmVersion = MapStructurePreprocessor.AlgorithmVersion,
                ResearchSession = resolvedResearchSession,
                MapRoot = resolvedMapRoot,
                SampleCount = generated.Count,
                Samples = generated
            });
        return generated.Count == Samples.Length ? 0 : 1;
    }

    private static object Describe(
        MapStructureFeatures features,
        PreprocessTiming? timing) => new
    {
        EdgePixelCount = Cv2.CountNonZero(features.Edges),
        StructurePixelCount = Cv2.CountNonZero(features.StructureMask),
        NuisancePixelCount = Cv2.CountNonZero(features.NuisanceMask),
        EdgeComponentCount = timing?.EdgeComponentCount ?? 0,
        StructureComponentCount = timing?.StructureComponentCount ?? 0,
        KeptStructureComponentCount = timing?.KeptStructureComponentCount ?? 0,
        GenerationFingerprint = timing?.GenerationFingerprint ?? string.Empty,
        EdgeComposition = timing?.EdgeComposition.ToString() ?? string.Empty
    };

    private static int ChangedPixels(Mat before, Mat after)
    {
        using var difference = new Mat();
        Cv2.Absdiff(before, after, difference);
        return Cv2.CountNonZero(difference);
    }

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions { WriteIndented = true }));

    private sealed record Sample(
        string Floor,
        string CaseDirectory,
        string OutputName);
}
