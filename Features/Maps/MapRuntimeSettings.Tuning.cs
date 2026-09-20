using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed class MapSessionTuning
{
    public const int CurrentSchemaVersion = 4;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int OpeningAnimationDelayMilliseconds { get; set; } = 10;
    public int OpeningTimeoutMilliseconds { get; set; } = 3000;
    public int StableFrameIntervalMilliseconds { get; set; } = 10;
    public int StableFrameCount { get; set; } = 3;
    public double StableFrameDifference { get; set; } = 0.005d;
    public int PresencePollingMilliseconds { get; set; } = 200;
    public int PlayerPollingMilliseconds { get; set; } = 100;
    public int WindowValidationMilliseconds { get; set; } = 500;
    public double HighConfidence { get; set; } = 0.70d;
    public double MediumConfidence { get; set; } = 0.60d;
    public int MediumConfidenceFrames { get; set; } = 2;
    public double CandidateStabilityPixels { get; set; } = 3d;
    public double NativeScaleChangeRatio { get; set; } =
        MapSessionRules.NativeScaleChangeRatio;
    /// <summary>设为 true 则跳过中等置信度的连续帧稳定性确认，直接锁定。</summary>
    public bool SkipStabilityConfirmation { get; set; }
    public List<NormalizedRectangle> ViewportIgnoreRegions { get; set; } = [];

    public MapSessionTuning Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        OpeningAnimationDelayMilliseconds = OpeningAnimationDelayMilliseconds,
        OpeningTimeoutMilliseconds = OpeningTimeoutMilliseconds,
        StableFrameIntervalMilliseconds = StableFrameIntervalMilliseconds,
        StableFrameCount = StableFrameCount,
        StableFrameDifference = StableFrameDifference,
        PresencePollingMilliseconds = PresencePollingMilliseconds,
        PlayerPollingMilliseconds = PlayerPollingMilliseconds,
        WindowValidationMilliseconds = WindowValidationMilliseconds,
        HighConfidence = HighConfidence,
        MediumConfidence = MediumConfidence,
        MediumConfidenceFrames = MediumConfidenceFrames,
        CandidateStabilityPixels = CandidateStabilityPixels,
        NativeScaleChangeRatio = NativeScaleChangeRatio,
        SkipStabilityConfirmation = SkipStabilityConfirmation,
        ViewportIgnoreRegions = ViewportIgnoreRegions
            .Where(region => region?.IsValid is true)
            .Select(region => region.Clone())
            .ToList()
    };

    public void Normalize()
    {
        var previousSchema = SchemaVersion;
        if (previousSchema < CurrentSchemaVersion
            && OpeningAnimationDelayMilliseconds == 250)
        {
            // 250ms was the old built-in default; migrate it to the new default
            // while preserving any explicit value saved by newer versions.
            OpeningAnimationDelayMilliseconds = 50;
        }
        if (previousSchema < 3
            && StableFrameCount == 2
            && Math.Abs(StableFrameDifference - 0.015d) < 0.0000001d
            && SkipStabilityConfirmation)
        {
            // Migrate only the complete legacy default tuple. A user who
            // changed any of these values keeps that explicit tuning.
            StableFrameCount = 3;
            StableFrameDifference = 0.005d;
            SkipStabilityConfirmation = false;
        }
        if (previousSchema < 4
            && StableFrameIntervalMilliseconds == 20
            && StableFrameCount == 3
            && Math.Abs(StableFrameDifference - 0.005d) < 0.0000001d
            && !SkipStabilityConfirmation)
        {
            // 稳定帧间隔旧默认 20ms → 新默认 10ms：连续三帧确认的帧间等待减半。
            // 仅当仍是完整旧默认元组时迁移，避免改写用户显式调过的值。
            StableFrameIntervalMilliseconds = 10;
        }
        SchemaVersion = CurrentSchemaVersion;
        OpeningAnimationDelayMilliseconds = Math.Clamp(
            OpeningAnimationDelayMilliseconds,
            0,
            1500);
        OpeningTimeoutMilliseconds = Math.Clamp(
            OpeningTimeoutMilliseconds,
            1000,
            10000);
        StableFrameIntervalMilliseconds = Math.Clamp(
            StableFrameIntervalMilliseconds,
            10,
            250);
        StableFrameCount = Math.Clamp(StableFrameCount, 2, 8);
        StableFrameDifference = Finite(
            StableFrameDifference,
            0.005d,
            0.001d,
            0.10d);
        PresencePollingMilliseconds = Math.Clamp(
            PresencePollingMilliseconds,
            100,
            2000);
        PlayerPollingMilliseconds = Math.Clamp(
            PlayerPollingMilliseconds,
            50,
            1000);
        WindowValidationMilliseconds = Math.Clamp(
            WindowValidationMilliseconds,
            250,
            5000);
        HighConfidence = Finite(HighConfidence, 0.70d, 0.65d, 0.99d);
        MediumConfidence = Finite(
            MediumConfidence,
            0.60d,
            0.30d,
            HighConfidence - 0.01d);
        MediumConfidenceFrames = Math.Clamp(MediumConfidenceFrames, 2, 8);
        CandidateStabilityPixels = Finite(
            CandidateStabilityPixels,
            3d,
            0.5d,
            20d);
        NativeScaleChangeRatio = Finite(
            NativeScaleChangeRatio,
            MapSessionRules.NativeScaleChangeRatio,
            0.005d,
            0.20d);
        ViewportIgnoreRegions ??= [];
        ViewportIgnoreRegions = ViewportIgnoreRegions
            .Where(region => region?.IsValid is true)
            .Select(region => NormalizeRegion(region!))
            .Where(region => region.IsValid)
            .ToList();
    }

    private static NormalizedRectangle NormalizeRegion(
        NormalizedRectangle region)
    {
        var left = Math.Clamp(region.X, 0d, 1d);
        var top = Math.Clamp(region.Y, 0d, 1d);
        var right = Math.Clamp(region.X + region.Width, left, 1d);
        var bottom = Math.Clamp(region.Y + region.Height, top, 1d);
        return new NormalizedRectangle
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
    }

    private static double Finite(
        double value,
        double fallback,
        double minimum,
        double maximum) =>
        double.IsFinite(value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
}

/// <summary>
/// 物理客户端分辨率调优档案：当标定分辨率匹配时，将档案中的非 null 参数
/// 覆盖到全局 <see cref="MapStructureRegistrationTuning"/> 和识别参数上。
/// 新增分辨率只需在 settings.json 中添加一条 JSON 对象。
/// </summary>
public sealed class ResolutionTuningProfile
{
    /// <summary>档案名称（仅用于标识，不参与匹配）。</summary>
    public string Name { get; set; } = "";

    /// <summary>精确匹配的客户端宽度（像素）。</summary>
    public int ClientWidth { get; set; }

    /// <summary>精确匹配的客户端高度（像素）。</summary>
    public int ClientHeight { get; set; }

    /// <summary>观测 DPI；仅用于同尺寸重复档案的次级偏好和诊断。</summary>
    public int Dpi { get; set; }

    /// <summary>模糊匹配时允许的宽/高像素偏差，默认 100。</summary>
    public int MatchTolerancePixels { get; set; } = 100;

    // ── 结构配准覆盖项 ──
    public double? MaximumChamferPixels { get; set; }
    public double? MinimumEdgeCoverage { get; set; }
    public double? MinimumOccupancyCoverage { get; set; }
    public double? EdgeDistanceTolerancePixels { get; set; }
    public int? FastCoarseMaxDimension { get; set; }
    public int? FastCoarseDownsampleFactor { get; set; }
    public double? ScaleSearchRadius { get; set; }
    public double? ScaleSearchStep { get; set; }
    public double? MinimumCandidateMargin { get; set; }

    // ── 识别参数覆盖项 ──
    public double? GateTemplateThreshold { get; set; }
    public double? VectorErrorTolerance { get; set; }

    /// <summary>将档案中的非 null 值覆盖到给定的 tuning 上。</summary>
    public void ApplyTo(MapStructureRegistrationTuning tuning)
    {
        if (MaximumChamferPixels is { } mcp) tuning.MaximumChamferPixels = mcp;
        if (MinimumEdgeCoverage is { } mec) tuning.MinimumEdgeCoverage = mec;
        if (MinimumOccupancyCoverage is { } moc) tuning.MinimumOccupancyCoverage = moc;
        if (EdgeDistanceTolerancePixels is { } edt) tuning.EdgeDistanceTolerancePixels = edt;
        if (FastCoarseMaxDimension is { } fmd) tuning.FastCoarseMaxDimension = fmd;
        if (FastCoarseDownsampleFactor is { } fdf) tuning.FastCoarseDownsampleFactor = fdf;
        if (ScaleSearchRadius is { } ssr) tuning.ScaleSearchRadius = ssr;
        if (ScaleSearchStep is { } sss) tuning.ScaleSearchStep = sss;
        if (MinimumCandidateMargin is { } mcm) tuning.MinimumCandidateMargin = mcm;
    }

    public void ApplyTo(MapRecognitionTuning tuning)
    {
        if (GateTemplateThreshold is { } gtt) tuning.GateTemplateThreshold = gtt;
        if (VectorErrorTolerance is { } vet) tuning.VectorErrorTolerance = vet;
    }

    /// <summary>
    /// 在档案列表中匹配合适的档案。
    /// 1) 精确匹配物理宽/高（DPI 仅作为重复项的次级偏好）
    /// 2) 模糊匹配物理宽/高
    /// 3) 宽高比近似匹配
    /// </summary>
    public static ResolutionTuningProfile? Match(
        IReadOnlyList<ResolutionTuningProfile> profiles,
        int clientWidth,
        int clientHeight,
        int dpi)
    {
        if (profiles.Count == 0)
            return null;

        // 1. 精确匹配
        var exact = profiles
            .Where(p =>
                p.ClientWidth == clientWidth
                && p.ClientHeight == clientHeight)
            .OrderByDescending(p => p.Dpi == dpi)
            .FirstOrDefault();
        if (exact is not null)
            return exact;

        // 2. 模糊匹配
        var fuzzy = profiles
            .Where(p =>
                Math.Abs(p.ClientWidth - clientWidth) <= p.MatchTolerancePixels
                && Math.Abs(p.ClientHeight - clientHeight) <= p.MatchTolerancePixels)
            .OrderBy(p =>
                Math.Abs(p.ClientWidth - clientWidth)
                + Math.Abs(p.ClientHeight - clientHeight))
            .ThenByDescending(p => p.Dpi == dpi)
            .FirstOrDefault();
        if (fuzzy is not null)
            return fuzzy;

        // 3. 物理宽高比近似匹配
        var ratio = (double)clientWidth / clientHeight;
        const double ratioTolerance = 0.05;
        return profiles
            .Where(p =>
                p.ClientHeight > 0
                && Math.Abs((double)p.ClientWidth / p.ClientHeight - ratio)
                    < ratioTolerance)
            .OrderBy(p => Math.Abs(p.ClientWidth - clientWidth))
            .ThenByDescending(p => p.Dpi == dpi)
            .FirstOrDefault();
    }
}
/*
 * 文件职责：MapRuntimeSettings.Tuning。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
