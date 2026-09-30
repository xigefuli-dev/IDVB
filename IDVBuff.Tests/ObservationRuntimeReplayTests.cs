using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

// Local diagnostic replay: consumes captured frames and reads the catalog without
// constructing its live repository (whose constructor may recover/migrate data).
[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class ObservationRuntimeReplayTests
{
    [ReplayFact]
    public void GateCoarseSearchRetainsSourceResolutionGatesAcrossMatch()
    {
        var root = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPLAY")!;
        var output = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPORT")!;
        var match = Directory.GetParent(root)!.Parent!.FullName;
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scan.json")));
        var viewport = saved.RootElement.GetProperty("ViewportBounds").Deserialize<MapScreenRect>();
        var client = saved.RootElement.GetProperty("ClientBounds").Deserialize<MapScreenRect>();
        using var detector = new GateTemplateDetector(Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png"));
        using var service = new MapCvRecognitionService(new MapRepository(Path.Combine(Path.GetDirectoryName(output)!, "empty-catalog")));
        var method = typeof(MapCvRecognitionService).GetMethod("DetectScanGates", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var rows = new List<object>();
        foreach (var path in Directory.GetFiles(Path.Combine(match, "显示区域"), "*.png"))
        {
            using var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
            using var gray = GateTemplateDetector.CreateMatchImage(image);
            var search = new GateSearchContext { AllowDualGateEarlyExit = false, AllowSingleGateEarlyExit = false };
            var before = detector.Detect(gray, viewport, client.Width, .72, search);
            var after = (GateDetectionResult)method.Invoke(service, [gray, viewport, client.Width, .72, search])!;
            foreach (var gate in before.Gates)
                Assert.True(after.Gates.Any(g => Math.Abs(g.ScreenBounds.CenterX - gate.ScreenBounds.CenterX) <= 4
                    && Math.Abs(g.ScreenBounds.CenterY - gate.ScreenBounds.CenterY) <= 4), $"Gate lost: {path}");
            Assert.All(after.Gates, gate => Assert.True(gate.Score >= .72));
            rows.Add(new { file = Path.GetFileName(path), beforeMs = before.ElapsedMilliseconds,
                afterMs = after.ElapsedMilliseconds, before = before.Gates.Count, after = after.Gates.Count });
        }
        File.WriteAllText(Path.ChangeExtension(output, ".gates.json"), JsonSerializer.Serialize(rows));
    }

    public sealed class ReplayFactAttribute : FactAttribute
    {
        public ReplayFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPLAY") is null)
                Skip = "Requires local captured scan evidence.";
        }
    }

    [ReplayFact]
    public async Task ReplayCapturedScan()
    {
        var configField = typeof(SideEntranceScanRules).GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousConfig = (SideEntranceScanConfig)configField.GetValue(null)!;
        var minimumScale = double.TryParse(Environment.GetEnvironmentVariable("IDVB_OBSERVATION_MIN_SCALE"),
            System.Globalization.CultureInfo.InvariantCulture, out var requestedMinimum) ? requestedMinimum : previousConfig.MinimumScale;
        var parallelism = int.TryParse(Environment.GetEnvironmentVariable("IDVB_OBSERVATION_PARALLELISM"),
            out var requestedParallelism) ? Math.Max(1, requestedParallelism) : previousConfig.ScanParallelism;
        var preparationMilliseconds = double.TryParse(Environment.GetEnvironmentVariable("IDVB_OBSERVATION_PREPARATION_MS"),
            System.Globalization.CultureInfo.InvariantCulture, out var preparation) ? preparation : 0;
        var root = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPLAY")!;
        var mapsRoot = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_MAPS")!;
        var output = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPORT")!;
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapsRoot, "maps.json")));
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scan.json")));
        var viewport = saved.RootElement.GetProperty("ViewportBounds").Deserialize<MapScreenRect>();
        var client = saved.RootElement.GetProperty("ClientBounds").Deserialize<MapScreenRect>();
        var mapClass = saved.RootElement.GetProperty("candidates")[0].GetProperty("Class").GetString();
        var maps = catalog.RootElement.GetProperty("Maps").Deserialize<MapRecord[]>()!
            .Where(m => m.Class == mapClass).ToArray();
        var inputs = new List<(MapRecord map, string floorKey, Mat featureTemplate)>();
        foreach (var map in maps)
        {
            var floor = MapScanFloorRules.ResolveScanFloorKey(map);
            var definition = map.Floors.Single(f => f.Key == floor);
            var path = Path.Combine(mapsRoot, map.Id.ToString("N"), definition.PrebuiltStructureLine!.FileName);
            var line = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Grayscale);
            Cv2.Threshold(line, line, 128, 255, ThresholdTypes.Binary);
            var a = MapScanFloorRules.GetScanFeatureAnchor(map, floor)!.Bounds!;
            var box = new Rect((int)Math.Floor(a.X * line.Width), (int)Math.Floor(a.Y * line.Height),
                (int)Math.Ceiling(a.Width * line.Width), (int)Math.Ceiling(a.Height * line.Height))
                .Intersect(new Rect(0, 0, line.Width, line.Height));
            Cv2.Rectangle(line, box, Scalar.Black, -1);
            _ = ScanStructureIndex.Get(line);
            inputs.Add((map, floor, line));
        }
        try
        {
            SideEntranceScanRules.ApplyConfig(new SideEntranceScanConfig
            {
                MinimumScale = minimumScale, MaximumScale = previousConfig.MaximumScale,
                ScanParallelism = parallelism
            });
            using var image = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(root, "viewport.png")), ImreadModes.Color);
            using var gray = GateTemplateDetector.CreateMatchImage(image);
            using var detector = new GateTemplateDetector(Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png"));
            var search = new GateSearchContext { AllowDualGateEarlyExit = false, AllowSingleGateEarlyExit = false };
            var before = detector.Detect(gray, viewport, client.Width, .72, search);
            using var service = new MapCvRecognitionService(new MapRepository(Path.Combine(Path.GetDirectoryName(output)!, "empty-catalog")));
            var method = typeof(MapCvRecognitionService).GetMethod("DetectScanGates", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var rows = new List<object>();
            object? refinedResult = null;
            object? formalResult = null;
            var formalChecks = new List<(bool accepted, ScanIdentityEvidence? evidence)>();
            Guid? expectedIdentity = null;
            Guid? selectedIdentity = null;
            using var formalService = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_VERIFY_REPO") is { } verificationRepo
                ? new MapCvRecognitionService(new MapRepository(verificationRepo)) : null;
            if (formalService is not null) await formalService.RefreshCacheAsync();
            for (var run = 0; run < 2; run++)
            {
                using var context = ScanExecutionContext.Enter(ScanPerformanceMode.Balanced,
                    startedTimestamp: Stopwatch.GetTimestamp() - (long)(preparationMilliseconds * Stopwatch.Frequency / 1000));
                var after = (GateDetectionResult)method.Invoke(service, [gray, viewport, client.Width, .72, search])!;
                foreach (var gate in before.Gates)
                    Assert.True(after.Gates.Any(g => Math.Abs(g.ScreenBounds.CenterX - gate.ScreenBounds.CenterX) <= 4
                        && Math.Abs(g.ScreenBounds.CenterY - gate.ScreenBounds.CenterY) <= 4), $"Source gate lost: {root}");
                Assert.All(after.Gates, gate => Assert.True(gate.Score >= .72));
                var candidates = new SideEntranceScanPipeline().RunScan(image, inputs, after.Gates, inputs.Count, viewport);
                var evidence = new List<object>();
                foreach (var candidate in candidates)
                {
                    var hypotheses = new List<object>();
                    foreach (var h in candidate.SearchHypotheses)
                    {
                        var transform = new MapOverlayTransform { ScaleX = h.MatchScale, ScaleY = h.MatchScale,
                            OffsetX = viewport.X + h.MatchLocation.X, OffsetY = viewport.Y + h.MatchLocation.Y };
                        var e = ScanIdentityVerifier.Verify(context.Frame!, h.StructureIndex!, transform, viewport, context);
                        if (e.State == ScanIdentityState.Excluded && e.SupportedFraction >= .80
                            && SideEntranceScanPipeline.RefineIdentityPose(h, context.Frame!, viewport, context) is { } polished)
                        {
                            e = polished.IdentityEvidence;
                            transform = new MapOverlayTransform { ScaleX = polished.MatchScale, ScaleY = polished.MatchScale,
                                OffsetX = viewport.X + polished.MatchLocation.X, OffsetY = viewport.Y + polished.MatchLocation.Y };
                            if (formalService is not null && run == 1)
                            {
                                Assert.True(formalService.TryCreateSideEntranceAlignmentSeed(polished, viewport, out var seed, out var failure), failure);
                                // The outer image lease owns this shared source frame.
                                var actualFrame = new CapturedGameFrame(image, client, viewport, IntPtr.Zero);
                                using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapsRoot, "..", "MapRuntime", "settings.json")));
                                var tuning = MapScaleSeedResolver.CreateStrictInitialIdentityValidationTuning(
                                    settings.RootElement.GetProperty("StructureRegistrationTuning").Deserialize<MapStructureRegistrationTuning>()!);
                                tuning.Mode = MapStructureRegistrationMode.ScanVerification;
                                tuning.EnableScanCheapReject = false;
                                tuning.StructureFallbackBudgetMilliseconds = MapOpenAlignmentRouteRules.ScanVerificationFormalStructureBudgetMilliseconds;
                                tuning.EnableFeatureVoting = false; tuning.EnableEccRefinement = false;
                                tuning.EnableFastAlignment = true; tuning.FastFallbackToLegacy = false;
                                tuning.FastAlignmentShadowMode = false; tuning.FastCoarseTopK = 2;
                                tuning.MaximumTranslationCandidates = 2; tuning.TopCandidateCount = 2;
                                tuning.PreviousAlignmentSearchRadiusPixels = 48;
                                tuning.DisableScaleEarlyTermination = false; tuning.EnableVisibleMask = false;
                                tuning.EnableVisibleAwareShadow = false; tuning.EnableVisibleAwareInjection = false;
                                tuning.EnableVisibleAwareEarlyExit = false; tuning.Normalize();
                                var align = formalService.AlignSideEntrance(actualFrame, polished.Map.Id, seed,
                                    MapOverlayAlignmentMode.Uniform, new MapRecognitionTuning(), tuning,
                                    alignmentSearchContext: new AlignmentSearchContext { UseRestrictedStructureFallback = true,
                                        UseInitialHighPrecisionRecovery = true, GateSearch = new GateSearchContext {
                                            Mode = GateSearchMode.WarmScaleSearch, WarmScale = seed.GateTemplateScale,
                                            AllowDualGateEarlyExit = false, AllowSingleGateEarlyExit = false } });
                                var final = align.Recognition?.Result.OverlayTransform is { } t
                                    ? ScanIdentityVerifier.Verify(context.Frame!, h.StructureIndex!, t, viewport, context) : null;
                                formalResult = new { align.StructureAccepted, align.FailureReason, align.StructureFailureReason,
                                    transform = align.Recognition?.Result.OverlayTransform, evidence = final };
                                formalChecks.Add((align.StructureAccepted, final));
                                expectedIdentity = polished.Map.Id;
                                e = final ?? ScanIdentityEvidence.Unverified("alignment-not-confirmed");
                                Assert.False(context.HasAlignmentConstraint(new MapStructureRegistrationRequest
                                    { ReferenceImage = image, LiveRoi = image, ViewportBounds = viewport }));
                            }
                        }
                        h.IdentityEvidence = e;
                        hypotheses.Add(new { h.MatchScale, transform, evidence = e });
                    }
                    candidate.IdentityEvidence = candidate.SearchHypotheses.Select(h => h.IdentityEvidence)
                        .OrderByDescending(e => e.State == ScanIdentityState.Supported)
                        .ThenByDescending(e => e.State == ScanIdentityState.Unverified).First();
                    if (candidate.IdentityEvidence.State == ScanIdentityState.Supported)
                        candidate.Disposition = SideEntranceCandidateDisposition.Reliable;
                    evidence.Add(new { candidate.Map.Id, candidate.Map.Title, candidate.MatchScore,
                        state = candidate.IdentityEvidence.State.ToString(), hypotheses });
                }
                if (run == 1 && formalService is not null)
                {
                    Assert.Equal(inputs.Count, candidates.Count);
                    selectedIdentity = ScanIdentityVerifier.SelectIdentity(candidates,
                        context.RetrievalCompleted, context.CanCompute, context.VariantGroups);
                }
                rows.Add(new { run, elapsedMs = context.ElapsedMilliseconds, context.RetrievalCompleted,
                    preparationMilliseconds, context.TestedHypotheses,
                    context.Policy.MinimumScale, parallelism,
                    gates = after, candidates = evidence });
                if (run == 1)
                {
                    using var unrestricted = ScanExecutionContext.Enter(ScanPerformanceMode.Quality);
                    unrestricted.CompleteAutomaticPhase();
                    var best = candidates[0];
                    var refined = SideEntranceScanPipeline.RefineVariant(best, context.Frame!, viewport, unrestricted);
                    refinedResult = refined.Select(h => new { h.MatchScale, h.MatchLocation,
                        evidence = ScanIdentityVerifier.Verify(context.Frame!, h.StructureIndex!,
                            new MapOverlayTransform { ScaleX = h.MatchScale, ScaleY = h.MatchScale,
                                OffsetX = viewport.X + h.MatchLocation.X, OffsetY = viewport.Y + h.MatchLocation.Y },
                            viewport, unrestricted) }).ToArray();
                    using var diagnostic = image.Clone();
                    foreach (var p in context.Frame!.DensePoints)
                    {
                        var d = best.StructureIndex!.Distance((p.X - best.MatchLocation.X) / best.MatchScale,
                            (p.Y - best.MatchLocation.Y) / best.MatchScale, best.MatchScale);
                        Cv2.Circle(diagnostic, p, 1, d > ScanIdentityVerifier.SupportTolerancePixels
                            ? new Scalar(0, 0, 255) : new Scalar(0, 255, 0), -1);
                    }
                    File.WriteAllBytes(Path.ChangeExtension(output, ".png"), diagnostic.ImEncode(".png"));
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { baselineGates = before, rows, refinedResult, formalResult }, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            }));
            if (formalService is not null)
            {
                var check = Assert.Single(formalChecks);
                Assert.True(check.accepted);
                Assert.NotNull(check.evidence);
                Assert.Equal(ScanIdentityState.Supported, check.evidence.State);
                Assert.Equal(check.evidence.TotalPoints, check.evidence.TestedPoints);
                Assert.NotNull(expectedIdentity);
                Assert.Equal(expectedIdentity, selectedIdentity);
            }
        }
        finally
        {
            SideEntranceScanRules.ApplyConfig(previousConfig);
            foreach (var input in inputs) input.featureTemplate.Dispose();
        }
    }
}
