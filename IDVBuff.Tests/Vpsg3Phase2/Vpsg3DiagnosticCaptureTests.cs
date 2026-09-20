using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3DiagnosticCaptureTests
{
    [Fact]
    public void InactiveDiagnosticMode_DoesNotWriteAnyFiles()
    {
        MapDiagnosticModeCapture.Clear();
        Assert.False(MapDiagnosticModeCapture.IsActive);

        using var edges = new Mat(50, 50, MatType.CV_8UC1, Scalar.Black);
        using var mask = new Mat(50, 50, MatType.CV_8UC1, Scalar.White);
        using var observation = new Vpsg3LiveObservation(edges, mask, 50, 50, 0, 2500, new(0, 0, 50, 50));
        var result = Vpsg3BootstrapResult.Fallback("test", default, default);

        Vpsg3DiagnosticCapture.CaptureIfActive(1, observation, "nonexistent.png", result, "vpsg3");

        // Verify root directory does not contain matches
        if (Directory.Exists(MapDiagnosticModeCapture.RootDirectory))
        {
            var matchDirs = Directory.GetDirectories(MapDiagnosticModeCapture.RootDirectory, "对局 *");
            Assert.Empty(matchDirs);
        }
    }

    [Fact]
    public void ActiveDiagnosticMode_ReusesObservedEdges_AndCreatesFittedOverlay()
    {
        MapDiagnosticModeCapture.Clear();
        try
        {
            MapDiagnosticModeCapture.BeginMatch();
            Assert.True(MapDiagnosticModeCapture.IsActive);

            var tempDir = Path.Combine(Path.GetTempPath(), "vpsg3-diag-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // Create a synthetic reference edge image: a horizontal line
                var refPath = Path.Combine(tempDir, "ref_edges.png");
                using (var refMat = new Mat(60, 60, MatType.CV_8UC1, Scalar.Black))
                {
                    Cv2.Line(refMat, new Point(10, 30), new Point(50, 30), Scalar.White, 1);
                    Cv2.ImWrite(refPath, refMat);
                }

                // Create live observation with exact same line
                using var liveEdges = new Mat(60, 60, MatType.CV_8UC1, Scalar.Black);
                Cv2.Line(liveEdges, new Point(10, 30), new Point(50, 30), Scalar.White, 1);
                using var liveMask = new Mat(60, 60, MatType.CV_8UC1, Scalar.White);
                using var observation = new Vpsg3LiveObservation(liveEdges, liveMask, 60, 60, 41, 3600, new(0, 0, 60, 60));

                using var dummyViewport = new Mat(60, 60, MatType.CV_8UC3, Scalar.Black);
                var attemptId = MapDiagnosticModeCapture.BeginMapOpen(dummyViewport);

                var scaleResult = new Vpsg3ScaleResult(Vpsg3ScaleStatus.Success, 1.0d, 3.5d, 0, string.Empty);
                var refinedCandidate = new Vpsg3RefinedCandidate(1.0d, 0d, 0d, 0.9d, 0.9d, 0.9d, default, 10);
                var result = new Vpsg3BootstrapResult(
                    isAccepted: true,
                    fallbackReason: string.Empty,
                    scale: 1.0d,
                    offsetX: 0d,
                    offsetY: 0d,
                    confidence: 0.92d,
                    apertureMargin: 0.15d,
                    hasDistinctRunnerUp: true,
                    passedPartitions: 4,
                    scaleResult: scaleResult,
                    bestCandidate: refinedCandidate,
                    runnerUpCandidate: null,
                    timing: default);

                Vpsg3DiagnosticCapture.CaptureIfActive(attemptId, observation, refPath, result, tag: "vpsg3");

                // Find the match directory
                var matchDir = Directory.GetDirectories(MapDiagnosticModeCapture.RootDirectory, "对局 *").Single();

                var structureFile = Path.Combine(matchDir, "结构配准", $"结构配准 {attemptId}_vpsg3.png");
                Assert.True(File.Exists(structureFile), $"Expected structure line file at {structureFile}");

                // Verify exact pixel reuse of observation.ObservedEdges
                using var readStructure = Cv2.ImRead(structureFile, ImreadModes.Grayscale);
                Assert.Equal(liveEdges.Size(), readStructure.Size());
                using var structureDiff = new Mat();
                Cv2.Absdiff(liveEdges, readStructure, structureDiff);
                Assert.Equal(0, Cv2.CountNonZero(structureDiff)); // Byte-for-byte identical

                // Verify fitted overlay
                var fitnessFile = Path.Combine(matchDir, "贴合度", $"贴合度 {attemptId}_vpsg3.png");
                Assert.True(File.Exists(fitnessFile), $"Expected fitness overlay file at {fitnessFile}");

                using var readFitness = Cv2.ImRead(fitnessFile, ImreadModes.Color);
                Assert.Equal(3, readFitness.Channels());
                Assert.Equal(60, readFitness.Width);
                Assert.Equal(60, readFitness.Height);

                // Check that the overlapping line (Point(30, 30)) is yellow: R > 150, G > 150, B < 50
                var sampleOverlapPixel = readFitness.At<Vec3b>(30, 30);
                Assert.True(sampleOverlapPixel.Item0 < 50, $"Blue should be low, got {sampleOverlapPixel.Item0}");
                Assert.True(sampleOverlapPixel.Item1 > 150, $"Green should be high, got {sampleOverlapPixel.Item1}");
                Assert.True(sampleOverlapPixel.Item2 > 150, $"Red should be high, got {sampleOverlapPixel.Item2}");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
        finally
        {
            MapDiagnosticModeCapture.Clear();
        }
    }

    [Fact]
    public void ActiveDiagnosticMode_ScaleFailed_OutputsLiveEdgesAndErrorBanner()
    {
        MapDiagnosticModeCapture.Clear();
        try
        {
            MapDiagnosticModeCapture.BeginMatch();
            using var liveEdges = new Mat(40, 40, MatType.CV_8UC1, Scalar.Black);
            using var liveMask = new Mat(40, 40, MatType.CV_8UC1, Scalar.White);
            using var observation = new Vpsg3LiveObservation(liveEdges, liveMask, 40, 40, 0, 1600, new(0, 0, 40, 40));

            var scaleResult = Vpsg3ScaleResult.Failed(Vpsg3ScaleStatus.DegenerateSignal, "DegenerateSignal");
            var result = Vpsg3BootstrapResult.Fallback("ScaleSolverFailed: DegenerateSignal", scaleResult, default);

            using var overlay = Vpsg3DiagnosticCapture.CreateFittedOverlay(observation, "nonexistent.png", result);
            Assert.NotNull(overlay);
            Assert.Equal(40, overlay.Width);
            Assert.Equal(40, overlay.Height);
            Assert.Equal(3, overlay.Channels());
        }
        finally
        {
            MapDiagnosticModeCapture.Clear();
        }
    }
}
