using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed class MapSessionTuning
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int OpeningAnimationDelayMilliseconds { get; set; } = 10;
    public int OpeningTimeoutMilliseconds { get; set; } = 3000;
    public int StableFrameIntervalMilliseconds { get; set; } = 20;
    public int StableFrameCount { get; set; } = 2;
    public double StableFrameDifference { get; set; } = 0.015d;
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
    public bool SkipStabilityConfirmation { get; set; } = true;
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
            20,
            250);
        StableFrameCount = Math.Clamp(StableFrameCount, 2, 8);
        StableFrameDifference = Finite(
            StableFrameDifference,
            0.015d,
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
