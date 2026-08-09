using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class MapMatchExitAndAlignmentPrerequisiteTests
{
    [Fact]
    public async Task ExitMatch_ClearsSelectedMapLeasePersistedSelectionAndWarmObservations()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var matches = new MapMatchSession();
        var matchOne = matches.Begin(PlayerSlot.Player1, "S1");
        var lease = new MapMatchMapLease();
        lease.Bind(matchOne, scenario.Map.Id);
        using (var frame = scenario.MainFrame(VisibleGates.Both))
        {
            var recognition = scenario.Service.Recognize(
                frame,
                MapOverlayAlignmentMode.Uniform,
                CompleteAlignmentTestScenario.RecognitionTuning);
            Assert.NotNull(recognition.Recognition);
        }
        Assert.NotNull(scenario.Service.LastGateTemplateScale);

        var settings = new MapRuntimeSettings
        {
            IsEnabled = true,
            FirstScanStrategy = FirstScanStrategy.SideEntrance,
            SelectedMapId = scenario.Map.Id,
            PersistentMiniMapEnabled = true
        };
        var ended = matches.End();
        settings = MapMatchLifecycleRules.CreateSettingsWithoutMatchSelection(
            settings);
        lease.Clear();
        scenario.Service.ResetMatchState();

        var settingsRoot = Path.Combine(scenario.Root, "runtime-settings");
        var repository = new MapRuntimeSettingsRepository(settingsRoot);
        await repository.SaveAsync(settings);
        var restored = await repository.LoadAsync();

        Assert.Equal(MapMatchState.Ended, ended.State);
        Assert.Null(ended.PlayerSlot);
        Assert.Null(ended.MapClass);
        Assert.Null(restored.SelectedMapId);
        Assert.Equal(FirstScanStrategy.SideEntrance, restored.FirstScanStrategy);
        Assert.True(restored.PersistentMiniMapEnabled);
        Assert.Null(lease.MapId);
        Assert.Equal(0, lease.MatchVersion);
        Assert.Null(scenario.Service.LastGateTemplateScale);

        var matchTwo = matches.Begin(PlayerSlot.Player2, "S1");
        Assert.False(lease.IsCurrent(matchTwo, scenario.Map.Id));
        Assert.True(MapMatchLifecycleRules.CanStart(
            MapAlignmentPrerequisiteKind.SideEntranceInitialScan,
            matchTwo,
            matchTwo,
            restored.FirstScanStrategy,
            lease));
    }

    [Theory]
    [InlineData(
        FirstScanStrategy.DoubleGate,
        MapAlignmentPrerequisiteKind.DoubleGateInitialScan)]
    [InlineData(
        FirstScanStrategy.SideEntrance,
        MapAlignmentPrerequisiteKind.SideEntranceInitialScan)]
    public void InitialScanPrerequisite_RequiresFreshCurrentMatchWithoutOldSelection(
        FirstScanStrategy strategy,
        MapAlignmentPrerequisiteKind operation)
    {
        var matches = new MapMatchSession();
        var matchOne = matches.Begin(PlayerSlot.Player1, "S1");
        var staleMapId = Guid.NewGuid();
        var lease = new MapMatchMapLease();
        lease.Bind(matchOne, staleMapId);
        matches.End();
        var matchTwo = matches.Begin(PlayerSlot.Player2, "S1");

        Assert.False(MapMatchLifecycleRules.CanStart(
            operation,
            matchTwo,
            matchTwo,
            strategy,
            lease,
            selectedMapId: staleMapId,
            alignmentSession: GatePairSession(staleMapId)));

        lease.Clear();
        Assert.True(MapMatchLifecycleRules.CanStart(
            operation,
            matchTwo,
            matchTwo,
            strategy,
            lease));
        Assert.False(MapMatchLifecycleRules.CanStart(
            operation,
            matchTwo,
            matchOne,
            strategy,
            lease));
        Assert.False(MapMatchLifecycleRules.CanStart(
            operation,
            matchTwo,
            matchTwo,
            strategy == FirstScanStrategy.DoubleGate
                ? FirstScanStrategy.SideEntrance
                : FirstScanStrategy.DoubleGate,
            lease));
    }

    [Theory]
    [InlineData(MapAlignmentPrerequisiteKind.DefaultDualGateAlignment)]
    [InlineData(MapAlignmentPrerequisiteKind.DefaultSingleGateAlignment)]
    [InlineData(MapAlignmentPrerequisiteKind.DefaultStructureAlignment)]
    public void DefaultStrategyAlignmentPrerequisite_RejectsPreviousMatchData(
        MapAlignmentPrerequisiteKind operation)
    {
        AssertAlignmentPrerequisiteLifecycle(
            FirstScanStrategy.DoubleGate,
            operation,
            GatePairSession);
    }

    [Theory]
    [InlineData(MapAlignmentPrerequisiteKind.SideSingleGateAlignment)]
    [InlineData(MapAlignmentPrerequisiteKind.SideStructureAlignment)]
    public void SideStrategyAlignmentPrerequisite_RejectsPreviousMatchData(
        MapAlignmentPrerequisiteKind operation)
    {
        AssertAlignmentPrerequisiteLifecycle(
            FirstScanStrategy.SideEntrance,
            operation,
            SideEntranceSession);
    }

    [Fact]
    public void SideStrategyAlignmentPrerequisite_RejectsGatePairOnlySession()
    {
        var matches = new MapMatchSession();
        var current = matches.Begin(PlayerSlot.Player1, "S1");
        var mapId = Guid.NewGuid();
        var lease = new MapMatchMapLease();
        lease.Bind(current, mapId);

        Assert.False(MapMatchLifecycleRules.CanStart(
            MapAlignmentPrerequisiteKind.SideSingleGateAlignment,
            current,
            current,
            FirstScanStrategy.SideEntrance,
            lease,
            selectedMapId: mapId,
            alignmentSession: GatePairSession(mapId)));
    }

    [Fact]
    public void OtherFloorStructurePrerequisite_RequiresCurrentMapLeaseAndValidScaleSeed()
    {
        var matches = new MapMatchSession();
        var current = matches.Begin(PlayerSlot.Player3, "S1");
        var mapId = Guid.NewGuid();
        var lease = new MapMatchMapLease();
        lease.Bind(current, mapId);
        var validSeed = Transform();

        Assert.True(MapMatchLifecycleRules.CanStart(
            MapAlignmentPrerequisiteKind.OtherFloorStructureAlignment,
            current,
            current,
            FirstScanStrategy.DoubleGate,
            lease,
            selectedMapId: mapId,
            floorScaleSeed: validSeed));
        Assert.False(MapMatchLifecycleRules.CanStart(
            MapAlignmentPrerequisiteKind.OtherFloorStructureAlignment,
            current,
            current,
            FirstScanStrategy.DoubleGate,
            lease,
            selectedMapId: mapId,
            floorScaleSeed: new MapOverlayTransform()));

        matches.End();
        var next = matches.Begin(PlayerSlot.Player4, "S1");
        Assert.False(MapMatchLifecycleRules.CanStart(
            MapAlignmentPrerequisiteKind.OtherFloorStructureAlignment,
            next,
            next,
            FirstScanStrategy.DoubleGate,
            lease,
            selectedMapId: mapId,
            floorScaleSeed: validSeed));
    }

    [Fact]
    public async Task ProductionSelectedAlignment_RejectsSingleGateAndNoGateWithoutSessionSeed()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        foreach (var gates in new[] { VisibleGates.SideOnly, VisibleGates.None })
        {
            using var frame = scenario.MainFrame(gates);
            var attempt = scenario.Service.AlignSelected(
                frame,
                scenario.Map.Id,
                session: null,
                MapOverlayAlignmentMode.Uniform,
                CompleteAlignmentTestScenario.RecognitionTuning,
                CompleteAlignmentTestScenario.StructureTuning);

            Assert.Null(attempt.Recognition);
            Assert.Equal(
                MapAlignmentTrackingMode.NeedsGatePair,
                attempt.Diagnostics.TrackingMode);
            Assert.NotEmpty(attempt.FailureReason);
        }
    }

    [Fact]
    public async Task ProductionOtherFloorAlignment_RejectsMissingScaleSeed()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var crop = new OpenCvSharp.Rect(80, 60, 420, 320);
        using var frame = scenario.FloorFrame(
            CompleteAlignmentTestScenario.UpperFloor,
            crop,
            new MapScreenRect(600d, 320d, crop.Width, crop.Height));

        var attempt = scenario.Service.AlignFloorWithoutGates(
            frame,
            scenario.Map.Id,
            CompleteAlignmentTestScenario.UpperFloor,
            new MapOverlayTransform(),
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        Assert.Null(attempt.Recognition);
        Assert.Equal(
            MapAlignmentTrackingMode.NeedsGatePair,
            attempt.Diagnostics.TrackingMode);
        Assert.Contains("scale seed", attempt.FailureReason);
    }

    private static void AssertAlignmentPrerequisiteLifecycle(
        FirstScanStrategy strategy,
        MapAlignmentPrerequisiteKind operation,
        Func<Guid, MapAlignmentSession> createSession)
    {
        var matches = new MapMatchSession();
        var matchOne = matches.Begin(PlayerSlot.Player1, "S1");
        var mapId = Guid.NewGuid();
        var lease = new MapMatchMapLease();
        lease.Bind(matchOne, mapId);
        var alignmentSession = createSession(mapId);

        Assert.True(MapMatchLifecycleRules.CanStart(
            operation,
            matchOne,
            matchOne,
            strategy,
            lease,
            selectedMapId: mapId,
            alignmentSession: alignmentSession));

        matches.End();
        var matchTwo = matches.Begin(PlayerSlot.Player2, "S1");
        Assert.False(MapMatchLifecycleRules.CanStart(
            operation,
            matchTwo,
            matchTwo,
            strategy,
            lease,
            selectedMapId: mapId,
            alignmentSession: alignmentSession));
    }

    private static MapAlignmentSession GatePairSession(Guid mapId) => new()
    {
        MapId = mapId,
        MapUpdatedAt = DateTimeOffset.UtcNow,
        FloorKey = "main",
        LockedTransform = Transform(),
        BaselineGateScale = 1d,
        HasGatePairLock = true,
        LastConfidence = 0.9d
    };

    private static MapAlignmentSession SideEntranceSession(Guid mapId) => new()
    {
        MapId = mapId,
        MapUpdatedAt = DateTimeOffset.UtcNow,
        FloorKey = "main",
        LockedTransform = Transform(),
        BaselineGateScale = 1d,
        HasGatePairLock = false,
        SideEntranceScanPriorConfidence = 0.9d,
        LastConfidence = 0.9d
    };

    private static MapOverlayTransform Transform() => new()
    {
        ScaleX = 1d,
        ScaleY = 1d,
        ReferenceWidth = 800,
        ReferenceHeight = 600,
        AlignmentMode = MapOverlayAlignmentMode.Uniform
    };
}
