using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed partial class MapStructureRegistrarTests
{
    [Fact]
    public void TwoStageFineChamferThreshold_AcceptsWithinPhysicalRatioLimit_AndRejectsInOrdinaryMode()
    {
        var tuning = TestFastTuning();

        var candidate = new MapStructureCandidate
        {
            ChamferPixels = 3.05d,
            EdgeCoverage = 0.85d,
            OccupancyCoverage = 0.80d,
            ReferenceCoverage = 0.80d,
            ConsistentPartitions = 4,
            PriorAgreement = 0.80d,
            IsWithinValidBounds = true
        };

        var ordinaryRequest = new MapStructureRegistrationRequest
        {
            TwoStagePhysicalRatio = 1.0d
        };
        var rejected = MapStructureValidator.Validate(
            candidate,
            margin: 0.30d,
            requiredMargin: 0.10d,
            tuning: tuning,
            restrictedSearch: true,
            request: ordinaryRequest);
        Assert.Equal(MapStructureRejectionReason.WeakAbsoluteScore, rejected);

        var twoStageRequest = new MapStructureRegistrationRequest
        {
            TwoStagePhysicalRatio = 1.316d
        };
        var accepted = MapStructureValidator.Validate(
            candidate,
            margin: 0.30d,
            requiredMargin: 0.10d,
            tuning: tuning,
            restrictedSearch: true,
            request: twoStageRequest);
        Assert.Equal(MapStructureRejectionReason.None, accepted);
    }

    [Fact]
    public void LowGeometricLockConfidence_HasCorrectDispositionAndText()
    {
        var reason = MapStructureRejectionReason.LowGeometricLockConfidence;
        Assert.Equal("几何锁定置信度不足", reason.ToDisplayText());
        Assert.Equal(MapStructureEvidenceDisposition.Inconclusive, reason.ToDisposition());
    }

    [Fact]
    public void Register_TwoStage_EndToEnd_AcceptsWithPhysicalRatio()
    {
        var preprocessor = new MapStructurePreprocessor();
        var registrar = new MapStructureRegistrar(preprocessor);
        using var reference = BuildReference();

        var crop = new Rect(60, 40, 240, 180);
        using var originalLive = new Mat(reference, crop).Clone();

        var compSize = new Size(180, 135);
        using var compLive = new Mat();
        Cv2.Resize(originalLive, compLive, compSize, 0d, 0d, InterpolationFlags.Area);

        var ratio = originalLive.Width / (double)compLive.Width;

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = false;
        tuning.FastAlignmentShadowMode = false;

        var request = new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = compLive,
            OriginalLiveRoi = originalLive,
            PhysicalPixelsPerLivePixel = ratio,
            ViewportBounds = new MapScreenRect(0d, 0d, compLive.Width, compLive.Height),
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        };

        var result = registrar.Register(request);
        Assert.True(result.Accepted, $"两阶段配准应当成功，但实际拒绝：{result.RejectionReason} - {result.FailureReason}");
        Assert.NotNull(result.Transform);
        Assert.NotEmpty(result.Candidates);
        Assert.True(result.Candidates[0].SpaceRatio > 1.0d, "候选应当记录物理空间尺度比率");
    }

    [Fact]
    public void CandidateWithSpaceRatio_PassesValidateAbsolute_WithoutRequest()
    {
        var tuning = TestFastTuning();

        var candidateWithRatio = new MapStructureCandidate
        {
            ChamferPixels = 3.50d,
            EdgeCoverage = 0.85d,
            OccupancyCoverage = 0.80d,
            ReferenceCoverage = 0.80d,
            ConsistentPartitions = 4,
            PriorAgreement = 0.80d,
            IsWithinValidBounds = true,
            SpaceRatio = 1.316d
        };

        // 没传 request，但 candidate 自身带 SpaceRatio = 1.316d，门限应放宽至 3.948px
        var outcome = MapStructureValidator.ValidateAbsolute(candidateWithRatio, tuning);
        Assert.Equal(MapStructureRejectionReason.None, outcome);

        // 普通 candidate（SpaceRatio = 1.0），3.50px 应当被拒
        var normalCandidate = candidateWithRatio with { SpaceRatio = 1.0d };
        var normalOutcome = MapStructureValidator.ValidateAbsolute(normalCandidate, tuning);
        Assert.Equal(MapStructureRejectionReason.WeakAbsoluteScore, normalOutcome);
    }
}
