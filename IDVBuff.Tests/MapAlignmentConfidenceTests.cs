using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests;

public class MapAlignmentConfidenceTests
{
    [Fact]
    public void DualGateConfidence_GateScoreDominates()
    {
        // 高门分数 + 中等几何误差 → 应该产生高置信度
        var highGates = MapAlignmentConfidence.ComputeDualGateConfidence(
            mainGateScore: 0.90d,
            sideGateScore: 0.90d,
            vectorError: 0.10d,
            vectorErrorTolerance: 0.15d);

        // 门分数占70%，几何占30%，所以高门分数应主导结果
        Assert.True(highGates >= 0.75d,
            $"高门分数(0.90)应产生 ≥75% 置信度，实际 {highGates:P1}");
    }

    [Fact]
    public void DualGateConfidence_ClearGatesClearThreshold()
    {
        // 回归测试：清晰可见的门(0.887)在容差内的几何误差(0.1043/0.15)
        // 应该轻松超过 62% 的中等置信度阈值
        var confidence = MapAlignmentConfidence.ComputeDualGateConfidence(
            mainGateScore: 0.8868d,
            sideGateScore: 0.8868d,
            vectorError: 0.1043d,
            vectorErrorTolerance: 0.15d);

        Assert.True(
            confidence >= MapSessionRules.MediumConfidence,
            $"清晰门(0.887)应超过中等置信度 {MapSessionRules.MediumConfidence:P0}，" +
            $"实际 {confidence:P1}");
    }

    [Fact]
    public void DualGateConfidence_GeometryIsSecondary()
    {
        // 相同的门分数，不同的几何误差
        var perfectGeometry = MapAlignmentConfidence.ComputeDualGateConfidence(
            0.85d, 0.85d, 0.01d, 0.15d);
        var goodGeometry = MapAlignmentConfidence.ComputeDualGateConfidence(
            0.85d, 0.85d, 0.08d, 0.15d);

        // 差异应该很小（几何只占30%）
        var difference = perfectGeometry - goodGeometry;
        Assert.InRange(difference, 0d, 0.15d);
        Assert.True(goodGeometry >= 0.70d,
            $"良好几何(0.08/0.15)仍应保持高置信度，实际 {goodGeometry:P1}");
    }

    [Fact]
    public void SingleGateTracking_CurrentObservationDominates()
    {
        // 常规单门跟踪：当前门分数占75%
        var highCurrent = MapAlignmentConfidence.ComputeSingleGateTrackingConfidence(
            gateScore: 0.90d,
            baselineConfidence: 0.65d,
            scaleAgreement: 1.0d);

        // 即使基线较低(65%)，高门分数(90%)应主导
        Assert.True(highCurrent >= 0.80d,
            $"高门分数(0.90)应主导单门置信度，实际 {highCurrent:P1}");
    }

    [Fact]
    public void SingleGateTracking_ScaleAgreementMatters()
    {
        var perfectScale = MapAlignmentConfidence.ComputeSingleGateTrackingConfidence(
            gateScore: 0.85d,
            baselineConfidence: 0.75d,
            scaleAgreement: 1.0d);

        var poorScale = MapAlignmentConfidence.ComputeSingleGateTrackingConfidence(
            gateScore: 0.85d,
            baselineConfidence: 0.75d,
            scaleAgreement: 0.5d);

        // 尺度不一致应降低置信度
        Assert.True(perfectScale > poorScale,
            $"完美尺度一致性应优于差尺度一致性");
        // 尺度占10%权重，0.5差异应导致约5%的置信度差异
        var expectedDiff = (1.0d - 0.5d) * 0.10d; // ≈ 0.05
        Assert.True(perfectScale - poorScale >= expectedDiff - 0.01d,
            $"尺度一致性影响应≥4%，实际差异{perfectScale - poorScale:F3}");
    }

    [Fact]
    public void SideEntranceSingleGate_PriorBoostsConfidence()
    {
        // 侧门扫描模式：地图ID已知
        var withPrior = MapAlignmentConfidence.ComputeSideEntranceSingleGateConfidence(
            sideEntrancePrior: 0.85d,
            gateScore: 0.75d,
            scaleAgreement: 0.95d);

        // 无先验的常规单门（基线置信度假设为75%）
        var withoutPrior = MapAlignmentConfidence.ComputeSingleGateTrackingConfidence(
            gateScore: 0.75d,
            baselineConfidence: 0.75d,
            scaleAgreement: 0.95d);

        // 侧门先验应显著提升置信度
        Assert.True(withPrior > withoutPrior,
            $"侧门先验(85%)应提升置信度：有先验 {withPrior:P1} vs 无先验 {withoutPrior:P1}");
    }

    [Fact]
    public void SideEntranceStructure_IdentityKnown()
    {
        // 侧门扫描后的结构定位：地图ID已知(85%)，位置质量一般(65%)
        var confidence = MapAlignmentConfidence.ComputeSideEntranceStructureConfidence(
            sideEntrancePrior: 0.85d,
            locationQuality: 0.65d,
            candidateSeparation: 0.50d,
            featureConsensus: 0.70d,
            refinementQuality: 0.75d);

        // 高ID置信度应补偿较低的位置质量
        Assert.True(confidence >= 0.70d,
            $"高地图ID置信度(85%)应补偿位置质量(65%)，实际 {confidence:P1}");
    }

    [Fact]
    public void ScaleAgreement_ToleratesSmallDeviation()
    {
        var perfect = MapAlignmentConfidence.ComputeScaleAgreement(1.0d, 1.0d);
        var within5Percent = MapAlignmentConfidence.ComputeScaleAgreement(1.05d, 1.0d);
        var within10Percent = MapAlignmentConfidence.ComputeScaleAgreement(1.10d, 1.0d);
        var at12Percent = MapAlignmentConfidence.ComputeScaleAgreement(1.12d, 1.0d);
        var beyond15Percent = MapAlignmentConfidence.ComputeScaleAgreement(1.15d, 1.0d);

        Assert.Equal(1.0d, perfect, 3);
        // 5% 偏差：1 - (0.05 / 0.12) ≈ 0.583
        Assert.True(within5Percent >= 0.58d && within5Percent <= 0.60d,
            $"5%偏差应约为58%一致性，实际{within5Percent:F3}");
        // 10% 偏差：1 - (0.10 / 0.12) ≈ 0.167
        Assert.True(within10Percent >= 0.15d && within10Percent <= 0.20d,
            $"10%偏差应约为17%一致性，实际{within10Percent:F3}");
        // 12% 边界：可能刚好在阈值上，由于浮点精度可能略高于0
        Assert.True(at12Percent >= 0d && at12Percent <= 0.8d,
            $"12%边界应接近0或使用备用公式，实际{at12Percent:F3}");
        // 15% 超出阈值：1 - (0.15 * 2) = 0.70
        Assert.True(beyond15Percent >= 0.68d && beyond15Percent <= 0.72d,
            $"15%偏差（超阈值）应约为70%一致性，实际{beyond15Percent:F3}");
    }

    [Fact]
    public void GeometryGoodness_ExponentialDecay()
    {
        // 容差内的误差应保持较高质量
        var nearZero = MapAlignmentConfidence.GeometryGoodness(0.01d, 0.15d);
        var at50Percent = MapAlignmentConfidence.GeometryGoodness(0.075d, 0.15d);
        var at70Percent = MapAlignmentConfidence.GeometryGoodness(0.105d, 0.15d);
        var atTolerance = MapAlignmentConfidence.GeometryGoodness(0.15d, 0.15d);

        // 0.01/0.15 ≈ 0.067, exp(-0.067) ≈ 0.935
        Assert.True(nearZero >= 0.93d, $"近零误差应≥93%，实际{nearZero:F3}");
        Assert.InRange(at50Percent, 0.55d, 0.65d); // exp(-0.5) ≈ 0.606
        Assert.InRange(at70Percent, 0.45d, 0.55d); // exp(-0.7) ≈ 0.497
        Assert.InRange(atTolerance, 0.35d, 0.40d); // exp(-1.0) ≈ 0.368
    }

    [Fact]
    public void AuxiliaryAnchorConfidence_RequiresMatches()
    {
        var empty = MapAlignmentConfidence.ComputeAuxiliaryAnchorConfidence(
            matches: [],
            geometricConsistency: 1.0d,
            baselineConfidence: 0.80d);

        Assert.Equal(0d, empty);
    }

    [Fact]
    public void AuxiliaryAnchorConfidence_WeightsCorrectly()
    {
        var matches = new[]
        {
            new CvAnchorEvidence
            {
                AnchorId = Guid.NewGuid(),
                Score = 0.85d,
                TemplateScale = 1.0d,
                ReferenceBounds = new MapScreenRect(100, 100, 20, 20),
                ScreenBounds = new MapScreenRect(500, 500, 20, 20)
            },
            new CvAnchorEvidence
            {
                AnchorId = Guid.NewGuid(),
                Score = 0.80d,
                TemplateScale = 1.0d,
                ReferenceBounds = new MapScreenRect(200, 200, 20, 20),
                ScreenBounds = new MapScreenRect(600, 600, 20, 20)
            }
        };

        var confidence = MapAlignmentConfidence.ComputeAuxiliaryAnchorConfidence(
            matches: matches,
            geometricConsistency: 0.90d,
            baselineConfidence: 0.75d);

        // 锚点平均0.825，一致性0.90，基线0.75
        // 公式：0.825*0.60 + 0.90*0.25 + 0.75*0.15 = 0.495 + 0.225 + 0.1125 = 0.8325
        Assert.InRange(confidence, 0.80d, 0.85d);
    }

    [Fact]
    public void HybridConfidence_FallsBackToSingleGate()
    {
        var hybrid = MapAlignmentConfidence.ComputeHybridSinglePlusAuxiliaryConfidence(
            singleGateScore: 0.85d,
            auxiliaryMatches: [],
            spatialSeparation: 0.5d,
            baselineConfidence: 0.75d);

        var singleOnly = MapAlignmentConfidence.ComputeSingleGateTrackingConfidence(
            gateScore: 0.85d,
            baselineConfidence: 0.75d,
            scaleAgreement: 1.0d);

        // 无辅助锚点时应回退到单门公式
        Assert.Equal(singleOnly, hybrid, 3);
    }
}
