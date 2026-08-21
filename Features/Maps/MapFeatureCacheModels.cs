using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public static class MapFeatureCacheSchema
{
    public const int CurrentVersion = 3;

    public static bool IsSupported(int version) =>
        version is >= 1 and <= CurrentVersion;
}

public enum MapFeatureCacheSource
{
    Manual = 0,
    Automatic = 1,
    Recovery = 2,
    PreprocessedEstimate = 3,
    CrossResolutionValidated = 4,
    /// <summary>
    /// Explicit player adjustment confirmed in the manual transform window.
    /// Highest-trust scale evidence; protected from automatic overwrites.
    /// </summary>
    Player = 5
}

public sealed record MapCacheResolutionSignature(
    int ClientWidth,
    int ClientHeight,
    int ViewportWidth,
    int ViewportHeight)
{
    public bool IsSupported =>
        ViewportWidth > 0
        && ViewportHeight > 0
        && (ClientWidth, ClientHeight) is
            (1920, 1080) or (2560, 1440) or (2560, 1600)
            or (3440, 1440) or (2560, 1080);

    public static MapCacheResolutionSignature FromBounds(
        MapScreenRect clientBounds,
        MapScreenRect viewportBounds,
        uint observedDpi) =>
        new(
            (int)Math.Round(clientBounds.Width),
            (int)Math.Round(clientBounds.Height),
            (int)Math.Round(viewportBounds.Width),
            (int)Math.Round(viewportBounds.Height));
}

public sealed record MapFeatureCacheKey(
    Guid MapId,
    string MapContentFingerprint,
    string FloorKey,
    MapCacheResolutionSignature Resolution)
{
    [JsonIgnore]
    public bool IsValid =>
        MapId != Guid.Empty
        && !string.IsNullOrWhiteSpace(MapContentFingerprint)
        && !string.IsNullOrWhiteSpace(FloorKey)
        && Resolution.IsSupported;
}

internal enum MapScaleSeedSource
{
    ExactCache,
    CrossResolution,
    Vpsg,
    SideTemplate
}

internal sealed record ResolvedMapScaleSeed(
    double Scale,
    MapScaleSeedSource Source,
    MapFeatureCacheSource CacheSource,
    MapCacheResolutionSignature SourceResolution,
    MapCacheResolutionSignature TargetResolution,
    bool IsProjected,
    MapFeatureCacheEntry CacheEntry);

/// <summary>
/// Pure cache-selection and cross-resolution projection logic. A resolved
/// value is only a search seed; callers must still run structure validation.
/// </summary>
internal static class MapScaleSeedResolver
{
    public const double MaximumAxisScaleDisagreement = 0.03d;

    public static bool TryResolve(
        IEnumerable<MapFeatureCacheEntry> entries,
        Guid mapId,
        string contentFingerprint,
        string floorKey,
        MapCacheResolutionSignature targetResolution,
        double minimumLocalizationConfidence,
        double minimumCandidateMargin,
        out ResolvedMapScaleSeed? resolved,
        out string rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(entries);
        resolved = null;
        rejectionReason = "no-trusted-cache";

        var trusted = entries
            .Where(entry => IsTrustedScaleEntry(
                entry,
                mapId,
                contentFingerprint,
                floorKey,
                minimumLocalizationConfidence,
                minimumCandidateMargin))
            .ToArray();
        var exact = OrderByTrust(trusted
                .Where(entry => entry.Key.Resolution == targetResolution))
            .FirstOrDefault();
        if (exact is not null)
        {
            resolved = new ResolvedMapScaleSeed(
                exact.Scale.UniformScale,
                MapScaleSeedSource.ExactCache,
                exact.Scale.Source,
                exact.Key.Resolution,
                targetResolution,
                IsProjected: false,
                exact);
            rejectionReason = string.Empty;
            return true;
        }

        var projectable = OrderByTrust(trusted
                .Where(entry => entry.Key.Resolution != targetResolution))
            .Select(entry => new
            {
                Entry = entry,
                Projection = TryProjectScale(
                    entry.Scale.UniformScale,
                    entry.Key.Resolution,
                    targetResolution,
                    out var scale,
                    out var reason)
                    ? (Scale: scale, Reason: string.Empty)
                    : (Scale: double.NaN, Reason: reason)
            })
            .ToArray();
        var projected = projectable.FirstOrDefault(candidate =>
            double.IsFinite(candidate.Projection.Scale));
        if (projected is null)
        {
            rejectionReason = projectable.Length == 0
                ? "no-trusted-cache"
                : string.Join(",", projectable
                    .Select(candidate => candidate.Projection.Reason)
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct(StringComparer.Ordinal));
            return false;
        }

        resolved = new ResolvedMapScaleSeed(
            projected.Projection.Scale,
            MapScaleSeedSource.CrossResolution,
            projected.Entry.Scale.Source,
            projected.Entry.Key.Resolution,
            targetResolution,
            IsProjected: true,
            projected.Entry);
        rejectionReason = string.Empty;
        return true;
    }

    public static bool TryProjectScale(
        double sourceScale,
        MapCacheResolutionSignature sourceResolution,
        MapCacheResolutionSignature targetResolution,
        out double projectedScale,
        out string rejectionReason)
    {
        projectedScale = double.NaN;
        rejectionReason = string.Empty;
        if (!double.IsFinite(sourceScale)
            || sourceScale <= 0.05d
            || sourceResolution.ViewportWidth <= 0
            || sourceResolution.ViewportHeight <= 0
            || targetResolution.ViewportWidth <= 0
            || targetResolution.ViewportHeight <= 0)
        {
            rejectionReason = "invalid-scale-or-viewport";
            return false;
        }

        var widthRatio = (double)targetResolution.ViewportWidth
            / sourceResolution.ViewportWidth;
        var heightRatio = (double)targetResolution.ViewportHeight
            / sourceResolution.ViewportHeight;
        var axisDisagreement = Math.Abs(widthRatio - heightRatio)
            / Math.Max(widthRatio, heightRatio);
        if (axisDisagreement > MaximumAxisScaleDisagreement + 1e-12d)
        {
            rejectionReason = "viewport-axis-scale-disagreement";
            return false;
        }

        projectedScale = sourceScale * Math.Sqrt(
            ((double)targetResolution.ViewportWidth
                * targetResolution.ViewportHeight)
            / ((double)sourceResolution.ViewportWidth
                * sourceResolution.ViewportHeight));
        if (!double.IsFinite(projectedScale) || projectedScale <= 0.05d)
        {
            projectedScale = double.NaN;
            rejectionReason = "invalid-projected-scale";
            return false;
        }
        return true;
    }

    public static MapStructureRegistrationTuning CreateStrictInitialIdentityValidationTuning(
        MapStructureRegistrationTuning source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var strict = source.Clone();
        strict.MaximumChamferPixels = Math.Min(
            strict.MaximumChamferPixels,
            Math.Min(strict.RestrictedSearchMaximumChamferPixels, 3.0d));
        strict.RestrictedSearchMaximumChamferPixels = Math.Min(
            strict.RestrictedSearchMaximumChamferPixels,
            3.0d);
        return strict;
    }

    public static MapStructureRegistrationTuning CreateStrictVpsgValidationTuning(
        MapStructureRegistrationTuning source) =>
        CreateStrictInitialIdentityValidationTuning(source);

    private static bool IsTrustedScaleEntry(
        MapFeatureCacheEntry entry,
        Guid mapId,
        string contentFingerprint,
        string floorKey,
        double minimumLocalizationConfidence,
        double minimumCandidateMargin)
    {
        if (!entry.IsValid
            || !MapFeatureCacheRules.IsCacheEntryTrusted(entry)
            || entry.Key.MapId != mapId
            || !string.Equals(
                entry.Key.MapContentFingerprint,
                contentFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(entry.Key.FloorKey, floorKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (entry.Scale.Source is MapFeatureCacheSource.Manual
            or MapFeatureCacheSource.Player)
        {
            return true;
        }

        if (entry.Scale.Source is not (
                MapFeatureCacheSource.Recovery
                or MapFeatureCacheSource.PreprocessedEstimate
                or MapFeatureCacheSource.CrossResolutionValidated))
        {
            return false;
        }

        var validation = entry.Scale.Validation;
        return validation is
        {
            SuccessfulValidationCount: > 0
        }
        && validation.LastLocalizationConfidence
            >= Math.Clamp(minimumLocalizationConfidence, 0d, 1d)
        && validation.LastCandidateMargin
            >= Math.Max(0d, minimumCandidateMargin);
    }

    private static IOrderedEnumerable<MapFeatureCacheEntry> OrderByTrust(
        IEnumerable<MapFeatureCacheEntry> entries) =>
        entries
            .OrderByDescending(entry => entry.Scale.Source is
                MapFeatureCacheSource.Manual or MapFeatureCacheSource.Player)
            .ThenByDescending(entry =>
                entry.Scale.Validation?.SuccessfulValidationCount ?? 0)
            .ThenByDescending(entry =>
                entry.Scale.Validation?.LastLocalizationConfidence ?? 0d)
            .ThenByDescending(entry =>
                entry.Scale.Validation?.LastCandidateMargin ?? 0d)
            .ThenByDescending(entry => entry.Scale.UpdatedAt);
}

public sealed class MapScaleCachePayload
{
    public int SchemaVersion { get; set; } = MapFeatureCacheSchema.CurrentVersion;
    public double UniformScale { get; set; }
    public MapFeatureCacheSource Source { get; set; }
    public int SampleCount { get; set; }
    public double Confidence { get; set; }
    public double RelativeMedianAbsoluteDeviation { get; set; }
    public uint LastObservedDpi { get; set; }
    public MapScaleEstimationEvidence? EstimationEvidence { get; set; }
    public MapScaleCacheValidationMetadata? Validation { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        MapFeatureCacheSchema.IsSupported(SchemaVersion)
        && double.IsFinite(UniformScale)
        && UniformScale > 0.05d
        && SampleCount > 0
        && double.IsFinite(Confidence)
        && Confidence is >= 0d and <= 1d
        && double.IsFinite(RelativeMedianAbsoluteDeviation)
        && RelativeMedianAbsoluteDeviation >= 0d
        && (Validation?.IsValid ?? true)
        && UpdatedAt != default;
}

public sealed class MapScaleCacheValidationMetadata
{
    /// <summary>
    /// Explicit manual bindings, including migrated schema 1/2 bindings, are
    /// usable immediately while background recovery gathers stronger evidence.
    /// </summary>
    public bool DirectlyTrusted { get; set; }
    public int SuccessfulValidationCount { get; set; }
    public int FailedValidationCount { get; set; }
    public double LastLocalizationConfidence { get; set; }
    public double LastCandidateMargin { get; set; }
    public DateTimeOffset LastValidatedAt { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        SuccessfulValidationCount >= 0
        && FailedValidationCount >= 0
        && double.IsFinite(LastLocalizationConfidence)
        && LastLocalizationConfidence is >= 0d and <= 1d
        && double.IsFinite(LastCandidateMargin)
        && LastCandidateMargin >= 0d
        && (SuccessfulValidationCount + FailedValidationCount == 0
            || LastValidatedAt != default);
}

public sealed class MapScaleEstimationEvidence
{
    public int UniqueMatches { get; set; }
    public int PairVotes { get; set; }
    public double ReferenceSpan { get; set; }
    public double LiveSpan { get; set; }
    public double ResidualPixels { get; set; }
    public double RotationDegrees { get; set; }
    public double RelativeMedianAbsoluteDeviation { get; set; }
}

public sealed class MapFeatureCacheEntry
{
    public int SchemaVersion { get; set; } = MapFeatureCacheSchema.CurrentVersion;
    public required MapFeatureCacheKey Key { get; set; }
    public required MapScaleCachePayload Scale { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        MapFeatureCacheSchema.IsSupported(SchemaVersion)
        && Key.IsValid
        && Scale.IsValid;
}

public sealed class MapFeatureCacheDocument
{
    public int SchemaVersion { get; set; } = MapFeatureCacheSchema.CurrentVersion;
    public List<MapFeatureCacheEntry> Entries { get; set; } = [];
}

public sealed record MapScaleSample(
    double Scale,
    double Confidence,
    double CandidateMargin = 1d);

public sealed record MapScaleAggregate(
    double Scale,
    int SampleCount,
    double Confidence,
    double RelativeMedianAbsoluteDeviation,
    double CandidateMargin = 1d);

public sealed record MapCacheRepairSample(
    double Scale,
    double OffsetX,
    double OffsetY,
    double LocalizationConfidence,
    double CandidateMargin);

public sealed record MapCacheRepairAggregate(
    double Scale,
    int SampleCount,
    double LocalizationConfidence,
    double CandidateMargin,
    double RelativeMedianAbsoluteDeviation);

public static class MapCacheRepairSampleAggregator
{
    public const int RequiredConsecutiveSamples = 3;
    public const double MaximumOffsetDeviationPixels = 3d;

    public static bool TryAggregate(
        IReadOnlyList<MapCacheRepairSample> samples,
        out MapCacheRepairAggregate? aggregate)
    {
        aggregate = null;
        if (samples.Count < RequiredConsecutiveSamples)
            return false;

        var window = samples
            .TakeLast(RequiredConsecutiveSamples)
            .ToArray();
        if (window.Any(sample =>
                !double.IsFinite(sample.Scale)
                || sample.Scale <= 0.05d
                || !double.IsFinite(sample.OffsetX)
                || !double.IsFinite(sample.OffsetY)
                || !double.IsFinite(sample.LocalizationConfidence)
                || !double.IsFinite(sample.CandidateMargin)))
        {
            return false;
        }

        var medianScale = Median(window.Select(sample => sample.Scale));
        var relativeDeviations = window
            .Select(sample => Math.Abs(sample.Scale - medianScale) / medianScale)
            .ToArray();
        if (relativeDeviations.Max()
            > MapScaleSampleAggregator.MaximumRelativeTolerance)
        {
            return false;
        }

        var centerX = Median(window.Select(sample => sample.OffsetX));
        var centerY = Median(window.Select(sample => sample.OffsetY));
        if (window.Max(sample => Math.Sqrt(
                Math.Pow(sample.OffsetX - centerX, 2d)
                + Math.Pow(sample.OffsetY - centerY, 2d)))
            > MaximumOffsetDeviationPixels)
        {
            return false;
        }

        var weight = window.Sum(sample =>
            Math.Max(0.01d, sample.LocalizationConfidence));
        aggregate = new MapCacheRepairAggregate(
            window.Sum(sample =>
                sample.Scale
                * Math.Max(0.01d, sample.LocalizationConfidence)) / weight,
            window.Length,
            window.Average(sample => sample.LocalizationConfidence),
            window.Min(sample => sample.CandidateMargin),
            Median(relativeDeviations));
        return true;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }
}

public static class MapScaleSampleAggregator
{
    public const int MinimumStableSamples = 3;
    public const double MinimumRelativeTolerance = 0.005d;
    public const double MaximumRelativeTolerance = 0.015d;

    public static bool TryAggregate(
        IReadOnlyCollection<MapScaleSample> samples,
        out MapScaleAggregate? aggregate)
    {
        aggregate = null;
        var valid = samples
            .Where(sample =>
                double.IsFinite(sample.Scale)
                && sample.Scale > 0.05d
                && double.IsFinite(sample.Confidence)
                && double.IsFinite(sample.CandidateMargin)
                && sample.CandidateMargin >= 0d)
            .Select(sample => new MapScaleSample(
                sample.Scale,
                Math.Clamp(sample.Confidence, 0d, 1d),
                Math.Clamp(sample.CandidateMargin, 0d, 1d)))
            .OrderBy(sample => sample.Scale)
            .ToArray();
        if (valid.Length < MinimumStableSamples)
            return false;

        var median = Median(valid.Select(sample => sample.Scale));
        var relativeMad = Median(valid.Select(sample =>
            Math.Abs(sample.Scale - median) / median));
        var tolerance = Math.Clamp(
            3d * relativeMad,
            MinimumRelativeTolerance,
            MaximumRelativeTolerance);

        MapScaleSample[] bestCluster = [];
        foreach (var center in valid.Select(sample => sample.Scale))
        {
            var cluster = valid
                .Where(sample => Math.Abs(sample.Scale - center) / center <= tolerance)
                .ToArray();
            if (cluster.Length > bestCluster.Length
                || (cluster.Length == bestCluster.Length
                    && ClusterSpread(cluster) < ClusterSpread(bestCluster)))
            {
                bestCluster = cluster;
            }
        }

        if (bestCluster.Length < MinimumStableSamples)
            return false;

        var weightSum = bestCluster.Sum(sample => Math.Max(0.01d, sample.Confidence));
        var scale = bestCluster.Sum(sample =>
            sample.Scale * Math.Max(0.01d, sample.Confidence)) / weightSum;
        var clusterMedian = Median(bestCluster.Select(sample => sample.Scale));
        var clusterMad = Median(bestCluster.Select(sample =>
            Math.Abs(sample.Scale - clusterMedian) / clusterMedian));
        aggregate = new MapScaleAggregate(
            scale,
            bestCluster.Length,
            bestCluster.Average(sample => sample.Confidence),
            clusterMad,
            bestCluster.Min(sample => sample.CandidateMargin));
        return true;
    }

    private static double ClusterSpread(IReadOnlyCollection<MapScaleSample> cluster)
    {
        if (cluster.Count == 0)
            return double.PositiveInfinity;
        var median = Median(cluster.Select(sample => sample.Scale));
        return Median(cluster.Select(sample => Math.Abs(sample.Scale - median) / median));
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return double.PositiveInfinity;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }
}

public static class MapFeatureCacheRules
{
    public const int MinimumRepairValidationSamples =
        MapCacheRepairSampleAggregator.RequiredConsecutiveSamples;
    // 失败计数达到该值即视为"不可信任"，命中缓存时跳过该条目。
    public const int MaximumFailedValidationCountBeforeDistrust = 2;

    /// <summary>
    /// 缓存条目是否仍被无条件信任。失败计数达到门槛即降级，
    /// 即使 DirectlyTrusted（Manual/Player）也会降级——这正是错误玩家缩放被淘汰的前提。
    /// null Validation 视为可信（向后兼容无验证元数据的历史条目）。
    /// </summary>
    public static bool IsCacheEntryTrusted(MapFeatureCacheEntry? entry) =>
        entry is not null
        && entry.Scale is { } scale
        && scale.Validation is not
        {
            FailedValidationCount: >= MaximumFailedValidationCountBeforeDistrust
        };

    /// <summary>
    /// 记录一次缓存验证结果。succeeded=true 且存在失败历史时重置失败计数
    /// （正向证据恢复信任）；succeeded=false 时失败计数 +1。返回 null 表示无需落盘
    /// （成功且无失败历史，快乐路径零写）。
    /// </summary>
    public static MapScaleCacheValidationMetadata? RecordValidationOutcome(
        MapScaleCacheValidationMetadata? current,
        bool succeeded,
        DateTimeOffset validatedAt)
    {
        if (succeeded)
        {
            if (current is null || current.FailedValidationCount == 0)
                return null;
            return new MapScaleCacheValidationMetadata
            {
                DirectlyTrusted = current.DirectlyTrusted,
                SuccessfulValidationCount =
                    current.SuccessfulValidationCount + 1,
                FailedValidationCount = 0,
                LastLocalizationConfidence = current.LastLocalizationConfidence,
                LastCandidateMargin = current.LastCandidateMargin,
                LastValidatedAt = validatedAt
            };
        }

        return new MapScaleCacheValidationMetadata
        {
            DirectlyTrusted = current?.DirectlyTrusted ?? false,
            SuccessfulValidationCount = current?.SuccessfulValidationCount ?? 0,
            FailedValidationCount = (current?.FailedValidationCount ?? 0) + 1,
            LastLocalizationConfidence =
                current?.LastLocalizationConfidence ?? 0d,
            LastCandidateMargin = current?.LastCandidateMargin ?? 0d,
            LastValidatedAt = validatedAt
        };
    }

    /// <summary>
    /// 语义修正 C：三次一致修复完成时生成全新验证元数据，失败计数清零，
    /// 避免携带毒缓存的失败历史导致新 Recovery 条目立即被降级。
    /// </summary>
    public static MapScaleCacheValidationMetadata CreateRepairValidation(
        MapCacheRepairAggregate aggregate) =>
        new()
        {
            DirectlyTrusted = false,
            SuccessfulValidationCount = aggregate.SampleCount,
            FailedValidationCount = 0,
            LastLocalizationConfidence = aggregate.LocalizationConfidence,
            LastCandidateMargin = aggregate.CandidateMargin,
            LastValidatedAt = DateTimeOffset.UtcNow
        };

    public static double GetCandidateMargin(MapRecognitionResult result) =>
        result.EvidenceKind == MapAlignmentEvidenceKind.Structure
            ? result.StructureCandidateMargin
            : result.GeometryMargin;

    public static bool IsReliableLocalizationSample(
        MapRecognitionResult result,
        double minimumLocalizationConfidence,
        double minimumCandidateMargin)
    {
        ArgumentNullException.ThrowIfNull(result);
        var confidence = result.LocalizationConfidence;
        var margin = GetCandidateMargin(result);
        return double.IsFinite(confidence)
            && confidence >= Math.Clamp(minimumLocalizationConfidence, 0d, 1d)
            && double.IsFinite(margin)
            && margin >= Math.Max(0d, minimumCandidateMargin);
    }

    public static bool CanReplaceExistingEntry(
        MapFeatureCacheEntry? existing,
        MapFeatureCacheEntry replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var existingSource = existing?.Scale.Source;
        // Manual and Player entries are directly trusted bindings: an
        // automatic or recovery entry may only displace them after three
        // consistent recovery samples accumulate.
        if (existingSource is not (MapFeatureCacheSource.Manual
            or MapFeatureCacheSource.Player))
        {
            return true;
        }

        var validation = replacement.Scale.Validation;
        return replacement.Scale.Source == MapFeatureCacheSource.Recovery
            && replacement.Scale.SampleCount >= MinimumRepairValidationSamples
            && replacement.Scale.RelativeMedianAbsoluteDeviation
                <= MapScaleSampleAggregator.MaximumRelativeTolerance
            && validation is
            {
                SuccessfulValidationCount:
                    >= MinimumRepairValidationSamples
            };
    }

    public static string ComputeContentFingerprint(MapRecord map)
    {
        var builder = new StringBuilder()
            .Append(map.Id.ToString("N")).Append('|')
            .Append(map.ContentVersion).Append('|')
            .Append(map.UpdatedAt.UtcTicks);
        foreach (var floor in MapFloorRules.GetOrderedFloors(map))
        {
            builder.Append('|').Append(floor.Key)
                .Append('|').Append(floor.ImageSha256)
                .Append('|').Append(floor.ImageWidth).Append('x').Append(floor.ImageHeight)
                .Append('|').Append(floor.RecognitionSha256)
                .Append('|').Append(floor.RecognitionWidth).Append('x').Append(floor.RecognitionHeight)
                .Append('|').Append(floor.OverlaySha256)
                .Append('|').Append(floor.OverlayWidth).Append('x').Append(floor.OverlayHeight);
        }
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    public static MapFeatureCacheKey CreateKey(
        MapRecord map,
        string floorKey,
        MapCacheResolutionSignature resolution) =>
        new(map.Id, ComputeContentFingerprint(map), floorKey, resolution);

    public static MapOverlayTransform CreateScaleSeed(
        MapRecord map,
        string floorKey,
        double uniformScale)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey)
            ?? throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        var width = Math.Max(1, profile.RecognitionPixelWidth);
        var height = Math.Max(1, profile.RecognitionPixelHeight);
        return new MapOverlayTransform
        {
            ScaleX = uniformScale,
            ScaleY = uniformScale,
            OffsetX = 0d,
            OffsetY = 0d,
            ReferenceCenterX = width / 2d,
            ReferenceCenterY = height / 2d,
            ScreenCenterX = width * uniformScale / 2d,
            ScreenCenterY = height * uniformScale / 2d,
            ReferenceWidth = width,
            ReferenceHeight = height,
            OrientationDegrees = profile.OrientationDegrees,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
    }
}
/*
 * 文件职责：MapFeatureCacheModels。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
