using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

// Opt-in external-data replay. Native level IDs are fixture IDs, never production MapIds.
public sealed class NativeMapObservationReplayTests
{
    public sealed class NativeMapsFactAttribute : FactAttribute
    {
        public NativeMapsFactAttribute()
        {
            if (!Directory.Exists(Environment.GetEnvironmentVariable("IDVB_NATIVE_MAPS_ROOT")))
                Skip = "Set IDVB_NATIVE_MAPS_ROOT to the native map export for this replay.";
        }
    }

    [NativeMapsFact]
    public void IdenticalNativeFloorsStayUnresolvedAcrossProgressiveMasks()
    {
        var root = Environment.GetEnvironmentVariable("IDVB_NATIVE_MAPS_ROOT")!;
        var rows = new List<object>();
        var supportedViews = 0;
        foreach (var (difficulty, firstId, secondId) in new[]
        {
            ("简单", "101003", "101005"), ("普通", "102002", "102005"), ("困难", "103007", "103024")
        })
        {
            Mat Read(string id)
            {
                var path = Path.Combine(root, difficulty, "楼层原图", id, $"level_{id}_floor_2_60px.png");
                using var source = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
                var image = new Mat();
                Cv2.Resize(source, image, default, .4, .4, InterpolationFlags.Area);
                return image;
            }
            using var first = Read(firstId);
            using var second = Read(secondId);
            Assert.Equal(first.Size(), second.Size());
            Assert.Equal(0, Cv2.Norm(first, second, NormTypes.INF));
            var bounds = new MapScreenRect(0, 0, first.Width, first.Height);
            var policy = ScanExecutionPolicy.For(ScanPerformanceMode.Balanced);
            using var full = new ScanFrameEvidence(first, bounds, [], policy);
            using var firstReference = full.Observation.ObservedEdges.Clone();
            using var secondReference = firstReference.Clone();
            // Known pose isolates visibility / identity policy; it is not retrieval or runtime acceptance.
            var transform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1 };
            foreach (var radius in new[] { 80, 160, 240, 2000 })
            {
                using var visible = new Mat(first.Size(), first.Type(), Scalar.Black);
                using var mask = new Mat(first.Size(), MatType.CV_8UC1, Scalar.Black);
                Cv2.Circle(mask, new Point(first.Width / 2, first.Height / 2), radius, Scalar.White, -1);
                first.CopyTo(visible, mask);
                using var observation = new ScanFrameEvidence(visible, bounds, [], policy);
                SideEntranceScanCandidate Candidate(Mat reference) => new()
                {
                    Map = new MapRecord { Id = Guid.NewGuid() }, FloorKey = "2F",
                    VerifiedTransform = transform, Disposition = SideEntranceCandidateDisposition.Reliable,
                    IdentityEvidence = ScanIdentityVerifier.Verify(observation,
                        ScanStructureIndex.Get(reference), transform, bounds, null)
                };
                var a = Candidate(firstReference);
                var b = Candidate(secondReference);
                Assert.Equal(a.IdentityEvidence.State, b.IdentityEvidence.State);
                for (var repeat = 0; repeat < 10; repeat++)
                    Assert.Null(ScanIdentityVerifier.SelectIdentity([a, b], true, true));
                if (a.IdentityEvidence.State == ScanIdentityState.Supported)
                {
                    supportedViews++;
                    Assert.Same(a, ScanObservationRules.SelectPreview([a, b], a.Map.Id, a.FloorKey));
                }
                rows.Add(new { firstId, secondId, floor = "2", radius,
                    points = observation.DensePoints.Length, evidence = a.IdentityEvidence, confirmed = false,
                    fullMapPresence = MapViewportPresenceDetector.Evaluate(visible).IsPresent,
                    partialReadiness = ScanObservationRules.HasVisibleStructure(visible) });
            }
        }
        Assert.True(supportedViews >= 3, "At least the three full-floor observations must be supported.");
        if (Environment.GetEnvironmentVariable("IDVB_OBSERVATION_RENDER_OUTPUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "native-variant-replay.json"),
                JsonSerializer.Serialize(new { supportedViews, rows }, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                }));
        }
    }
}
