using IDVBuff.Tests;
using IdentityVisionBridge.Vision;
using OpenCvSharp;
using Xunit;

namespace IDVB.Vision.Tests;

public sealed class EmbeddedVisionTests
{
    [Fact]
    public async Task EmptyRepositoryAndBlankFrameCannotReportSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "IDVB-VisionTests", Guid.NewGuid().ToString("N"));
        await using var engine = new IdvbVisionEngine(root);
        Assert.Empty(await engine.GetMapsAsync());
        using var blank = new Mat(240, 320, MatType.CV_8UC3, Scalar.Black);
        var result = await engine.ScanAsync(new() { Frame = Encode(blank) });
        Assert.False(result.Succeeded);
        Assert.False(result.CommittedToHost);
        Assert.Null(result.Transform);
        Assert.NotEmpty(result.OperationId);
    }

    [Fact]
    public async Task InvalidViewportAndDisposedEngineAreRejected()
    {
        await using var engine = new IdvbVisionEngine(Path.Combine(Path.GetTempPath(), "IDVB-VisionTests", Guid.NewGuid().ToString("N")));
        using var blank = new Mat(100, 100, MatType.CV_8UC3, Scalar.Black);
        var frame = Encode(blank) with { Viewport = new(10, 0, 100, 100) };
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ScanAsync(new() { Frame = frame }));
        await engine.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.InitializeAsync());
    }

    [Fact]
    public async Task UnknownFloorNeverFallsBackToAnotherFloor()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync(nativeStructure: true);
        await using var engine = new IdvbVisionEngine(Path.Combine(scenario.Root, "maps"));
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var result = await engine.AlignAsync(new()
        {
            Frame = Encode(frame.Image), MapId = scenario.Map.Id, FloorKey = "does-not-exist"
        });
        Assert.Equal(VisionOutcome.Rejected, result.Outcome);
        Assert.Equal("map-or-floor-not-found", result.Reason);
        Assert.Null(result.Transform);
    }

    [Fact]
    public async Task IndependentEngineReturnsProductionAlignmentWithoutDesktopHost()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync(nativeStructure: true);
        await using var engine = new IdvbVisionEngine(Path.Combine(scenario.Root, "maps"));
        await engine.InitializeAsync();
        Assert.Contains(await engine.GetMapsAsync(), map => map.Id == scenario.Map.Id);
        using var frame = scenario.MainFrame(VisibleGates.None);
        var result = await engine.AlignAsync(new()
        {
            Frame = Encode(frame.Image), MapId = scenario.Map.Id,
            FloorKey = CompleteAlignmentTestScenario.MainFloor, BudgetMilliseconds = 1000
        });
        Assert.True(result.Succeeded, result.Reason);
        Assert.False(result.CommittedToHost);
        Assert.Equal(scenario.Map.Id, result.MapId);
        Assert.Equal(CompleteAlignmentTestScenario.MainFloor, result.FloorKey);
        Assert.NotNull(result.Transform);
        Assert.InRange(result.Transform.ScaleX, .97, 1.03);
        Assert.InRange(result.Transform.OffsetX, -2, 2);
        Assert.InRange(result.Transform.OffsetY, -2, 2);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly =>
            assembly.GetName().Name is "Microsoft.WinUI" or "IDVB");
    }

    private static VisionFrame Encode(Mat image)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        return new() { EncodedImage = bytes };
    }

    [Fact]
    public async Task CroppedViewportAlignmentReturnsClientCoordinates()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync(nativeStructure: true);
        await using var engine = new IdvbVisionEngine(Path.Combine(scenario.Root, "maps"));
        using var frame = scenario.MainFrame(VisibleGates.None);
        var result = await engine.AlignAsync(new()
        {
            Frame = Encode(frame.Image) with
            {
                Viewport = new(100, 80, frame.Image.Width, frame.Image.Height),
                ClientWidth = frame.Image.Width + 200, ClientHeight = frame.Image.Height + 160
            },
            MapId = scenario.Map.Id, FloorKey = CompleteAlignmentTestScenario.MainFloor
        });
        Assert.True(result.Succeeded, result.Reason);
        Assert.InRange(result.Transform!.OffsetX, 98, 102);
        Assert.InRange(result.Transform.OffsetY, 78, 82);
    }

    [Fact]
    public async Task SideEntranceImageRunsRealScanAndNeverClaimsAHostCommit()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync(nativeStructure: true);
        await using var engine = new IdvbVisionEngine(Path.Combine(scenario.Root, "maps"));
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var result = await engine.ScanAsync(new()
        {
            Frame = Encode(frame.Image), MapClass = scenario.Map.Class, Mode = VisionScanMode.Quality
        });
        Assert.True(result.Succeeded, $"{result.Outcome}: {result.Reason}; "
            + string.Join("; ", result.Candidates.Select(value => $"{value.EvidenceState}:{value.Reason}")));
        Assert.Equal(scenario.Map.Id, result.MapId);
        Assert.NotNull(result.Transform);
        Assert.False(result.CommittedToHost);
        Assert.InRange(result.ElapsedMilliseconds, 0, 1000);
    }
}
