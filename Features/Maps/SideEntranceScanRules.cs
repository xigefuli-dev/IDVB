using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 侧门扫描调参。可通过 <see cref="IConfigProvider"/> 在 "side_entrance" TOML
/// 段下覆盖。三个分辨率预设目录（1920x1080 / 2560x1440 / 2560x1600）各提供
/// 专属 side_entrance.toml，按分辨率定制扫描网格密度与并行度。
/// </summary>
public sealed class SideEntranceScanConfig
{
    /// <summary>粗搜索的相对步长；决定缩放网格疏密。0.06 → 约 24 档。</summary>
    public double CoarseScaleStep { get; set; } = 0.06d;
    /// <summary>细化阶段在粗峰值两侧各取的档数。</summary>
    public int RefineStepsPerSide { get; set; } = 3;
    /// <summary>粗搜索的降采样倍率（帧尺寸 ÷ 该值）。</summary>
    public int CoarsePyramidFactor { get; set; } = 4;
    /// <summary>只对粗分前 K 的地图做全分辨率精化（粗分数剪枝）。</summary>
    public int RefineCandidateTopK { get; set; } = 5;
    /// <summary>粗分绝对下限，低于该值的地图直接淘汰；0 = 不启用绝对剪枝。</summary>
    public double CoarseScorePruneThreshold { get; set; } = 0d;
    /// <summary>跨地图扫描并行度；1 = 串行。</summary>
    public int ScanParallelism { get; set; } = 4;
}

/// <summary>
/// 侧门扫描的可配置规则入口。遵循与 <see cref="RecognitionConfigRules"/> 相同的
/// 模式：内部持有 <see cref="SideEntranceScanConfig"/> 实例，对外暴露 static
/// 属性并通过 ApplyConfig 注入。
/// </summary>
internal static class SideEntranceScanRules
{
    private static SideEntranceScanConfig _config = new();

    public static double CoarseScaleStep => _config.CoarseScaleStep;
    public static int RefineStepsPerSide => _config.RefineStepsPerSide;
    public static int CoarsePyramidFactor => _config.CoarsePyramidFactor;
    public static int RefineCandidateTopK => _config.RefineCandidateTopK;
    public static double CoarseScorePruneThreshold => _config.CoarseScorePruneThreshold;
    public static int ScanParallelism => _config.ScanParallelism;

    /// <summary>Apply a pre-populated <see cref="SideEntranceScanConfig"/> instance.</summary>
    internal static void ApplyConfig(SideEntranceScanConfig config)
    {
        _config = config ?? new SideEntranceScanConfig();
    }

    /// <summary>
    /// Read and apply configuration from an <see cref="IConfigProvider"/> under
    /// the "side_entrance" TOML section.
    /// </summary>
    internal static void ApplyConfig(IConfigProvider provider)
    {
        _config = provider.Get<SideEntranceScanConfig>("side_entrance")
            ?? new SideEntranceScanConfig();
    }
}
