using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class LatestBoundsReplayTests
{
    [Fact]
    public void LatestMap13FrameIsNotRejectedAsOutsideValidBounds()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sample = Path.Combine(local, "IDVB", "AlignmentResearch", "sessions",
            "2026-09-04_112016--18cba4cc", "87a70547", "1f", "019-rejected", "viewport.png");
        var mapDirectory = Path.Combine(local, "IDVB", "Maps", "87a705471e7940c2a9fd98df11564f61");
        var referencePath = Path.Combine(mapDirectory, "prebuilt-1f.png");
        if (!File.Exists(sample) || !File.Exists(referencePath))
            return;

        using var live = Cv2.ImRead(sample, ImreadModes.Unchanged);
        using var referenceImage = Cv2.ImRead(referencePath, ImreadModes.Grayscale);
        Assert.False(live.Empty());
        Assert.False(referenceImage.Empty());

        using var observed = IdvaNativeObservedExtractor.Process(live);
        using var computationEdges = new Mat();
        using var computationMask = new Mat();
        Cv2.Resize(observed.ObservedEdges, computationEdges, new Size(1003, 788), interpolation: InterpolationFlags.Nearest);
        Cv2.Resize(observed.ValidMask, computationMask, new Size(1003, 788), interpolation: InterpolationFlags.Nearest);
        using var computation = MapStructurePreprocessor.UseNativeObservedStructureLine(computationEdges, computationMask);
        using var original = MapStructurePreprocessor.UseNativeObservedStructureLine(observed.ObservedEdges, observed.ValidMask);
        using var reference = MapStructurePreprocessor.UsePrebuiltStructureLine(referenceImage);
        var tuning = new MapStructureRegistrationTuning { UsePrebuiltStructureLine = true };
        tuning.Normalize();
        var legacy1FProfile = new FloorRecognitionProfile
        {
            RecognitionPixelWidth = 1064,
            RecognitionPixelHeight = 1199,
            ValidMapBounds = new MapReferenceBounds
            {
                X = 0,
                Y = 0,
                Width = 532,
                Height = 600
            }
        };
        var effectiveBounds = legacy1FProfile.GetEffectiveValidMapBounds(referenceImage.Width, referenceImage.Height);
        Assert.Equal(1064, effectiveBounds.Width);
        Assert.Equal(1199, effectiveBounds.Height);

        var result = new MapStructureRegistrar(new MapStructurePreprocessor()).Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = referenceImage,
                LiveRoi = computationEdges,
                OriginalLiveRoi = live,
                PhysicalPixelsPerLivePixel = 1320d / 1003d,
                ViewportBounds = new MapScreenRect(0, 0, 1320, 1037),
                LockedTransform = new MapOverlayTransform
                {
                    AlignmentMode = MapOverlayAlignmentMode.Uniform,
                    ScaleX = 1.1985609440316205,
                    ScaleY = 1.1985609440316205
                },
                Tuning = tuning,
                ScaleSearchPolicy = MapScaleSearchPolicy.Fixed,
                PreparedReference = reference,
                PreparedLive = computation,
                PreparedOriginalLive = original,
                ValidMapBounds = effectiveBounds
            });

        Console.WriteLine($"accepted={result.Accepted}; rejection={result.RejectionReason}; confidence={result.Confidence:F6}; scale={result.Transform?.ScaleX:F6}; candidates={result.Candidates.Count}");
        Assert.True(result.Candidates.Count > 0);
        Assert.NotEqual(MapStructureRejectionReason.OutsideValidBounds, result.RejectionReason);
    }
}
