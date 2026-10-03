using System.Text.Json;
using System.Text.Json.Serialization;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class ScanFailureArchiveReplayTests
{
    [ObservationRuntimeReplayTests.ReplayFact]
    public void AuditStrongConflictEvidence()
    {
        var root = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPLAY")!;
        var mapsRoot = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_MAPS")!;
        var output = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPORT")!;
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapsRoot, "maps.json")));
        var maps = catalog.RootElement.GetProperty("Maps").Deserialize<MapRecord[]>()!;
        var archive = Directory.GetParent(root)!.Parent!.Parent!.FullName;
        var rows = new List<object>();
        foreach (var path in Directory.GetFiles(archive, "scan.json", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(Path.GetDirectoryName(p))!.StartsWith("20261002_1659", StringComparison.Ordinal)).Order())
        {
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            var c = saved.RootElement.GetProperty("candidates")[0];
            var map = maps.Single(m => m.Class == c.GetProperty("Class").GetString() && m.SequenceNumber == c.GetProperty("SequenceNumber").GetInt32());
            var floor = c.GetProperty("FloorKey").GetString()!;
            using var line = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(mapsRoot, map.Id.ToString("N"), map.Floors.Single(f => f.Key == floor).PrebuiltStructureLine!.FileName)), ImreadModes.Grayscale);
            var index = ScanStructureIndex.Get(line).WithScanAnchor(map, floor);
            using var source = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "viewport.png")), ImreadModes.Color);
            var viewport = saved.RootElement.GetProperty("ViewportBounds").Deserialize<MapScreenRect>();
            var gate = c.GetProperty("hypotheses")[0].GetProperty("AssociatedGate").Deserialize<GateDetection>()!;
            using var frame = new ScanFrameEvidence(source, viewport, [gate], ScanExecutionPolicy.For(ScanPerformanceMode.Quality));
            var scale = c.GetProperty("MatchScale").GetDouble();
            var location = c.GetProperty("MatchLocation").Deserialize<MapScreenRect>();
            double Distance(Point p) => index.IsUnknown((p.X-location.X)/scale, (p.Y-location.Y)/scale) ? 0 : index.Distance((p.X-location.X)/scale, (p.Y-location.Y)/scale, scale);
            var strong = frame.DensePoints.Where(p => frame.Observation.ProposalEdges.At<byte>(p.Y,p.X) != 0).ToArray();
            var cells = strong.GroupBy(p => Math.Min(3,p.X*4/source.Width)+4*Math.Min(3,p.Y*4/source.Height)).Select(g => new { cell=g.Key, total=g.Count(), hits=g.Count(p=>Distance(p)<=5.5) }).ToArray();
            Cv2.FindContours(frame.Observation.ProposalEdges, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
            rows.Add(new { capture=Path.GetFileName(Path.GetDirectoryName(path)), total=frame.DensePoints.Length, strong=strong.Length, support=strong.Count(p=>Distance(p)<=5.5)/(double)strong.Length, longest=contours.Max(c=>ScanIdentityVerifier.MeasureStraightConflict(c, Distance)), cells });
        }
        File.WriteAllText(Path.ChangeExtension(output, ".strong-audit.json"), JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
    }
    [ObservationRuntimeReplayTests.ReplayFact]
    public void AuditConflictPhotometry()
    {
        var root = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPLAY")!;
        var output = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPORT")!;
        using var source = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(root, "viewport.png")), ImreadModes.Color);
        using var observation = Vpsg3FastLiveExtractor.Extract(source);
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        var samples = Enumerable.Range(690, 65).Select(y => new {
            y, observed = observation.ObservedEdges.At<byte>(y, 141),
            strong = observation.ProposalEdges.At<byte>(y, 141),
            gray = Enumerable.Range(126, 31).Select(x => (int)gray.At<byte>(y, x)).ToArray()
        });
        File.WriteAllText(Path.ChangeExtension(output, ".photometry.json"), JsonSerializer.Serialize(samples));
        File.WriteAllBytes(Path.ChangeExtension(output, ".strong.png"), observation.ProposalEdges.ImEncode(".png"));
    }
    [ObservationRuntimeReplayTests.ReplayFact]
    public void CapturedWeakConflictDoesNotRejectSupportedIdentityInEveryMode()
    {
        var root = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPLAY")!;
        var mapsRoot = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_MAPS")!;
        var output = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPORT")!;
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scan.json")));
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapsRoot, "maps.json")));
        var c = saved.RootElement.GetProperty("candidates")[0];
        var map = catalog.RootElement.GetProperty("Maps").Deserialize<MapRecord[]>()!.Single(m =>
            m.Class == c.GetProperty("Class").GetString() && m.SequenceNumber == c.GetProperty("SequenceNumber").GetInt32());
        var floor = c.GetProperty("FloorKey").GetString()!;
        var definition = map.Floors.Single(f => f.Key == floor);
        using var line = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(mapsRoot, map.Id.ToString("N"),
            definition.PrebuiltStructureLine!.FileName)), ImreadModes.Grayscale);
        var anchor = MapScanFloorRules.GetScanFeatureAnchor(map, floor)!.Bounds!;
        Cv2.Rectangle(line, new Rect((int)Math.Floor(anchor.X * line.Width), (int)Math.Floor(anchor.Y * line.Height),
            (int)Math.Ceiling(anchor.Width * line.Width), (int)Math.Ceiling(anchor.Height * line.Height)), Scalar.Black, -1);
        var index = ScanStructureIndex.Get(line).WithScanAnchor(map, floor);
        using var source = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(root, "viewport.png")), ImreadModes.Color);
        var viewport = saved.RootElement.GetProperty("ViewportBounds").Deserialize<MapScreenRect>();
        var gate = c.GetProperty("hypotheses")[0].GetProperty("AssociatedGate").Deserialize<GateDetection>()!;
        var scale = c.GetProperty("MatchScale").GetDouble();
        var location = c.GetProperty("MatchLocation").Deserialize<MapScreenRect>();
        var rows = new List<object>();
        foreach (var mode in Enum.GetValues<ScanPerformanceMode>())
        {
            using var context = ScanExecutionContext.Enter(mode);
            context.CompleteAutomaticPhase();
            using var frame = new ScanFrameEvidence(source, viewport, [gate], context.Policy);
            var transform = new MapOverlayTransform { ScaleX = scale, ScaleY = scale,
                OffsetX = viewport.X + location.X, OffsetY = viewport.Y + location.Y };
            var baseline = ScanIdentityVerifier.Verify(frame, index, transform, viewport, context);
            var poses = new List<object>();
            ScanIdentityEvidence? best = null;
            for (var dy = -10; dy <= 10; dy++)
            for (var dx = -10; dx <= 10; dx++)
            {
                var pose = new MapOverlayTransform { ScaleX = scale, ScaleY = scale,
                    OffsetX = transform.OffsetX + dx, OffsetY = transform.OffsetY + dy };
                var evidence = ScanIdentityVerifier.Verify(frame, index, pose, viewport, context);
                if (best is null || ScanIdentityVerifier.FitCost(evidence) < ScanIdentityVerifier.FitCost(best)) best = evidence;
                if (evidence.State == ScanIdentityState.Supported) poses.Add(new { dx, dy, evidence });
            }
            rows.Add(new { mode, baseline, best, supported = poses });
            Assert.Equal(ScanIdentityState.Supported, baseline.State);
            Assert.NotEmpty(poses);
        }
        File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }

    [ObservationRuntimeReplayTests.ReplayFact]
    public async Task OrdinaryModesRetainUniqueIdentityThroughFinalStructureVerification()
    {
        var root = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPLAY")!;
        var mapsRoot = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_MAPS")!;
        var output = Environment.GetEnvironmentVariable("IDVB_OBSERVATION_REPORT")!;
        var archive = Directory.GetParent(root)!.Parent!.Parent!.FullName;
        using var service = new MapCvRecognitionService(new MapRepository(Path.Combine(Path.GetDirectoryName(output)!, "verification-catalog")));
        await service.RefreshCacheAsync();
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapsRoot, "maps.json")));
        using var initial = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scan.json")));
        var mapClass = initial.RootElement.GetProperty("candidates")[0].GetProperty("Class").GetString();
        var maps = catalog.RootElement.GetProperty("Maps").Deserialize<MapRecord[]>()!.Where(m => m.Class == mapClass).ToArray();
        var inputs = new List<(MapRecord map, string floorKey, Mat featureTemplate)>();
        var rows = new List<object>();
        try
        {
            foreach (var map in maps)
            {
                var floor = MapScanFloorRules.ResolveScanFloorKey(map);
                var definition = map.Floors.Single(f => f.Key == floor);
                var line = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(mapsRoot, map.Id.ToString("N"),
                    definition.PrebuiltStructureLine!.FileName)), ImreadModes.Grayscale);
                var anchor = MapScanFloorRules.GetScanFeatureAnchor(map, floor)!.Bounds!;
                Cv2.Rectangle(line, new Rect((int)Math.Floor(anchor.X * line.Width), (int)Math.Floor(anchor.Y * line.Height),
                    (int)Math.Ceiling(anchor.Width * line.Width), (int)Math.Ceiling(anchor.Height * line.Height)), Scalar.Black, -1);
                _ = ScanStructureIndex.Get(line);
                inputs.Add((map, floor, line));
            }
            var captures = Directory.GetFiles(archive, "scan.json", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(Path.GetDirectoryName(p))!.StartsWith("20261002_1659", StringComparison.Ordinal))
                .Order().ToArray();
            Assert.Equal(5, captures.Length);
            foreach (var path in captures)
            {
                using var saved = JsonDocument.Parse(File.ReadAllText(path));
                var viewport = saved.RootElement.GetProperty("ViewportBounds").Deserialize<MapScreenRect>();
                var expected = saved.RootElement.GetProperty("candidates")[0].GetProperty("SequenceNumber").GetInt32();
                var gate = saved.RootElement.GetProperty("candidates")[0].GetProperty("hypotheses")[0]
                    .GetProperty("AssociatedGate").Deserialize<GateDetection>()!;
                using var image = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "viewport.png")), ImreadModes.Color);
                var selectedMap = maps.Single(m => m.SequenceNumber == expected);
                var selectedFloor = MapScanFloorRules.ResolveScanFloorKey(selectedMap);
                var selectedDefinition = selectedMap.Floors.Single(f => f.Key == selectedFloor);
                using var selectedLine = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(mapsRoot, selectedMap.Id.ToString("N"),
                    selectedDefinition.PrebuiltStructureLine!.FileName)), ImreadModes.Grayscale);
                using var prepared = Vpsg3PreparedIndexBuilder.BuildFromMat(selectedLine,
                    new Vpsg3IndexCacheKey(selectedMap.Id, selectedFloor, "archive-replay", selectedMap.UpdatedAt, "archive-replay"));
                using var observed = Vpsg3FastLiveExtractor.Extract(image, viewport);
                // No scan pose, cached scale or another floor is provided.
                var aligned = Vpsg3FastBootstrapSolver.TrySolve(observed, prepared);
                Assert.True(aligned.IsAccepted, aligned.FallbackReason.ToString());
                Assert.InRange(Math.Abs(aligned.Scale - saved.RootElement.GetProperty("candidates")[0]
                    .GetProperty("MatchScale").GetDouble()), 0, .035);
                foreach (var mode in new[] { ScanPerformanceMode.Fast, ScanPerformanceMode.Balanced, ScanPerformanceMode.Quality })
                {
                    using var context = ScanExecutionContext.Enter(mode);
                    // Correctness replay uses the captured gate and no wall-clock deadline;
                    // capture scheduling and target-machine performance are separate evidence.
                    context.CompleteAutomaticPhase();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var candidates = new SideEntranceScanPipeline().RunScan(image, inputs, [gate], inputs.Count, viewport);
                    foreach (var candidate in candidates)
                    {
                        foreach (var h in candidate.SearchHypotheses)
                        {
                            Assert.True(SideEntranceScanPipeline.TryCreateAlignmentSeed(h, viewport, out var seed, out var failure), failure);
                            h.IdentityEvidence = ScanIdentityVerifier.Verify(context.Frame!, h.StructureIndex!, seed.LockedTransform, viewport, context);
                            if (ScanIdentityVerifier.HasRefinablePose(h.IdentityEvidence)
                                && SideEntranceScanPipeline.RefineIdentityPose(h, context.Frame!, viewport, context) is { } refined)
                                candidate.SearchHypotheses = candidate.SearchHypotheses.Append(refined).ToArray();
                        }
                        candidate.IdentityEvidence = candidate.SearchHypotheses.Select(h => h.IdentityEvidence)
                            .OrderByDescending(e => e.State == ScanIdentityState.Supported)
                            .ThenByDescending(e => e.State == ScanIdentityState.Unverified).ThenBy(ScanIdentityVerifier.FitCost).First();
                    }
                    var references = MapCandidatePresentationRules.SelectScanReferences(candidates, 5, mode);
                    var choices = references.Select(c => new MapRecognitionChoice { IsReferenceOnly = true,
                        Recognition = new() { Map = c.Map, Result = new() { MapId = c.Map.Id, Floor = c.FloorKey } } }).ToArray();
                    Assert.Equal(expected, references[0].Map.SequenceNumber);
                    Assert.True(context.RetrievalCompleted);
                    Assert.Equal(maps.Length, candidates.Count);
                    Assert.True(MapCandidatePresentationRules.CanPresentChoices(context, choices));
                    var strongCandidate = candidates.Single(c => c.Map.Id == selectedMap.Id);
                    // Scan rescue consumes the shared masked observation and this
                    // candidate's scale, unlike the independent cold-start probe.
                    var seedHypothesis = strongCandidate.SearchHypotheses.OrderByDescending(h => h.IdentityEvidence.State == ScanIdentityState.Supported)
                        .ThenBy(h => ScanIdentityVerifier.FitCost(h.IdentityEvidence)).First();
                    Assert.True(service.TryCreateSideEntranceAlignmentSeed(seedHypothesis, viewport, out var formalSeed, out var seedFailure), seedFailure);
                    var actualFrame = new CapturedGameFrame(image, saved.RootElement.GetProperty("ClientBounds").Deserialize<MapScreenRect>(), viewport, IntPtr.Zero);
                    var tuning = MapScaleSeedResolver.CreateStrictInitialIdentityValidationTuning(new MapStructureRegistrationTuning());
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
                    var formal = service.AlignSideEntrance(actualFrame, selectedMap.Id, formalSeed,
                        MapOverlayAlignmentMode.Uniform, new MapRecognitionTuning(), tuning,
                        alignmentSearchContext: new AlignmentSearchContext { UseRestrictedStructureFallback = true,
                            UseInitialHighPrecisionRecovery = true, GateSearch = new GateSearchContext {
                                Mode = GateSearchMode.WarmScaleSearch, WarmScale = formalSeed.GateTemplateScale,
                                AllowDualGateEarlyExit = false, AllowSingleGateEarlyExit = false } });
                    Vpsg3BootstrapResult? scanAligned = null;
                    var finalTransform = formal.Recognition?.Result.OverlayTransform;
                    var confidence = formal.Recognition?.Result.LocalizationConfidence ?? 0;
                    if (!formal.StructureAccepted)
                    {
                        scanAligned = Vpsg3FastBootstrapSolver.TrySolve(context.Frame!.Observation, prepared,
                            knownScaleSeed: seedHypothesis.MatchScale);
                        Assert.True(scanAligned.IsAccepted, scanAligned.FallbackReason);
                        finalTransform = new MapOverlayTransform { ScaleX = scanAligned.Scale, ScaleY = scanAligned.Scale,
                            OffsetX = scanAligned.OffsetX, OffsetY = scanAligned.OffsetY };
                        confidence = scanAligned.Confidence;
                    }
                    Assert.NotNull(finalTransform);
                    var finalEvidence = ScanIdentityVerifier.Verify(context.Frame!, strongCandidate.StructureIndex!, finalTransform, viewport, context);
                    Assert.True(finalEvidence.State == ScanIdentityState.Supported,
                        JsonSerializer.Serialize(new { capture=Path.GetFileName(Path.GetDirectoryName(path)), mode, finalEvidence, aligned }));
                    strongCandidate.IdentityEvidence = finalEvidence;
                    strongCandidate.VerifiedTransform = finalTransform;
                    strongCandidate.Disposition = SideEntranceCandidateDisposition.Reliable;
                    Assert.Equal(selectedMap.Id, ScanIdentityVerifier.SelectIdentity(candidates, context.RetrievalCompleted, true));
                    var session = new MapOpenSession();
                    var committed = session.LockAlignedMap(selectedMap.Id, selectedFloor,
                        MapSimilarityTransform.FromOverlay(finalTransform), MapLocationMethod.StructureTranslation, confidence);
                    Assert.True(committed.IsLocked);
                    rows.Add(new { capture = Path.GetFileName(Path.GetDirectoryName(path)), mode,
                        count = candidates.Count, selected = references[0].Map.SequenceNumber,
                        rawEvidence = references[0].IdentityEvidence, correctnessReplayMs = timer.Elapsed.TotalMilliseconds,
                        finalEvidence, formalAccepted = formal.StructureAccepted, formalFailure = formal.StructureFailureReason,
                        scanAligned, canPresent = true, automaticIdentity = true,
                        independentAlignment = aligned, selectedAlignmentLocked = committed.IsLocked });
                }
            }
        }
        finally
        {
            foreach (var input in inputs) input.featureTemplate.Dispose();
            File.WriteAllText(Path.ChangeExtension(output, ".retrieval.json"), JsonSerializer.Serialize(rows,
                new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        }
    }
}
