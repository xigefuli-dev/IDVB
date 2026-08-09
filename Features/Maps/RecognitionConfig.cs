using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 几何评分权重。控制在 RankGeometry / EvaluateAssignment 中各分量
/// 对最终得分的贡献比例。
/// </summary>
public sealed class GeometryScoreWeights
{
    /// <summary>向量误差分量在几何子评分中的权重。</summary>
    public double VectorScoreWeight { get; set; } = 0.65d;
    /// <summary>距离误差分量在几何子评分中的权重。</summary>
    public double DistanceScoreWeight { get; set; } = 0.25d;
    /// <summary>角度误差分量在几何子评分中的权重。</summary>
    public double AngleScoreWeight { get; set; } = 0.10d;
    /// <summary>几何子评分在候选总分中的权重。</summary>
    public double GeometryScoreWeight { get; set; } = 0.85d;
    /// <summary>模板匹配子评分在候选总分中的权重。</summary>
    public double TemplateScoreWeight { get; set; } = 0.15d;
}

/// <summary>
/// 识别算法参数，可通过 <see cref="IConfigProvider"/> 在 "recognition" TOML
/// 分段下覆盖。所有属性均有与当前硬编码一致的默认值。
/// </summary>
internal sealed class RecognitionConfig
{
    // ── MapFastAlignmentRules ───────────────────────────────────────────
    public double MinimumDirectLockConfidence { get; set; } = 0.75d;

    // ── MapOverlayTransformSolver ──────────────────────────────────────
    public double ExactFitTolerancePixels { get; set; } = 2d;
    public double MinimumScale { get; set; } = 0.1d;
    public double MaximumScale { get; set; } = 8d;
    public double MinimumStableAxisPixels { get; set; } = 4d;
    public double StableAxisDistanceRatio { get; set; } = 0.05d;

    // ── MapCvRecognitionScript ─────────────────────────────────────────
    public double VectorErrorTolerance { get; set; } = 0.15d;
    public double AmbiguityMargin { get; set; } = 0.015d;
    public double ConfirmationMargin { get; set; } = 0.08d;
    public double GeometryGoodnessDecayRate { get; set; } = 1.0d;

    /// <summary>角度归一化上限（弧度），默认 15°。</summary>
    public double AngleNormalizationRadians { get; set; } = Math.PI / 12d;

    // ── Scoring weights ────────────────────────────────────────────────
    public GeometryScoreWeights ScoreWeights { get; set; } = new();
}

/// <summary>
/// 识别算法的可配置规则入口。遵循与 <see cref="GateTemplateRules"/> 相同的
/// 模式：内部持有 <see cref="RecognitionConfig"/> 实例，对外暴露 static
/// 属性并通过 ApplyConfig 注入。
/// </summary>
internal static class RecognitionConfigRules
{
    private static RecognitionConfig _config = new();

    // ── MapFastAlignmentRules ───────────────────────────────────────
    public static double MinimumDirectLockConfidence => _config.MinimumDirectLockConfidence;

    // ── MapOverlayTransformSolver ───────────────────────────────────
    public static double ExactFitTolerancePixels => _config.ExactFitTolerancePixels;
    public static double MinimumScale => _config.MinimumScale;
    public static double MaximumScale => _config.MaximumScale;
    public static double MinimumStableAxisPixels => _config.MinimumStableAxisPixels;
    public static double StableAxisDistanceRatio => _config.StableAxisDistanceRatio;

    // ── MapCvRecognitionScript ──────────────────────────────────────
    public static double VectorErrorTolerance => _config.VectorErrorTolerance;
    public static double AmbiguityMargin => _config.AmbiguityMargin;
    public static double ConfirmationMargin => _config.ConfirmationMargin;
    public static double GeometryGoodnessDecayRate => _config.GeometryGoodnessDecayRate;
    public static double AngleNormalizationRadians => _config.AngleNormalizationRadians;

    // ── Scoring weights ─────────────────────────────────────────────
    public static GeometryScoreWeights ScoreWeights => _config.ScoreWeights;

    /// <summary>Apply a pre-populated <see cref="RecognitionConfig"/> instance.</summary>
    internal static void ApplyConfig(RecognitionConfig config)
    {
        _config = config ?? new RecognitionConfig();
    }

    /// <summary>
    /// Read and apply configuration from an <see cref="IConfigProvider"/>
    /// under the "geometry" TOML section.
    /// </summary>
    internal static void ApplyConfig(IConfigProvider provider)
    {
        _config = provider.Get<RecognitionConfig>("geometry") ?? new RecognitionConfig();
    }
}
