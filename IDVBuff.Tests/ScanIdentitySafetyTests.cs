using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class ScanIdentitySafetyTests
{
    [Fact]
    public async Task TrackingTaskDoesNotInheritScanDeadlineOrFrame()
    {
        using var scan = ScanExecutionContext.Enter(ScanPerformanceMode.Fast);
        Task<ScanExecutionContext?> child;
        using (ScanExecutionContext.Suppress())
            child = Task.Run(() => ScanExecutionContext.Current);
        Assert.Same(scan, ScanExecutionContext.Current);
        Assert.Null(await child);
    }

    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    public void OneWrongRoomCannotHideBehindGlobalSupport(ScanPerformanceMode mode)
    {
        using var image = new Mat(600, 800, MatType.CV_8UC3, new Scalar(30, 25, 22));
        foreach (var room in new[] { new Rect(80, 130, 200, 140), new Rect(390, 120, 250, 200),
            new Rect(120, 400, 250, 120), new Rect(680, 380, 40, 40) })
            Cv2.Rectangle(image, room, new Scalar(110, 97, 88), -1);
        var viewport = new MapScreenRect(0, 0, 800, 600);
        using var frame = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(mode));
        using var reference = frame.Observation.ObservedEdges.Clone();
        var transform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1 };
        Assert.Equal(ScanIdentityState.Supported,
            ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(reference), transform, viewport, null).State);
        using var wrong = reference.Clone();
        Cv2.Rectangle(wrong, new Rect(665, 365, 75, 75), Scalar.Black, -1);
        using var context = ScanExecutionContext.Enter(mode);
        var conflict = ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(wrong), transform, viewport, context);
        Assert.Equal(ScanIdentityState.Excluded, conflict.State);
        Assert.True(conflict.SupportedFraction > ScanIdentityVerifier.MinimumSupport);
        Assert.True(conflict.LongestConflictPixels >= ScanIdentityVerifier.MaximumContinuousConflictPixels);
        var moved = new MapOverlayTransform { ScaleX = 1, ScaleY = 1, OffsetX = 50 };
        Assert.NotEqual(ScanIdentityState.Supported,
            ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(reference), moved, viewport, context).State);
    }

    [Fact]
    public void SupersededOrCancelledScanCannotContinueValidation()
    {
        var current = true;
        using var cancellation = new CancellationTokenSource();
        using var scan = ScanExecutionContext.Enter(ScanPerformanceMode.Quality, cancellation.Token, () => current);
        Assert.True(scan.CanCompute);
        current = false;
        Assert.True(scan.Expired);
        Assert.False(scan.CanCompute);
        current = true;
        cancellation.Cancel();
        Assert.False(scan.CanCompute);
    }

    [Fact]
    public void WeakSharpSemanticWallSurvivesWhileSmoothFogRemainsNeutral()
    {
        using var sharp = new Mat(240, 320, MatType.CV_8UC3, new Scalar(78, 70, 63));
        Cv2.Rectangle(sharp, new Rect(70, 70, 160, 100), new Scalar(106, 94, 84), -1);
        using var soft = new Mat();
        Cv2.GaussianBlur(sharp, soft, new Size(61, 61), 12);
        using var wall = Vpsg3FastLiveExtractor.Extract(sharp);
        using var fog = Vpsg3FastLiveExtractor.Extract(soft);
        Assert.True(wall.EdgePixelCount > 250, $"Weak wall had only {wall.EdgePixelCount} points");
        Assert.True(fog.EdgePixelCount < wall.EdgePixelCount / 5, $"Fog produced {fog.EdgePixelCount} points");
    }
    [Fact]
    public void UnfinishedCompetitorCannotBeHiddenByFirstSupportedResult()
    {
        var winner = Candidate(ScanIdentityState.Supported, 1);
        var pending = Candidate(ScanIdentityState.Unverified, 0);
        Assert.Null(ScanIdentityVerifier.SelectIdentity([winner, pending], true, true));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([winner], false, true));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([winner], true, false));
        pending.IdentityEvidence = pending.IdentityEvidence with { State = ScanIdentityState.Excluded };
        Assert.Equal(winner.Map.Id, ScanIdentityVerifier.SelectIdentity([winner, pending], true, true));
    }

    [Fact]
    public void ActualFitCanSelectSecondRetrievedMap()
    {
        var first = Candidate(ScanIdentityState.Supported, 2);
        var second = Candidate(ScanIdentityState.Supported, .4);
        Assert.Equal(second.Map.Id, ScanIdentityVerifier.SelectIdentity([first, second], true, true));
    }

    [Fact]
    public void OutOfReferencePointsRemainInScoreDenominator()
    {
        using var line = new Mat(100, 100, MatType.CV_8UC1, Scalar.Black);
        Cv2.Line(line, new(30, 0), new(30, 99), Scalar.White);
        var index = ScanStructureIndex.Get(line);
        Assert.Equal(.5, index.Score([new(30, 20), new(130, 20)], 1, 0, 0), 6);
    }

    [Fact]
    public void PolicyAndSettingsAreIndependentSnapshots()
    {
        var settings = new MapRuntimeSettings();
        Assert.Equal(ScanPerformanceMode.Balanced, settings.ScanPerformanceMode);
        settings.ScanPerformanceMode = ScanPerformanceMode.Quality;
        Assert.Equal(settings.ScanPerformanceMode, settings.Clone().ScanPerformanceMode);
        using var context = ScanExecutionContext.Enter(settings.ScanPerformanceMode);
        settings.ScanPerformanceMode = ScanPerformanceMode.Fast;
        Assert.Equal(512, context.Policy.SparsePoints);
        settings.ScanPerformanceMode = (ScanPerformanceMode)99;
        settings.Normalize();
        Assert.Equal(ScanPerformanceMode.Balanced, settings.ScanPerformanceMode);
        Assert.False(settings.SilentScanEnabled);
        Assert.Equal(ScanPerformanceMode.Balanced,
            System.Text.Json.JsonSerializer.Deserialize<MapRuntimeSettings>("{\"ScanPerformanceMode\":\"invalid\"}")!.ScanPerformanceMode);
    }

    private static SideEntranceScanCandidate Candidate(ScanIdentityState state, double distance) => new()
    {
        Map = new MapRecord { Id = Guid.NewGuid() },
        Disposition = SideEntranceCandidateDisposition.Reliable,
        IdentityEvidence = new(state, 100, 100, distance, .95, 0, "test")
    };
}
