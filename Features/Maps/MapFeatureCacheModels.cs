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
            (1920, 1080) or (2560, 1440) or (2560, 1600);

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
