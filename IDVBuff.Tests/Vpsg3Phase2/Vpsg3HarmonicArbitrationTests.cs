using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

/// <summary>
/// Regression coverage for the VPSG3 scale-harmonic disambiguation work:
///  1. The S-B scale stage must *propose* harmonic families (≈2× / ≈0.5× / ≈0.75×) instead of
///     collapsing to a single dominant pitch, so the 2D registration can arbitrate.
///  2. The clean single-peak case must stay a single hypothesis (fast path).
///  3. A genuine ≈2× / ≈0.5× map scale must still be accepted — the fix must not bias toward ≈1.0.
/// </summary>
public class Vpsg3HarmonicArbitrationTests
{
    private readonly ITestOutputHelper _output;
    public Vpsg3HarmonicArbitrationTests(ITestOutputHelper output) => _output = output;

    private static Vpsg3IndexCacheKey MakeKey(string name) =>
        new(Guid.NewGuid(), name, "test", DateTimeOffset.UnixEpoch, "test");

    private static Vpsg3PitchCandidate Cand(double pitch, double corr, double ratio = 8.0d) =>
        new(pitch, corr, ratio);

    // ---- Test A: 2× harmonic family is proposed (dominant 2P + true P both surfaced) ----

    [Fact]
    public void TestA_TwoXHarmonic_ProposesBothScales()
    {
        // Dominant autocorrelation peak sits at 2P (wrong octave), a genuine secondary at P.
        // The scale stage must surface both so 2D registration can reject 2× and accept 1×.
        var target = new Vpsg3ScaleHypothesis[3];
        var count = Vpsg3ScaleSolver.BuildHypotheses(
            [Cand(88.0d, 0.86d), Cand(44.0d, 0.80d)],
            candidateCount: 2,
            refPitch: 44.0d,
            referencePeakRatio: 9.0d,
            target);

        Assert.Equal(2, count);
        Assert.Equal(Vpsg3HarmonicRelation.Primary, target[0].Relation);
        Assert.Equal(2.0d, target[0].SeedScale, 3);   // dominant 2P / P_ref
        Assert.Equal(Vpsg3HarmonicRelation.SubOctaveBelow, target[1].Relation);
        Assert.Equal(1.0d, target[1].SeedScale, 3);   // true P / P_ref — the one registration should pick
    }

    // ---- Test B: 0.5× harmonic family is proposed ----

    [Fact]
    public void TestB_HalfHarmonic_ProposesBothScales()
    {
        var target = new Vpsg3ScaleHypothesis[3];
        var count = Vpsg3ScaleSolver.BuildHypotheses(
            [Cand(44.0d, 0.86d), Cand(22.0d, 0.82d)],
            candidateCount: 2,
            refPitch: 44.0d,
            referencePeakRatio: 9.0d,
            target);

        Assert.Equal(2, count);
        Assert.Equal(1.0d, target[0].SeedScale, 3);
        Assert.Equal(Vpsg3HarmonicRelation.SubOctaveBelow, target[1].Relation);
        Assert.Equal(0.5d, target[1].SeedScale, 3);
    }

    // ---- Test C: clean single peak stays a single hypothesis (fast path) ----

    [Fact]
    public void TestC_SinglePeak_SingleHypothesisOnly()
    {
        var target = new Vpsg3ScaleHypothesis[3];
        var count = Vpsg3ScaleSolver.BuildHypotheses(
            [Cand(44.0d, 0.90d)],
            candidateCount: 1,
            refPitch: 44.0d,
            referencePeakRatio: 9.0d,
            target);

        Assert.Equal(1, count);
        Assert.Equal(Vpsg3HarmonicRelation.Primary, target[0].Relation);
        Assert.Equal(1.0d, target[0].SeedScale, 3);
    }

    // ---- Test D: a true ≈2× scale is proposed with 2× first, never biased toward 1.0 ----

    [Fact]
    public void TestD_TrueScaleTwo_DominantTwoXFirst()
    {
        // True fundamental is 2P (map is genuinely twice the reference period) with a weaker P lobe.
        var target = new Vpsg3ScaleHypothesis[3];
        var count = Vpsg3ScaleSolver.BuildHypotheses(
            [Cand(88.0d, 0.90d), Cand(44.0d, 0.45d)],
            candidateCount: 2,
            refPitch: 44.0d,
            referencePeakRatio: 9.0d,
            target);

        Assert.True(count >= 1);
        Assert.Equal(2.0d, target[0].SeedScale, 3);
    }

    // ---- Real-world fractional harmonics: ≈0.75× (the dominant replay failure signature) ----

    [Fact]
    public void TestE_ThreeQuarterHarmonic_ProposesTrueScale()
    {
        // Replay data shows failing candidates at ≈0.75 of the true ≈1.0 scale (ratio 4/3).
        var target = new Vpsg3ScaleHypothesis[3];
        var count = Vpsg3ScaleSolver.BuildHypotheses(
            [Cand(33.0d, 0.86d), Cand(44.0d, 0.83d)],
            candidateCount: 2,
            refPitch: 44.0d,
            referencePeakRatio: 9.0d,
            target);

        Assert.Equal(2, count);
        Assert.Equal(0.75d, target[0].SeedScale, 3);          // wrong primary (0.75×)
        Assert.Equal(Vpsg3HarmonicRelation.FractionalHarmonic, target[1].Relation);
        Assert.Equal(1.0d, target[1].SeedScale, 3);           // true scale still proposed for arbitration
    }

    // ---- Weak unrelated noise peak is never promoted ----

    [Fact]
    public void TestF_WeakUnrelatedPeak_NotPromoted()
    {
        var target = new Vpsg3ScaleHypothesis[3];
        var count = Vpsg3ScaleSolver.BuildHypotheses(
            [Cand(44.0d, 0.90d), Cand(37.0d, 0.30d)],   // 37/44 non-harmonic, corr far below primary
            candidateCount: 2,
            refPitch: 44.0d,
            referencePeakRatio: 9.0d,
            target);

        Assert.Equal(1, count);
    }

    // ---- Full-pipeline: a true ≈2× map scale is still accepted (no near-1 bias) ----

    [Fact]
    public void TestG_TrueScaleTwo_AcceptedEndToEnd()
    {
        var (refColor, refLine) = Vpsg3Phase0DatasetGenerator.BuildSyntheticReference(800, 600);
        using (refColor)
        using (refLine)
        {
            RunTrueScaleAccepts(refColor, refLine, TargetScale: 2.0d, toleranceScaleErr: 0.05d);
        }
    }

    // ---- Full-pipeline: a true ≈0.5× map scale is still accepted (no near-1 bias) ----

    [Fact]
    public void TestH_TrueScaleHalf_AcceptedEndToEnd()
    {
        // The synthetic room layout has too few structural features at 0.5× (its extractor legitimately
        // reports zero semantic edges). The real map reference retains rich room/corridor semantics at
        // 0.5×, so it is used here to prove a genuine ≈0.5 map scale is accepted (not biased to ≈1).
        if (!TryLoadRealReference(out var refColor, out var refLine))
        {
            _output.WriteLine("Real map reference unavailable; skipping 0.5× end-to-end acceptance.");
            return;
        }

        using (refColor)
        using (refLine)
        {
            RunTrueScaleAccepts(refColor, refLine, TargetScale: 0.5d, toleranceScaleErr: 0.07d);
        }
    }

    private static bool TryLoadRealReference(out Mat refColor, out Mat refLine)
    {
        var mapsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVB", "Maps");
        var dir = Path.Combine(mapsRoot, "00d73bbbc9a442d2aabd4750c7944f74");
        if (!Directory.Exists(dir)) { refColor = null!; refLine = null!; return false; }

        var colorPath = Path.Combine(dir, "floor-1-recognition.png");
        if (!File.Exists(colorPath)) colorPath = Path.Combine(dir, "floor-1.png");
        var linePath = Path.Combine(dir, "prebuilt-1f.png");
        if (!File.Exists(colorPath) || !File.Exists(linePath)) { refColor = null!; refLine = null!; return false; }

        refColor = Cv2.ImRead(colorPath, ImreadModes.Color);
        refLine = Cv2.ImRead(linePath, ImreadModes.Grayscale);
        return !refColor.Empty() && !refLine.Empty();
    }

    private void RunTrueScaleAccepts(Mat refColor, Mat refLine, double TargetScale, double toleranceScaleErr)
    {
        using var live = BuildLiveAtScale(refColor, refLine, TargetScale, out var cropRect);
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(refLine, MakeKey($"syn_s{TargetScale:F2}"));

        var viewport = new MapScreenRect(240d, 160d, cropRect.Width, cropRect.Height);
        using var obs = Vpsg3FastLiveExtractor.Extract(live, viewport);
        var result = Vpsg3FastBootstrapSolver.TrySolve(obs, floor);

        _output.WriteLine(
            $"scale={TargetScale:F2}: accepted={result.IsAccepted} solved={result.Scale:F4} " +
            $"reason={result.FallbackReason} margin={result.ApertureMargin:F3} conf={result.Confidence:F3}");

        Assert.True(result.IsAccepted, $"True scale {TargetScale:F2} should be accepted; got {result.FallbackReason}");
        Assert.InRange(result.Scale, TargetScale - toleranceScaleErr, TargetScale + toleranceScaleErr);
    }

    // ---- Phase B: a previously accepted scale reused as a seed skips the S-B stage entirely ----

    [Fact]
    public void TestI_WarmAcceptedSeed_SkipsScaleStage()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var sample = dataset.First(s => s.SourceType == "Synthetic" && Math.Abs(s.TrueScale - 1.0d) < 1e-4 && s.FogFraction == 0.0d);
            using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
            using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, MakeKey(sample.ReferenceName));

            var first = Vpsg3FastBootstrapSolver.TrySolve(obs, floor);
            Assert.True(first.IsAccepted, $"First solve should accept; got {first.FallbackReason}");
            Assert.True(first.Timing.ScaleMs > 0d, "First solve must actually run the S-B scale stage.");

            // Second map-open for the same floor reuses the accepted scale as a known seed.
            var second = Vpsg3FastBootstrapSolver.TrySolve(obs, floor, knownScaleSeed: first.Scale);
            Assert.True(second.IsAccepted, $"Seed-reuse solve should accept; got {second.FallbackReason}");
            Assert.Equal(0d, second.Timing.ScaleMs);          // S-B scale solver skipped
            Assert.InRange(second.Scale, first.Scale - 0.05d, first.Scale + 0.05d);
        }
        finally
        {
            foreach (var d in dataset) d.Dispose();
        }
    }

    /// <summary>
    /// Mirrors Vpsg3Phase0DatasetGenerator.CreateSample geometry for an arbitrary scale: resize the
    /// reference to the target scale and crop a centered-ish viewport (no fog / no HUD noise).
    /// </summary>
    private static Mat BuildLiveAtScale(Mat refColor, Mat refLine, double scale, out Rect cropRect)
    {
        var scaledW = (int)Math.Round(refColor.Width * scale);
        var scaledH = (int)Math.Round(refColor.Height * scale);

        using var scaledColor = new Mat();
        Cv2.Resize(refColor, scaledColor, new Size(scaledW, scaledH), interpolation: InterpolationFlags.Linear);

        var cropW = Math.Min(scaledW - 10, (int)Math.Round(refColor.Width * 0.65 * scale));
        var cropH = Math.Min(scaledH - 10, (int)Math.Round(refColor.Height * 0.65 * scale));
        var cropX = Math.Clamp((int)Math.Round(0.30d * (scaledW - cropW)), 0, scaledW - cropW);
        var cropY = Math.Clamp((int)Math.Round(0.30d * (scaledH - cropH)), 0, scaledH - cropH);
        cropRect = new Rect(cropX, cropY, cropW, cropH);

        return new Mat(scaledColor, cropRect).Clone();
    }
}
