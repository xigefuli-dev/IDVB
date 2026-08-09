namespace IDVBuff.Features.Maps;

/// <summary>Pure geometry ranking used by the runtime recognizer and deterministic tests.</summary>
public static class MapCvRecognitionScript
{
    /// <summary>向量误差容忍比例（相对于门对距离归一化）。</summary>
    public static double VectorErrorTolerance =>
        RecognitionConfigRules.VectorErrorTolerance;
    /// <summary>排名第一与第二之间需要的最小差距，否则视为模糊。</summary>
    public static double AmbiguityMargin =>
        RecognitionConfigRules.AmbiguityMargin;
    /// <summary>复核阶段的最佳候选优势阈值。</summary>
    public static double ConfirmationMargin =>
        RecognitionConfigRules.ConfirmationMargin;

    // ── Dual-gate confidence weighting ─────────────────────────────────
    // Gate template score is the primary evidence: clearly visible gates
    // are the strongest signal that the map is open and identified.
    // Geometry acts as a soft secondary check rather than the dominant term.
    [Obsolete("Use MapAlignmentConfidence.ComputeDualGateConfidence instead")]
    public static double GateScoreConfidenceWeight => 0.50d;
    [Obsolete("Use MapAlignmentConfidence.ComputeDualGateConfidence instead")]
    public static double GeometryConfidenceWeight => 0.50d;
    /// <summary>Soft-decay rate for the geometry goodness curve exp(−k·v/t).</summary>
    public static double GeometryGoodnessDecayRate =>
        RecognitionConfigRules.GeometryGoodnessDecayRate;

    public static IReadOnlyList<MapGeometryCandidate> RankGeometry(
        IReadOnlyList<MapGeometryFingerprint> fingerprints,
        IReadOnlyList<GateDetection> gates,
        MapScreenRect viewportBounds,
        double vectorErrorTolerance = -1d,
        bool testSwappedAssignments = true)
    {
        if (fingerprints.Count == 0 || gates.Count < 2 || !viewportBounds.IsValid)
            return [];

        var bestByMap = new Dictionary<Guid, MapGeometryCandidate>();
        for (var left = 0; left < gates.Count - 1; left++)
        {
            for (var right = left + 1; right < gates.Count; right++)
            {
                foreach (var fingerprint in fingerprints)
                {
                    EvaluateAssignment(
                        fingerprint,
                        gates[left],
                        gates[right],
                        vectorErrorTolerance,
                        bestByMap);
                    if (testSwappedAssignments)
                    {
                        EvaluateAssignment(
                            fingerprint,
                            gates[right],
                            gates[left],
                            vectorErrorTolerance,
                            bestByMap);
                    }
                }
            }
        }

        return bestByMap.Values
            .OrderBy(candidate => candidate.VectorError)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Fingerprint.Map.Id)
            .ToArray();
    }

    /// <summary>
    /// Converts a geometry error ratio into a 0..1 "goodness" score using a
    /// soft exponential curve. A match comfortably inside the tolerance keeps
    /// most of its credit; only near-tolerance errors are discounted sharply.
    /// This replaces the former linear (1 − v/t) penalty, which over-penalized
    /// matches that were already well within the tolerance.
    /// </summary>
    public static double GeometryGoodness(
        double vectorError,
        double vectorErrorTolerance) =>
        MapAlignmentConfidence.GeometryGoodness(
            vectorError,
            vectorErrorTolerance);

    /// <summary>
    /// Confidence for a dual-gate recognition. Gate template score carries the
    /// primary weight — clearly visible gates are the strongest evidence —
    /// while geometry contributes as a soft secondary check.
    /// </summary>
    [Obsolete("Use MapAlignmentConfidence.ComputeDualGateConfidence instead")]
    public static double ComputeDualGateConfidence(
        double mainGateScore,
        double sideGateScore,
        double vectorError,
        double vectorErrorTolerance) =>
        MapAlignmentConfidence.ComputeDualGateConfidence(
            mainGateScore,
            sideGateScore,
            vectorError,
            vectorErrorTolerance);

    /// <summary>
    /// 单门跟踪的置信度计算。缺少双门几何验证，但可以通过锁定会话的
    /// 先验置信度和模板匹配质量来补偿。单门跟踪本质上是"已知地图+
    /// 锁定缩放"下的平移更新，其可靠性应接近双门对齐。
    /// </summary>
    /// <param name="gateScore">单个门的模板匹配分数</param>
    /// <param name="lockedSessionConfidence">锁定会话的原始置信度（来自初始双门对齐）</param>
    /// <param name="trackingWeight">跟踪模式下当前观测的权重（0.6 = 当前观测60%，先验40%）</param>
    [Obsolete("Use MapAlignmentConfidence.ComputeSingleGateTrackingConfidence with explicit scaleAgreement")]
    public static double ComputeSingleGateTrackingConfidence(
        double gateScore,
        double lockedSessionConfidence,
        double trackingWeight = 0.6d) =>
        MapAlignmentConfidence.ComputeSingleGateTrackingConfidence(
            gateScore,
            lockedSessionConfidence,
            scaleAgreement: 1d); // Legacy: assume perfect scale agreement

    public static double WrappedAngleDifference(double left, double right)
    {
        var difference = Math.Abs(left - right) % (Math.PI * 2d);
        return difference > Math.PI ? (Math.PI * 2d) - difference : difference;
    }

    private static void EvaluateAssignment(
        MapGeometryFingerprint fingerprint,
        GateDetection main,
        GateDetection side,
        double vectorErrorTolerance,
        Dictionary<Guid, MapGeometryCandidate> bestByMap)
    {
        var referenceMain = new MapNormalizedPoint(
            fingerprint.MainPoint.X * fingerprint.ReferenceWidth,
            fingerprint.MainPoint.Y * fingerprint.ReferenceHeight);
        var referenceSide = new MapNormalizedPoint(
            fingerprint.SidePoint.X * fingerprint.ReferenceWidth,
            fingerprint.SidePoint.Y * fingerprint.ReferenceHeight);
        var screenMain = new MapNormalizedPoint(
            main.ScreenBounds.CenterX,
            main.ScreenBounds.CenterY);
        var screenSide = new MapNormalizedPoint(
            side.ScreenBounds.CenterX,
            side.ScreenBounds.CenterY);
        var referenceCenter = Midpoint(referenceMain, referenceSide);
        var screenCenter = Midpoint(screenMain, screenSide);
        var referenceDeltaX = referenceSide.X - referenceMain.X;
        var referenceDeltaY = referenceSide.Y - referenceMain.Y;
        var screenDeltaX = screenSide.X - screenMain.X;
        var screenDeltaY = screenSide.Y - screenMain.Y;

        // Gate boxes scale together with the draggable map. Normalizing each
        // axis by their observed size removes both zoom and ROI dimensions.
        // Use template-matched reference icon sizes when available; otherwise
        // fall back to user-drawn anchor bounds (loose rectangles that may be
        // larger than the actual gate icon).
        var refMainWidth = fingerprint.HasReferenceGateIconSize
            ? fingerprint.ReferenceGateIconWidth
            : fingerprint.MainReferenceBounds.Width;
        var refSideWidth = fingerprint.HasReferenceGateIconSize
            ? fingerprint.ReferenceGateIconWidth
            : fingerprint.SideReferenceBounds.Width;
        var refMainHeight = fingerprint.HasReferenceGateIconSize
            ? fingerprint.ReferenceGateIconHeight
            : fingerprint.MainReferenceBounds.Height;
        var refSideHeight = fingerprint.HasReferenceGateIconSize
            ? fingerprint.ReferenceGateIconHeight
            : fingerprint.SideReferenceBounds.Height;

        var estimatedScaleX = EstimateAxisScale(
            refMainWidth,
            refSideWidth,
            main.ScreenBounds.Width,
            side.ScreenBounds.Width,
            referenceDeltaX,
            screenDeltaX);
        var estimatedScaleY = EstimateAxisScale(
            refMainHeight,
            refSideHeight,
            main.ScreenBounds.Height,
            side.ScreenBounds.Height,
            referenceDeltaY,
            screenDeltaY);
        var alignedReferenceDeltaX = referenceDeltaX * estimatedScaleX;
        var alignedReferenceDeltaY = referenceDeltaY * estimatedScaleY;
        var screenDistance = Length(screenDeltaX, screenDeltaY);
        var alignedReferenceDistance = Length(
            alignedReferenceDeltaX,
            alignedReferenceDeltaY);
        var normalizationDistance = Math.Max(
            1d,
            Math.Max(screenDistance, alignedReferenceDistance));
        var distanceError =
            Math.Abs(screenDistance - alignedReferenceDistance) / normalizationDistance;
        var rawAngleError = WrappedAngleDifference(
            Math.Atan2(screenDeltaY, screenDeltaX),
            Math.Atan2(referenceDeltaY, referenceDeltaX));
        var scaleAdjustedAngleError = WrappedAngleDifference(
            Math.Atan2(screenDeltaY, screenDeltaX),
            Math.Atan2(alignedReferenceDeltaY, alignedReferenceDeltaX));
        var angleError = Math.Min(rawAngleError, scaleAdjustedAngleError);
        var directionError = 2d * Math.Sin(angleError / 2d);
        // Combine direction error and distance error into a single vector
        // metric so that candidates with correct direction but wrong gate
        // spacing cannot pass. Both components are scale-invariant (distance
        // error is already normalised by the longer diagonal) and equally
        // important for rejecting incorrect map identities.
        var vectorError = Math.Sqrt(
            (directionError * directionError)
            + (distanceError * distanceError));
        var tolerance = double.IsFinite(vectorErrorTolerance) && vectorErrorTolerance > 0d
            ? vectorErrorTolerance
            : VectorErrorTolerance;
        var vectorScore = 1d - Math.Clamp(vectorError / tolerance, 0d, 1d);
        var distanceScore = 1d - Math.Clamp(distanceError / tolerance, 0d, 1d);
        var angleScore = 1d - Math.Clamp(
            angleError / RecognitionConfigRules.AngleNormalizationRadians, 0d, 1d);
        var weights = RecognitionConfigRules.ScoreWeights;
        var geometryScore = (vectorScore * weights.VectorScoreWeight)
            + (distanceScore * weights.DistanceScoreWeight)
            + (angleScore * weights.AngleScoreWeight);
        var templateScore = Math.Clamp((main.Score + side.Score) / 2d, 0d, 1d);
        var candidate = new MapGeometryCandidate
        {
            Fingerprint = fingerprint,
            MainGate = main,
            SideGate = side,
            ReferenceCenter = referenceCenter,
            ScreenCenter = screenCenter,
            EstimatedScaleX = estimatedScaleX,
            EstimatedScaleY = estimatedScaleY,
            VectorError = vectorError,
            DistanceError = distanceError,
            AngleError = angleError,
            Score = (geometryScore * weights.GeometryScoreWeight)
                + (templateScore * weights.TemplateScoreWeight)
        };
        if (!bestByMap.TryGetValue(fingerprint.Map.Id, out var current)
            || candidate.VectorError < current.VectorError
            || (Math.Abs(candidate.VectorError - current.VectorError) < 0.000001d && candidate.Score > current.Score))
        {
            bestByMap[fingerprint.Map.Id] = candidate;
        }
    }

    private static double EstimateAxisScale(
        double firstReferenceSize,
        double secondReferenceSize,
        double firstScreenSize,
        double secondScreenSize,
        double referenceDelta,
        double screenDelta)
    {
        var referenceSize = AveragePositive(firstReferenceSize, secondReferenceSize);
        var screenSize = AveragePositive(firstScreenSize, secondScreenSize);
        if (referenceSize > 0d && screenSize > 0d)
            return screenSize / referenceSize;
        if (Math.Abs(referenceDelta) > 1d)
            return Math.Abs(screenDelta / referenceDelta);
        return 1d;
    }

    private static double AveragePositive(double first, double second)
    {
        var firstIsValid = double.IsFinite(first) && first > 0d;
        var secondIsValid = double.IsFinite(second) && second > 0d;
        if (firstIsValid && secondIsValid)
            return (first + second) / 2d;
        if (firstIsValid)
            return first;
        return secondIsValid ? second : 0d;
    }

    private static MapNormalizedPoint Midpoint(
        MapNormalizedPoint left,
        MapNormalizedPoint right) =>
        new((left.X + right.X) / 2d, (left.Y + right.Y) / 2d);

    private static double Length(double x, double y) => Math.Sqrt((x * x) + (y * y));
}
