using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class MapScanDiagnosticCaptureTests
{
    [Fact]
    public void FailedScanPreservesExactInputAndSeedsEvenWhenAlignmentDumpsAreSuppressed()
    {
        using var frame = new CapturedGameFrame(new Mat(100, 160, MatType.CV_8UC3, new Scalar(30, 25, 22)),
            new(0, 0, 2560, 1600), new(400, 200, 160, 100), IntPtr.Zero);
        using var evidence = new ScanFrameEvidence(frame.Image, frame.ViewportBounds, [],
            ScanExecutionPolicy.For(ScanPerformanceMode.Fast));
        var candidate = new SideEntranceScanCandidate
        {
            Map = new MapRecord { Id = Guid.NewGuid(), SequenceNumber = 14 },
            FloorKey = "1f", MatchScore = .94, MatchScale = 1.25,
            MatchLocation = new(10, -20, 200, 125),
            IdentityEvidence = ScanIdentityEvidence.Unverified("verification-incomplete")
        };
        MapDiagnosticModeCapture.BeginMatch();
        try
        {
            using var suppressed = MapDiagnosticModeCapture.Suppress();
            var path = MapDiagnosticModeCapture.WriteUnresolvedScan(frame, evidence, [candidate], ScanPerformanceMode.Fast);
            Assert.NotNull(path);
            using var saved = Cv2.ImRead(Path.Combine(path, "viewport.png"));
            Assert.Equal(0d, Cv2.Norm(frame.Image, saved));
            Assert.True(File.Exists(Path.Combine(path, "observed-edges.png")));
            Assert.True(File.Exists(Path.Combine(path, "valid-mask.png")));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "scan.json")));
            var first = json.RootElement.GetProperty("candidates")[0];
            Assert.Equal(candidate.Map.Id, first.GetProperty("Id").GetGuid());
            Assert.Equal(1, first.GetProperty("retrievalRank").GetInt32());
            Assert.Equal(1.25, first.GetProperty("hypotheses")[0].GetProperty("MatchScale").GetDouble());
            Assert.Equal(400, json.RootElement.GetProperty("ViewportBounds").GetProperty("X").GetDouble());
            MapDiagnosticModeCapture.EndMatch();
            Assert.Null(MapDiagnosticModeCapture.WriteUnresolvedScan(frame, evidence, [candidate], ScanPerformanceMode.Fast));
        }
        finally { MapDiagnosticModeCapture.EndMatch(); }
    }
}
