namespace IDVBuff.Features.Maps;

/// <summary>
/// 统一的地图对齐置信度计算。每种对齐场景都有独立的置信度公式，
/// 避免跨场景复用导致的语义混淆和权重耦合。
/// </summary>
public static class MapAlignmentConfidence
{
    // ═══════════════════════════════════════════════════════════════
    // 模式1: 常规双门扫描
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 双门几何对齐：通过两个门的位置识别地图并计算变换。
    /// 门模板分数是主要证据，几何验证是二次确认。
    /// </summary>
    public static double ComputeDualGateConfidence(
        double mainGateScore,
        double sideGateScore,
        double vectorError,
        double vectorErrorTolerance)
    {
        // 门分数占主导（70%），几何验证占辅助（30%）
        var gateConfidence = (mainGateScore + sideGateScore) / 2d;
        var geometryGoodness = GeometryGoodness(vectorError, vectorErrorTolerance);
        return Math.Clamp(
            (gateConfidence * 0.70d) + (geometryGoodness * 0.30d),
            0d,
            1d);
    }

    /// <summary>
    /// 单门跟踪：已有双门几何锁定，用单门更新平移。
    /// 基于已验证的双门缩放锁定，单门只需更新平移。
    /// </summary>
    public static double ComputeSingleGateTrackingConfidence(
        double gateScore,
        double baselineConfidence,
        double scaleAgreement)
    {
        // 当前门分数占75%，基线置信度占15%，尺度一致性占10%
        var currentWeight = 0.75d;
        var baselineWeight = 0.15d;
        var scaleWeight = 0.10d;

        return Math.Clamp(
            (gateScore * currentWeight)
            + (baselineConfidence * baselineWeight)
            + (scaleAgreement * scaleWeight),
            0d,
            1d);
    }

    /// <summary>
    /// 纯结构配准（冷启动）：同时识别地图ID和定位视口。
    /// 这是最困难的场景，需要同时解决两个不确定性。
    /// </summary>
    public static double ComputeStructureColdConfidence(
        double structureQuality,
        double candidateSeparation,
        double featureConsensus,
        double refinementQuality,
        double boundsAndPrior)
    {
        // 结构质量30%，候选分离度25%，特征一致性20%，精修质量15%，边界和先验10%
        var total = 0d;
        var weight = 0d;

        total += structureQuality * 0.30d;
        weight += 0.30d;

        total += candidateSeparation * 0.25d;
        weight += 0.25d;

        if (featureConsensus >= 0d)
        {
            total += featureConsensus * 0.20d;
            weight += 0.20d;
        }

        if (refinementQuality >= 0d)
        {
            total += refinementQuality * 0.15d;
            weight += 0.15d;
        }

        total += boundsAndPrior * 0.10d;
        weight += 0.10d;

        return weight > 0d ? Math.Clamp(total / weight, 0d, 1d) : 0d;
    }

    // ═══════════════════════════════════════════════════════════════
    // 模式2: 侧门扫描先验
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 侧门扫描后的单门验证：地图ID已知，用单门定位并验证。
    /// 侧门扫描已解决"这是哪张地图"，单门只需解决"视口在哪里"。
    /// </summary>
    public static double ComputeSideEntranceSingleGateConfidence(
        double sideEntrancePrior,
        double gateScore,
        double scaleAgreement)
    {
        // 地图ID置信度40%，当前门分数50%，尺度一致性10%
        return Math.Clamp(
            (sideEntrancePrior * 0.40d)
            + (gateScore * 0.50d)
            + (scaleAgreement * 0.10d),
            0d,
            1d);
    }

    /// <summary>
    /// 侧门扫描后的结构定位：地图ID已知，纯定位视口。
    /// 侧门扫描已解决"这是哪张地图"，结构配准只需解决"视口在哪里"。
    /// </summary>
    public static double ComputeSideEntranceStructureConfidence(
        double sideEntrancePrior,
        double locationQuality,
        double candidateSeparation,
        double featureConsensus,
        double refinementQuality)
    {
        // 地图ID置信度35%，位置质量30%，候选分离度15%，特征一致性10%，精修质量10%
        var total = sideEntrancePrior * 0.35d;
        var weight = 0.35d;

        total += locationQuality * 0.30d;
        weight += 0.30d;

        total += candidateSeparation * 0.15d;
        weight += 0.15d;

        if (featureConsensus >= 0d)
        {
            total += featureConsensus * 0.10d;
            weight += 0.10d;
        }

        if (refinementQuality >= 0d)
        {
            total += refinementQuality * 0.10d;
            weight += 0.10d;
        }

        return weight > 0d ? Math.Clamp(total / weight, 0d, 1d) : 0d;
    }

    // ═══════════════════════════════════════════════════════════════
    // 辅助场景
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 辅助锚点追踪：已知地图和缩放，用多个辅助锚点更新平移。
    /// </summary>
    public static double ComputeAuxiliaryAnchorConfidence(
        IReadOnlyList<CvAnchorEvidence> matches,
        double geometricConsistency,
        double baselineConfidence)
    {
        if (matches.Count == 0)
            return 0d;

        // 锚点分数平均60%，几何一致性25%，基线置信度15%
        var averageScore = matches.Average(m => m.Score);
        return Math.Clamp(
            (averageScore * 0.60d)
            + (geometricConsistency * 0.25d)
            + (baselineConfidence * 0.15d),
            0d,
            1d);
    }

    /// <summary>
    /// 单门+辅助组合：单门提供主锚点，辅助锚点提供验证。
    /// </summary>
    public static double ComputeHybridSinglePlusAuxiliaryConfidence(
        double singleGateScore,
        IReadOnlyList<CvAnchorEvidence> auxiliaryMatches,
        double spatialSeparation,
        double baselineConfidence)
    {
        if (auxiliaryMatches.Count == 0)
            return ComputeSingleGateTrackingConfidence(
                singleGateScore,
                baselineConfidence,
                scaleAgreement: 1d);

        // 单门分数40%，辅助平均分30%，空间分离度20%，基线置信度10%
        var auxiliaryAverage = auxiliaryMatches.Average(m => m.Score);
        return Math.Clamp(
            (singleGateScore * 0.40d)
            + (auxiliaryAverage * 0.30d)
            + (spatialSeparation * 0.20d)
            + (baselineConfidence * 0.10d),
            0d,
            1d);
    }

    // ═══════════════════════════════════════════════════════════════
    // 辅助函数
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 几何拟合质量：使用指数衰减曲线，容差内的误差保留较高权重。
    /// </summary>
    public static double GeometryGoodness(
        double vectorError,
        double vectorErrorTolerance)
    {
        if (!double.IsFinite(vectorErrorTolerance) || vectorErrorTolerance <= 0d)
            return 0d;

        const double decayRate = 1.0d;
        return Math.Exp(-(decayRate * (vectorError / vectorErrorTolerance)));
    }

    /// <summary>
    /// 计算尺度一致性：实际尺度与预期尺度的一致程度。
    /// </summary>
    public static double ComputeScaleAgreement(
        double actualScale,
        double expectedScale)
    {
        if (!double.IsFinite(actualScale)
            || !double.IsFinite(expectedScale)
            || actualScale <= 0d
            || expectedScale <= 0d)
        {
            return 0d;
        }

        var ratio = actualScale / expectedScale;
        var deviation = Math.Abs(ratio - 1d);

        // 在12%以内线性衰减，超过12%急剧下降
        const double threshold = 0.12d;
        if (deviation <= threshold)
            return 1d - (deviation / threshold);
        else
            return Math.Max(0d, 1d - (deviation * 2d));
    }

    /// <summary>
    /// 计算几何一致性：多个锚点之间的残差分布是否一致。
    /// </summary>
    public static double ComputeGeometricConsistency(
        IReadOnlyList<CvAnchorEvidence> matches,
        MapOverlayTransform transform)
    {
        if (matches.Count < 2 || transform is null)
            return 1d;

        var residuals = matches
            .Select(m =>
            {
                var predictedX = (m.ReferenceBounds.CenterX * transform.ScaleX)
                    + transform.OffsetX;
                var predictedY = (m.ReferenceBounds.CenterY * transform.ScaleY)
                    + transform.OffsetY;
                var dx = predictedX - m.ScreenBounds.CenterX;
                var dy = predictedY - m.ScreenBounds.CenterY;
                return Math.Sqrt((dx * dx) + (dy * dy));
            })
            .ToArray();

        var maxResidual = residuals.Max();
        if (maxResidual <= 0d)
            return 1d;

        var variance = residuals.Average(r => Math.Pow(r / maxResidual, 2));
        return Math.Clamp(1d - variance, 0d, 1d);
    }

    /// <summary>
    /// 计算空间分离度：单门与辅助锚点的距离占对角线的比例。
    /// </summary>
    public static double ComputeSpatialSeparation(
        MapScreenRect gateScreen,
        MapScreenRect auxiliaryScreen,
        int referenceWidth,
        int referenceHeight)
    {
        var diagonal = Math.Sqrt(
            (referenceWidth * referenceWidth)
            + (referenceHeight * referenceHeight));

        if (diagonal <= 0d)
            return 0d;

        var dx = gateScreen.CenterX - auxiliaryScreen.CenterX;
        var dy = gateScreen.CenterY - auxiliaryScreen.CenterY;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));

        var ratio = distance / diagonal;
        return Math.Clamp(ratio * 10d, 0d, 1d);
    }
}
/*
 * 文件职责：MapAlignmentConfidence。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
