using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class SideEntranceStrategyCompleteAlignmentTests
{
    [Fact]
    public void InitialSideSelectionRecoveryUsesBroadNonTrackingSearch()
    {
        var initial = new AlignmentSearchContext
        {
            GateSearch = new GateSearchContext(),
            UseRestrictedStructureFallback = true,
            UseInitialHighPrecisionRecovery = true
        };
        var laterTracking = new AlignmentSearchContext
        {
            GateSearch = new GateSearchContext(),
            UseRestrictedStructureFallback = true
        };

        Assert.False(
            MapAlignmentSearchPolicy.UseTrackingForGlobalRecovery(initial));
        Assert.True(
            MapAlignmentSearchPolicy.UseTrackingForGlobalRecovery(
                laterTracking));
    }

    [Fact]
    public async Task SideScan_RequiresVisibleGateBeforeUsingCustomFeatureRegion()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.None);

        var scan = scenario.Service.RunSideEntranceScan(
            frame,
            CompleteAlignmentTestScenario.RecognitionTuning,
            topK: 5,
            mapClass: scenario.Map.Class);

        Assert.Null(scan.Gate);
        Assert.Empty(scan.Candidates);
        Assert.Contains("gate", scan.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SideScan_SeedKeepsMapScaleSeparateFromGateTemplateScale()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);

        var scan = scenario.Service.RunSideEntranceScan(
            frame,
            CompleteAlignmentTestScenario.RecognitionTuning,
            topK: 5,
            mapClass: scenario.Map.Class);
        var candidate = Assert.Single(scan.Candidates);
        var gate = scan.Gate;
        Assert.NotNull(gate);

        var created = scenario.Service.TryCreateSideEntranceAlignmentSeed(
            candidate,
            gate!,
            frame.ViewportBounds,
            out var seed,
            out var failureReason);

        Assert.True(created, failureReason);
        Assert.InRange(seed.BaselineGateScale, 0.90d, 1.10d);
        Assert.Equal(gate!.Scale, seed.GateTemplateScale!.Value, precision: 5);
        Assert.False(seed.HasGatePairLock);
        Assert.True(seed.SideEntranceScanPriorConfidence > 0d);
    }

    [Fact]
    public async Task LockedSideFeature_IsOnlyASeedAndRequiresStructureAcceptance()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, frame);
        using var alignmentBudget = MapNoDoorAlignmentBudgetContext.Enter(
            () => 1500);

        var attempt = scenario.Service.AlignLockedSideEntranceFeature(
            frame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(
            attempt.Recognition);
        Assert.True(attempt.StructureAttempted);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        Assert.False(attempt.Diagnostics.SkippedStructureValidation);
        Assert.Equal(0d, attempt.Diagnostics.GateDetectionMilliseconds);
        Assert.Equal(0d, attempt.Diagnostics.AuxiliaryAnchorMilliseconds);
        Assert.Equal(
            MapRecognitionSource.StructureMatching,
            recognition.Result.Source);
    }

    [Fact]
    public async Task SideScan_UsesOnlyPrimaryFloorAndCurrentMapClass()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);

        var candidates = scenario.Service.RunSideEntranceScan(
            frame.Image,
            topK: 5,
            mapClass: scenario.Map.Class);
        var candidate = Assert.Single(candidates);
        Assert.Equal(scenario.Map.Id, candidate.Map.Id);
        Assert.Equal(CompleteAlignmentTestScenario.MainFloor, candidate.FloorKey);

        Assert.Empty(scenario.Service.RunSideEntranceScan(
            frame.Image,
            topK: 5,
            mapClass: "another-class"));
    }

    [Fact]
    public async Task SideStrategy_ScanAndAlignment_LocksFromSideEntranceIdentity()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, frame);

        var attempt = scenario.Service.AlignSideEntrance(
            frame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        Assert.Equal(scenario.Map.Id, recognition.Map.Id);
        Assert.Equal(CompleteAlignmentTestScenario.MainFloor, recognition.Result.Floor);
        Assert.Equal(1, attempt.Diagnostics.GateCandidateCount);
        Assert.True(attempt.Diagnostics.GateDetectionMilliseconds > 0d);
        Assert.True(session.SideEntranceScanPriorConfidence > 0.80d);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        Assert.True(double.IsFinite(recognition.Result.Confidence));
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            100d,
            80d,
            4d);
    }

    [Fact]
    public async Task SideStrategy_DualGateFrame_NeverEntersDoubleGatePipeline()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var scanFrame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, scanFrame);
        using var dualGateFrame = scenario.MainFrame(
            VisibleGates.Both,
            new MapScreenRect(420d, 210d, 800d, 600d));

        var attempt = scenario.Service.AlignSideEntrance(
            dualGateFrame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        // 侧门链路跳过单门身份识别，直接由结构配准完成对齐，
        // 因此无锚点证据匹配
        Assert.Empty(recognition.Result.AnchorMatches);
        Assert.False(recognition.Result.HasAllRequiredAnchorEvidence);
        Assert.True(attempt.Diagnostics.GateCandidateCount >= 2);
        Assert.True(attempt.Diagnostics.GateDetectionMilliseconds > 0d);
        Assert.Equal(MapRecognitionSource.StructureMatching, recognition.Result.Source);
        Assert.Equal(MapAlignmentTrackingMode.StructureMatched, attempt.Diagnostics.TrackingMode);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        Assert.NotEqual(MapAlignmentEvidenceKind.DualGate, recognition.Result.EvidenceKind);
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            420d,
            210d,
            2d);
    }

    [Fact]
    public async Task SideStrategy_SingleGateFrame_AlignsWithSidePriorAndStructure()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var scanFrame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, scanFrame);
        var crop = new Rect(300, 20, 470, 360);
        var viewport = new MapScreenRect(750d, 330d, crop.Width, crop.Height);
        using var singleGateFrame = scenario.MainFrame(
            VisibleGates.SideOnly,
            viewport,
            crop);

        var attempt = scenario.Service.AlignSideEntrance(
            singleGateFrame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        Assert.Equal(1, attempt.Diagnostics.GateCandidateCount);
        Assert.True(attempt.Diagnostics.GateDetectionMilliseconds > 0d);
        // 侧门链路跳过单门身份识别，直接由结构配准完成对齐
        Assert.Equal(MapAlignmentTrackingMode.StructureMatched, attempt.Diagnostics.TrackingMode);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        Assert.InRange(recognition.Result.Confidence, 0d, 1d);
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            viewport.X - crop.X,
            viewport.Y - crop.Y,
            4d);
    }

    [Fact]
    public async Task SideStrategy_NoGateFrame_UsesKnownMapStructureForAlignment()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var scanFrame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, scanFrame);
        var crop = new Rect(220, 180, 420, 300);
        var viewport = new MapScreenRect(760d, 420d, crop.Width, crop.Height);
        using var noGateFrame = scenario.MainFrame(
            VisibleGates.None,
            viewport,
            crop);

        var attempt = scenario.Service.AlignSideEntrance(
            noGateFrame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        Assert.Equal(0, attempt.Diagnostics.GateCandidateCount);
        Assert.Equal(MapRecognitionSource.StructureMatching, recognition.Result.Source);
        Assert.Equal(MapAlignmentTrackingMode.StructureMatched, attempt.Diagnostics.TrackingMode);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        Assert.True(session.SideEntranceScanPriorConfidence > 0d);
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            viewport.X - crop.X,
            viewport.Y - crop.Y,
            4d);
    }

    [Fact]
    public void SideStrategy_Confidence_UsesSideSpecificNumericFormulas()
    {
        const double prior = 0.58d;
        const double gateScore = 0.36d;
        const double scaleAgreement = 0.20d;
        var singleGate = MapAlignmentConfidence.ComputeSideEntranceSingleGateConfidence(
            prior,
            gateScore,
            scaleAgreement);
        var structure = MapAlignmentConfidence.ComputeSideEntranceStructureConfidence(
            prior,
            locationQuality: 0.42d,
            candidateSeparation: 0.20d,
            featureConsensus: -1d,
            refinementQuality: -1d);

        Assert.Equal(0.432d, singleGate, 12);
        Assert.Equal((prior * 0.35d + 0.42d * 0.30d + 0.20d * 0.15d) / 0.80d,
            structure,
            12);
        Assert.All(
            new[] { singleGate, structure },
            confidence =>
            {
                Assert.True(double.IsFinite(confidence));
                Assert.InRange(confidence, 0d, 1d);
                Assert.Matches(
                    @"\d+\.\d\s*%",
                    confidence.ToString(
                        "P1",
                        System.Globalization.CultureInfo.InvariantCulture));
            });
    }

    [Fact]
    public async Task SideStrategy_CompleteAlignment_StaysWithinPerformanceBudget()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var stopwatch = Stopwatch.StartNew();

        var session = SeedWithSideEntranceStrategy(scenario, frame);
        var attempt = scenario.Service.AlignSideEntrance(
            frame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        stopwatch.Stop();
        Assert.NotNull(attempt.Recognition);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(7),
            $"Side-strategy chain took {stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
        Assert.True(attempt.Diagnostics.GateDetectionMilliseconds > 0d);
        Assert.Equal(1, attempt.Diagnostics.GateCandidateCount);
        Assert.True(attempt.Diagnostics.StructureSearchMilliseconds >= 0d);
    }

    [Fact]
    public async Task SideStrategy_CompleteAlignment_BatchHandlesGateVisibilityChanges()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var samples = new[]
        {
            (X: 100d, Y: 80d, Gates: VisibleGates.SideOnly),
            (X: 360d, Y: 180d, Gates: VisibleGates.Both),
            (X: 620d, Y: 300d, Gates: VisibleGates.MainOnly),
            (X: 900d, Y: 440d, Gates: VisibleGates.None)
        };
        var stopwatch = Stopwatch.StartNew();

        foreach (var sample in samples)
        {
            using var scanFrame = scenario.MainFrame(
                VisibleGates.SideOnly,
                new MapScreenRect(sample.X, sample.Y, 800d, 600d));
            var session = SeedWithSideEntranceStrategy(scenario, scanFrame);
            using var alignmentFrame = scenario.MainFrame(
                sample.Gates,
                new MapScreenRect(sample.X, sample.Y, 800d, 600d));
            var attempt = scenario.Service.AlignSideEntrance(
                alignmentFrame,
                scenario.Map.Id,
                session,
                MapOverlayAlignmentMode.Uniform,
                CompleteAlignmentTestScenario.RecognitionTuning,
                CompleteAlignmentTestScenario.StructureTuning);

            Assert.True(
                attempt.Recognition is not null,
                $"{sample.Gates} frame failed: {attempt.FailureReason}");
            if (sample.Gates == VisibleGates.None)
                Assert.Equal(0, attempt.Diagnostics.GateCandidateCount);
            else
                Assert.True(attempt.Diagnostics.GateCandidateCount > 0);
            Assert.NotEqual(
                MapRecognitionSource.SelectedMapGatePair,
                attempt.Recognition!.Result.Source);
            Assert.NotEqual(
                MapAlignmentEvidenceKind.DualGate,
                attempt.Recognition.Result.EvidenceKind);
            DefaultDualGateCompleteAlignmentTests.AssertTransform(
                attempt.Recognition!.Result.OverlayTransform,
                1d,
                sample.X,
                sample.Y,
                4d);
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"Four side-strategy chains took {stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
    }

    [Fact]
    public async Task SideEntrancePriorSurvivesTrackingAdvanceAndHold()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var seed = SeedWithSideEntranceStrategy(scenario, frame);
        var attempt = scenario.Service.AlignSideEntrance(
            frame,
            scenario.Map.Id,
            seed,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);
        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);

        var advanced = seed.Advance(scenario.Map, recognition.Result);
        var held = advanced.Hold(attempt.StructureResult);

        Assert.Equal(
            seed.SideEntranceScanPriorConfidence,
            advanced.SideEntranceScanPriorConfidence);
        Assert.Equal(
            seed.SideEntranceScanPriorConfidence,
            held.SideEntranceScanPriorConfidence);
        Assert.False(advanced.HasGatePairLock);
        Assert.False(held.HasGatePairLock);
    }

    /// <summary>
    /// Regression: once the side gate is not positively identified, the
    /// already-selected map must use ordinary structure alignment. In
    /// particular, a no-gate frame must not inherit the side-route scale band
    /// or restricted search basin.
    /// </summary>
    [Fact]
    public async Task SideStrategy_NoGateFallback_UsesOrdinaryStructureAlignment()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var scanFrame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, scanFrame);
        // Force pure structure (no single-gate proposal) so the search flags
        // under test are the ones used when gate identity fails in production.
        var crop = new Rect(220, 180, 420, 300);
        var viewport = new MapScreenRect(760d, 420d, crop.Width, crop.Height);
        using var noGateFrame = scenario.MainFrame(
            VisibleGates.None,
            viewport,
            crop);

        var structureTuning = CompleteAlignmentTestScenario.StructureTuning;
        // Keep a non-zero tracking scale band so this catches accidental
        // inheritance of the side-route scale search.
        structureTuning.TrackingScaleSearchRadius = 0.02d;
        structureTuning.ScaleSearchStep = 0.01d;
        structureTuning.Normalize();

        var attempt = scenario.Service.AlignSideEntrance(
            noGateFrame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            structureTuning);

        var structure = Assert.IsType<MapStructureRegistrationResult>(
            attempt.StructureResult);
        Assert.True(
            attempt.StructureAccepted,
            attempt.StructureFailureReason);
        Assert.True(
            structure.ScaleHypothesisCount == 1,
            "A no-gate frame must use the ordinary fixed-scale structure path "
            + $"(got ScaleHypothesisCount={structure.ScaleHypothesisCount}).");
        Assert.False(
            structure.UsedRestrictedSearch,
            "The changed viewport should use the global recovery branch "
            + "after local structure search fails.");
        Assert.Equal(
            MapRecognitionSource.StructureMatching,
            attempt.Recognition!.Result.Source);
    }

    private static MapAlignmentSession SeedWithSideEntranceStrategy(
        CompleteAlignmentTestScenario scenario,
        CapturedGameFrame frame)
    {
        var candidates = scenario.Service.RunSideEntranceScan(frame.Image, topK: 3);
        var candidate = Assert.Single(candidates);
        Assert.Equal(scenario.Map.Id, candidate.Map.Id);
        Assert.Equal(CompleteAlignmentTestScenario.MainFloor, candidate.FloorKey);
        Assert.True(candidate.MatchScore > 0.80d);
        var created = SideEntranceScanPipeline.TryCreateAlignmentSeed(
            candidate,
            frame.ViewportBounds,
            out var session,
            out var failureReason);
        Assert.True(created, failureReason);
        Assert.False(session.HasGatePairLock);
        Assert.True(session.SideEntranceScanPriorConfidence > 0d);
        return session;
    }
}
