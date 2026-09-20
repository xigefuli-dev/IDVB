using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class OtherFloorCompleteAlignmentTests
{
    [Fact]
    public async Task FloorSwitch_AlignsEachSelectedFloorWithItsOwnReference()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        Assert.Equal(
            CompleteAlignmentTestScenario.UpperFloor,
            MapFloorRules.GetNextFloorKey(
                scenario.Map,
                CompleteAlignmentTestScenario.MainFloor));
        Assert.Equal(
            CompleteAlignmentTestScenario.BasementFloor,
            MapFloorRules.GetNextFloorKey(
                scenario.Map,
                CompleteAlignmentTestScenario.UpperFloor));

        var upperCrop = new Rect(80, 60, 440, 340);
        var upperViewport = new MapScreenRect(
            650d,
            330d,
            upperCrop.Width,
            upperCrop.Height);
        using var upperFrame = scenario.FloorFrame(
            CompleteAlignmentTestScenario.UpperFloor,
            upperCrop,
            upperViewport);
        var upper = AlignFloor(
            scenario,
            CompleteAlignmentTestScenario.UpperFloor,
            upperFrame);

        var basementCrop = new Rect(140, 90, 420, 330);
        var basementViewport = new MapScreenRect(
            830d,
            410d,
            basementCrop.Width,
            basementCrop.Height);
        using var basementFrame = scenario.FloorFrame(
            CompleteAlignmentTestScenario.BasementFloor,
            basementCrop,
            basementViewport);
        var basement = AlignFloor(
            scenario,
            CompleteAlignmentTestScenario.BasementFloor,
            basementFrame);

        Assert.Equal(CompleteAlignmentTestScenario.UpperFloor, upper.Result.Floor);
        Assert.Equal(CompleteAlignmentTestScenario.BasementFloor, basement.Result.Floor);
        Assert.NotEqual(upper.FloorImagePath, basement.FloorImagePath);
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            upper.Result.OverlayTransform,
            1d,
            upperViewport.X - upperCrop.X,
            upperViewport.Y - upperCrop.Y,
            4d);
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            basement.Result.OverlayTransform,
            1d,
            basementViewport.X - basementCrop.X,
            basementViewport.Y - basementCrop.Y,
            4d);
    }

    [Fact]
    public async Task OtherFloor_NoGateScanAndAlignment_UsesStructureOnly()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var crop = new Rect(105, 75, 430, 335);
        var viewport = new MapScreenRect(710d, 370d, crop.Width, crop.Height);
        using var frame = scenario.FloorFrame(
            CompleteAlignmentTestScenario.UpperFloor,
            crop,
            viewport);

        var attempt = scenario.Service.AlignFloorWithoutGates(
            frame,
            scenario.Map.Id,
            CompleteAlignmentTestScenario.UpperFloor,
            scenario.FloorScaleSeed(CompleteAlignmentTestScenario.UpperFloor),
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        Assert.Equal(0, attempt.Diagnostics.GateCandidateCount);
        Assert.True(attempt.StructureAttempted);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        Assert.Equal(MapRecognitionSource.StructureMatching, recognition.Result.Source);
        Assert.Equal(MapAlignmentEvidenceKind.Structure, recognition.Result.EvidenceKind);
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            viewport.X - crop.X,
            viewport.Y - crop.Y,
            4d);
    }

    [Fact]
    public async Task OtherFloor_NoGateConfidence_RemainsNumericAndMatchesStructureResult()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var crop = new Rect(120, 85, 410, 320);
        var viewport = new MapScreenRect(760d, 390d, crop.Width, crop.Height);
        using var frame = scenario.FloorFrame(
            CompleteAlignmentTestScenario.BasementFloor,
            crop,
            viewport);

        var attempt = scenario.Service.AlignFloorWithoutGates(
            frame,
            scenario.Map.Id,
            CompleteAlignmentTestScenario.BasementFloor,
            scenario.FloorScaleSeed(CompleteAlignmentTestScenario.BasementFloor),
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        var structure = Assert.IsType<MapStructureRegistrationResult>(attempt.StructureResult);
        Assert.True(double.IsFinite(structure.Confidence));
        Assert.InRange(structure.Confidence, 0d, 1d);
        Assert.Equal(structure.Confidence, recognition.Result.Confidence, 12);
        Assert.Matches(
            @"\d+\.\d\s*%",
            structure.Confidence.ToString(
                "P1",
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(double.IsFinite(attempt.Diagnostics.StructureBestScore));
        Assert.True(double.IsFinite(attempt.Diagnostics.StructureCandidateMargin));
    }

    [Fact]
    public async Task OtherFloor_CompleteAlignment_StaysWithinPerformanceBudget()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var samples = new[]
        {
            (
                Floor: CompleteAlignmentTestScenario.UpperFloor,
                Crop: new Rect(80, 60, 440, 340),
                Viewport: new MapScreenRect(650d, 330d, 440d, 340d)),
            (
                Floor: CompleteAlignmentTestScenario.BasementFloor,
                Crop: new Rect(120, 85, 410, 320),
                Viewport: new MapScreenRect(760d, 390d, 410d, 320d))
        };
        var stopwatch = Stopwatch.StartNew();

        foreach (var sample in samples)
        {
            using var frame = scenario.FloorFrame(
                sample.Floor,
                sample.Crop,
                sample.Viewport);
            var attempt = scenario.Service.AlignFloorWithoutGates(
                frame,
                scenario.Map.Id,
                sample.Floor,
                scenario.FloorScaleSeed(sample.Floor),
                MapOverlayAlignmentMode.Uniform,
                CompleteAlignmentTestScenario.RecognitionTuning,
                CompleteAlignmentTestScenario.StructureTuning);
            Assert.True(
                attempt.Recognition is not null,
                $"Floor {sample.Floor} failed: {attempt.FailureReason}");
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(8),
            $"Two floor chains took {stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
    }

    private static RuntimeMapRecognition AlignFloor(
        CompleteAlignmentTestScenario scenario,
        string floorKey,
        CapturedGameFrame frame)
    {
        var attempt = scenario.Service.AlignFloorWithoutGates(
            frame,
            scenario.Map.Id,
            floorKey,
            scenario.FloorScaleSeed(floorKey),
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        return Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
    }
}
