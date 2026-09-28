using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class SideEntranceScanPipelineTests
{
    [Fact]
    public void MultiGateScanAssociatesCandidateWithItsOwnGate()
    {
        using var scene = new StructuralScanScene(x: 70, y: 50);
        var results = new SideEntranceScanPipeline().RunScan(scene.Frame,
            [(scene.Map, "1f", scene.Line)], detectedGates: [scene.UnrelatedGate, scene.Gate],
            viewportBounds: scene.Viewport);
        var candidate = Assert.Single(results);
        Assert.Same(scene.Gate, candidate.AssociatedGate);
        Assert.Equal(1, candidate.AssociatedGateIndex);
        Assert.Equal(SideEntranceGateAssociationKind.DetectedGate, candidate.GateAssociationKind);
        scene.AssertPose(candidate);
    }

    [Fact]
    public void MultiGateScanDoesNotRescueStructureWithoutAValidGate()
    {
        using var scene = new StructuralScanScene();
        var pipeline = new SideEntranceScanPipeline();
        Assert.Empty(pipeline.RunScan(scene.Frame, [(scene.Map, "1f", scene.Line)],
            detectedGates: [], viewportBounds: scene.Viewport));
        Assert.Empty(pipeline.RunScan(scene.Frame, [(scene.Map, "1f", scene.Line)]));
        var unrelatedGate = scene.UnrelatedGate;
        var candidates = pipeline.RunScan(scene.Frame, [(scene.Map, "1f", scene.Line)],
            detectedGates: [unrelatedGate], viewportBounds: scene.Viewport);
        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.Same(unrelatedGate, candidate.AssociatedGate);
            Assert.Equal(SideEntranceGateAssociationKind.DetectedGate, candidate.GateAssociationKind);
            Assert.All(candidate.SearchHypotheses, hypothesis =>
                Assert.NotEqual(ScanIdentityState.Supported, scene.Verify(hypothesis).State));
        });
        Assert.Null(ScanIdentityVerifier.SelectIdentity(candidates, true, true));
    }

    [Fact]
    public void MultiGateScanDoesNotRescueUnrelatedCandidatesWhenValidGateAssociationExists()
    {
        using var scene = new StructuralScanScene();
        var wrongMap = scene.CreateMatchingMap();
        using var wrongLine = new Mat(600, 800, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(wrongLine, new Rect(40, 40, 90, 70), Scalar.White, 1);
        var results = new SideEntranceScanPipeline().RunScan(scene.Frame,
            [(scene.Map, "1f", scene.Line), (wrongMap, "1f", wrongLine)],
            detectedGates: [scene.Gate], viewportBounds: scene.Viewport);
        // Retrieval is intentionally inclusive; unsupported identities must remain
        // visible until verification excludes them, rather than vanishing by rank.
        Assert.Equal(2, results.Count);
        var correct = Assert.Single(results, c => c.Map.Id == scene.Map.Id);
        scene.AssertPose(correct);
        correct.IdentityEvidence = scene.Verify(correct);
        var wrong = Assert.Single(results, c => c.Map.Id == wrongMap.Id);
        Assert.All(wrong.SearchHypotheses, hypothesis =>
            Assert.Equal(ScanIdentityState.Excluded, scene.Verify(hypothesis).State));
        wrong.IdentityEvidence = scene.Verify(wrong);
        Assert.All(results, c => Assert.Equal(SideEntranceGateAssociationKind.DetectedGate, c.GateAssociationKind));
        // A unique supported contour still awaits the separate alignment stage.
        Assert.Null(ScanIdentityVerifier.SelectIdentity(results, true, true));
    }
    [Fact]
    public void GateMaskClearsEveryGateWithViewportOffset()
    {
        using var frame = BuildTexture(260, 220, seed: 173);
        var viewport = new MapScreenRect(100d, 200d, frame.Width, frame.Height);
        var gates = new[]
        {
            new GateDetection
            {
                Score = 0.95d,
                ScreenBounds = new MapScreenRect(120d, 230d, 18d, 16d)
            },
            new GateDetection
            {
                Score = 0.93d,
                ScreenBounds = new MapScreenRect(280d, 330d, 22d, 20d)
            }
        };

        SideEntranceScanPipeline.MaskDetectedGates(frame, gates, viewport);

        using var first = new Mat(frame, new Rect(20, 30, 18, 16));
        using var second = new Mat(frame, new Rect(180, 130, 22, 20));
        Cv2.MeanStdDev(first, out var firstMean, out var firstStdDev);
        Cv2.MeanStdDev(second, out var secondMean, out var secondStdDev);
        Assert.InRange(firstStdDev.Val0, 0d, 0.001d);
        Assert.InRange(secondStdDev.Val0, 0d, 0.001d);
        Assert.Equal(0d, firstMean.Val0);
        Assert.Equal(0d, secondMean.Val0);
        Assert.Equal(firstMean.Val0, secondMean.Val0, 8);
    }

}
