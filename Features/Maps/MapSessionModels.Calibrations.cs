using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>V6: reads Floor from JSON as either int (1→"1f", 2→"2f") or string.</summary>
internal sealed class FloorKeyJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32() switch
            {
                1 => "1f",
                2 => "2f",
                _ => reader.GetInt32().ToString()
            },
            JsonTokenType.String => reader.GetString(),
            _ => null
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

public sealed class MapAlignmentCalibration
{
    public Guid MapId { get; set; }

    [JsonConverter(typeof(FloorKeyJsonConverter))]
    public string Floor { get; set; } = "1f";
    public DateTimeOffset MapUpdatedAt { get; set; }
    public int ReferenceWidth { get; set; }
    public int ReferenceHeight { get; set; }
    public double UniformScale { get; set; }
    public double RotationDegrees { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public int ViewportWidth { get; set; }
    public int ViewportHeight { get; set; }
    public uint Dpi { get; set; } = 96;
    public double Confidence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        MapId != Guid.Empty
        && ReferenceWidth > 0
        && ReferenceHeight > 0
        && double.IsFinite(UniformScale)
        && UniformScale > 0d
        && double.IsFinite(RotationDegrees)
        && ClientWidth > 0
        && ClientHeight > 0
        && ViewportWidth > 0
        && ViewportHeight > 0
        && Dpi > 0;

    public bool Matches(
        Guid mapId,
        DateTimeOffset mapUpdatedAt,
        MapWindowSignature signature,
        string floor = "1f") =>
        IsValid
        && Floor == floor
        && MapId == mapId
        && MapUpdatedAt == mapUpdatedAt
        && ClientWidth == signature.ClientWidth
        && ClientHeight == signature.ClientHeight
        && ViewportWidth == signature.ViewportWidth
        && ViewportHeight == signature.ViewportHeight;

    public MapAlignmentCalibration Clone() => (MapAlignmentCalibration)MemberwiseClone();
}

/// <summary>
/// A floor-specific scale relationship learned from trusted alignments.  It is
/// intentionally independent of neighbouring floors and invalidates naturally
/// whenever the map package changes.
/// </summary>
public sealed class MapFloorScaleCalibration
{
    public Guid MapId { get; set; }
    public DateTimeOffset MapUpdatedAt { get; set; }
    public string PrimaryFloorKey { get; set; } = string.Empty;
    public string FloorKey { get; set; } = string.Empty;
    public List<double> RecentTrustedRatios { get; set; } = [];
    public int TotalSampleCount { get; set; }
    public double MedianRatio { get; set; }
    /// <summary>Median absolute deviation expressed relative to the median.</summary>
    public double MedianAbsoluteDeviation { get; set; }
    public double LastConfidence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        MapId != Guid.Empty
        && MapUpdatedAt != default
        && !string.IsNullOrWhiteSpace(PrimaryFloorKey)
        && !string.IsNullOrWhiteSpace(FloorKey)
        && !string.Equals(PrimaryFloorKey, FloorKey, StringComparison.Ordinal)
        && RecentTrustedRatios is { Count: > 0 }
        && RecentTrustedRatios.All(IsValidRatio)
        && IsValidRatio(MedianRatio)
        && double.IsFinite(MedianAbsoluteDeviation)
        && MedianAbsoluteDeviation >= 0d;

    public bool Matches(
        Guid mapId,
        DateTimeOffset mapUpdatedAt,
        string primaryFloorKey,
        string floorKey) =>
        IsValid
        && MapId == mapId
        && MapUpdatedAt == mapUpdatedAt
        && string.Equals(PrimaryFloorKey, primaryFloorKey, StringComparison.Ordinal)
        && string.Equals(FloorKey, floorKey, StringComparison.Ordinal);

    public bool TryAddTrustedSample(
        double ratio,
        double confidence,
        DateTimeOffset observedAt,
        out string? rejectionReason)
    {
        rejectionReason = null;
        Normalize();
        if (!IsValidRatio(ratio))
        {
            rejectionReason = "invalid-scale-ratio";
            return false;
        }

        if (RecentTrustedRatios.Count >= 3)
        {
            var relativeDeviation = Math.Abs(ratio - MedianRatio) / MedianRatio;
            var threshold = Math.Max(0.08d, 3d * MedianAbsoluteDeviation);
            if (relativeDeviation > threshold)
            {
                rejectionReason =
                    $"ratio-outlier:{relativeDeviation:F6}>{threshold:F6}";
                return false;
            }
        }

        RecentTrustedRatios.Add(ratio);
        if (RecentTrustedRatios.Count > 7)
            RecentTrustedRatios.RemoveRange(0, RecentTrustedRatios.Count - 7);
        TotalSampleCount = Math.Max(TotalSampleCount, 0) + 1;
        LastConfidence = double.IsFinite(confidence)
            ? Math.Clamp(confidence, 0d, 1d)
            : 0d;
        UpdatedAt = observedAt;
        RecalculateStatistics();
        return true;
    }

    public void Normalize()
    {
        RecentTrustedRatios ??= [];
        RecentTrustedRatios = RecentTrustedRatios
            .Where(IsValidRatio)
            .TakeLast(7)
            .ToList();
        TotalSampleCount = Math.Max(TotalSampleCount, RecentTrustedRatios.Count);
        LastConfidence = double.IsFinite(LastConfidence)
            ? Math.Clamp(LastConfidence, 0d, 1d)
            : 0d;
        RecalculateStatistics();
    }

    public MapFloorScaleCalibration Clone() => new()
    {
        MapId = MapId,
        MapUpdatedAt = MapUpdatedAt,
        PrimaryFloorKey = PrimaryFloorKey,
        FloorKey = FloorKey,
        RecentTrustedRatios = [.. RecentTrustedRatios],
        TotalSampleCount = TotalSampleCount,
        MedianRatio = MedianRatio,
        MedianAbsoluteDeviation = MedianAbsoluteDeviation,
        LastConfidence = LastConfidence,
        UpdatedAt = UpdatedAt
    };

    private void RecalculateStatistics()
    {
        if (RecentTrustedRatios.Count == 0)
        {
            MedianRatio = 0d;
            MedianAbsoluteDeviation = 0d;
            return;
        }

        MedianRatio = Median(RecentTrustedRatios);
        MedianAbsoluteDeviation = Median(
            RecentTrustedRatios.Select(value =>
                Math.Abs(value - MedianRatio) / MedianRatio));
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static bool IsValidRatio(double value) =>
        double.IsFinite(value) && value > 0d;
}

public static class MapFloorScaleSearchPolicy
{
    public const double UncalibratedMinimumScale = 0.30d;
    public const double UncalibratedMaximumScale = 1.70d;

    public static (double InitialRadius, double ExpandedRadius) GetRadii(
        bool hasFloorCalibration) =>
        hasFloorCalibration
            ? (0.04d, 0.15d)
            : (0.15d, 0.70d);
}
/*
 * 文件职责：MapSessionModels.Calibrations。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
