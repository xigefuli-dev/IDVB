using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Controls whether live structure preprocessing also computes local feature
/// descriptors. Edge-only registration paths do not consume descriptors and
/// should avoid paying the AKAZE extraction cost.
/// </summary>
public enum MapStructurePreprocessingProfile
{
    EdgesOnly,
    EdgesAndFeatures
}

internal static class MapStructurePreprocessingProfileExtensions
{
    public static bool IncludesDescriptors(
        this MapStructurePreprocessingProfile profile) =>
        profile == MapStructurePreprocessingProfile.EdgesAndFeatures;

    public static bool CanSatisfy(
        this MapStructurePreprocessingProfile available,
        MapStructurePreprocessingProfile requested) =>
        available.Satisfies(requested);

    public static bool Satisfies(
        this MapStructurePreprocessingProfile available,
        MapStructurePreprocessingProfile requested) =>
        available == requested
        || (available == MapStructurePreprocessingProfile.EdgesAndFeatures
            && requested == MapStructurePreprocessingProfile.EdgesOnly);
}

/// <summary>
/// Converts annotated reference maps and live explored-map ROIs into comparable
/// positive structure evidence. Missing live pixels remain unknown evidence.
/// </summary>
public sealed partial class MapStructurePreprocessor
{
    public const int AlgorithmVersion = 5;

    private static readonly object _cacheGate = new();
    private static string? _cachedReferencePath;
    private static MapStructureFeatures? _cachedReferenceFeatures;

    public static void ClearReferenceCache()
    {
        lock (_cacheGate)
        {
            _cachedReferenceFeatures?.Dispose();
            _cachedReferenceFeatures = null;
            _cachedReferencePath = null;
        }
    }

    public MapStructureFeatures Process(Mat source)
    {
        var timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            timing,
            useOrb: false,
            profile: MapStructurePreprocessingProfile.EdgesAndFeatures);
    }

    public MapStructureFeatures ProcessOrb(Mat source)
    {
        var timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            timing,
            useOrb: true,
            profile: MapStructurePreprocessingProfile.EdgesAndFeatures);
    }

    public MapStructureFeatures ProcessCachedReference(
        Mat source,
        string? referencePath,
        out PreprocessTiming timing,
        out bool cacheHit)
    {
        timing = new PreprocessTiming();
        cacheHit = false;
        if (referencePath is not null)
        {
            lock (_cacheGate)
            {
                if (string.Equals(
                    _cachedReferencePath,
                    referencePath,
                    StringComparison.Ordinal))
                {
                    var existing = _cachedReferenceFeatures;
                    if (existing is not null
                        && !existing.Edges.Empty())
                    {
                        cacheHit = true;
                        // Return a clone so the caller can safely Dispose
                        // without invalidating the cached instance.
                        return existing.Clone();
                    }
                }
                _cachedReferenceFeatures?.Dispose();
                _cachedReferenceFeatures = null;
                _cachedReferencePath = null;
            }
        }

        var result = ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            timing,
            useOrb: true,
            profile: MapStructurePreprocessingProfile.EdgesAndFeatures);
        if (referencePath is not null)
        {
            lock (_cacheGate)
            {
                _cachedReferenceFeatures?.Dispose();
                _cachedReferenceFeatures = result;
                _cachedReferencePath = referencePath;
            }
            // Return a clone so the caller owns their copy and can
            // Dispose it independently. The cache keeps the original.
            return result.Clone();
        }
        // No caching path — ownership transfers directly to the caller.
        return result;
    }

    public MapStructureFeatures ProcessReference(
        Mat source,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions) =>
        ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions,
            dynamicIgnoreRegions: null,
            new PreprocessTiming(),
            useOrb: false,
            profile: MapStructurePreprocessingProfile.EdgesAndFeatures);

    /// <summary>
    /// Processes a calibrated live ROI and removes detached fixed UI elements
    /// before its non-zero bounds are used as the registration template.
    /// </summary>
    public MapStructureFeatures ProcessLiveRoi(Mat source) =>
        ProcessLiveRoi(source, null, null);

    public MapStructureFeatures ProcessLiveRoi(
        Mat source,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions,
        IReadOnlyList<Rect>? dynamicIgnoreRegions,
        bool generateVisibleMask = false,
        MapStructurePreprocessingProfile profile =
            MapStructurePreprocessingProfile.EdgesAndFeatures)
    {
        var timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: true,
            ignoreRegions,
            dynamicIgnoreRegions,
            timing,
            // The immutable reference cache uses AKAZE/MLDB. Structure feature
            // voting requires identical descriptor type and width, so the
            // regular live path must use the same extractor.
            useOrb: false,
            generateVisibleMask: generateVisibleMask,
            profile: profile);
    }

    /// <summary>
    /// Produces the same cleaned live structure as ProcessLiveRoi while using
    /// AKAZE descriptors compatible with the immutable reference cache. VPSG
    /// consumes this path before any translation is known.
    /// </summary>
    public MapStructureFeatures ProcessLiveRoiAkaze(
        Mat source,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions = null,
        IReadOnlyList<Rect>? dynamicIgnoreRegions = null) =>
        ProcessCore(
            source,
            retainDominantStructureCluster: true,
            ignoreRegions,
            dynamicIgnoreRegions,
            new PreprocessTiming(),
            useOrb: false,
            profile: MapStructurePreprocessingProfile.EdgesAndFeatures);

    public MapStructureFeatures ProcessLiveRoiDiagnostic(
        Mat source,
        out PreprocessTiming timing,
        MapStructurePreprocessingProfile profile =
            MapStructurePreprocessingProfile.EdgesAndFeatures) =>
        ProcessLiveRoiDiagnostic(
            source,
            null,
            null,
            out timing,
            profile: profile);

    public MapStructureFeatures ProcessLiveRoiDiagnostic(
        Mat source,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions,
        IReadOnlyList<Rect>? dynamicIgnoreRegions,
        out PreprocessTiming timing,
        bool generateVisibleMask = false,
        MapStructurePreprocessingProfile profile =
            MapStructurePreprocessingProfile.EdgesAndFeatures)
    {
        timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: true,
            ignoreRegions,
            dynamicIgnoreRegions,
            timing,
            useOrb: false,
            generateVisibleMask: generateVisibleMask,
            profile: profile);
    }

    /// <summary>
    /// Upgrades a cached edge-only live extraction by computing only the
    /// AKAZE keypoints and descriptors. The expensive thresholding,
    /// connected-component cleanup and edge construction remain reusable.
    /// </summary>
    internal static MapStructureFeatures UpgradeLiveRoiWithDescriptors(
        MapStructureFeatures edgesOnly,
        out PreprocessTiming timing)
    {
        ArgumentNullException.ThrowIfNull(edgesOnly);
        if (edgesOnly.NormalizedGray.Empty()
            || edgesOnly.NuisanceMask.Empty()
            || edgesOnly.Edges.Empty())
        {
            throw new ArgumentException(
                "The cached live structure does not contain an upgradeable base extraction.",
                nameof(edgesOnly));
        }

        timing = edgesOnly.DiagnosticTiming?.Clone()
            ?? new PreprocessTiming
            {
                Profile = MapStructurePreprocessingProfile.EdgesOnly,
                DescriptorExtractionSkipped = true
            };
        var previousFeatureMilliseconds = timing.FeaturesMs;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var descriptors = new Mat();
        try
        {
            DetectFeatures(
                edgesOnly.NormalizedGray,
                edgesOnly.NuisanceMask,
                useOrb: false,
                descriptors,
                out var keyPoints);
            stopwatch.Stop();
            timing.Profile =
                MapStructurePreprocessingProfile.EdgesAndFeatures;
            timing.DescriptorExtractionSkipped = false;
            timing.FeaturesMs = stopwatch.Elapsed.TotalMilliseconds;
            timing.TotalMs = Math.Max(
                    0d,
                    timing.TotalMs - previousFeatureMilliseconds)
                + timing.FeaturesMs;

            return new MapStructureFeatures(
                edgesOnly.NuisanceMask.Clone(),
                edgesOnly.StructureMask.Clone(),
                edgesOnly.Edges.Clone(),
                edgesOnly.ReferenceDistanceMap?.Clone(),
                edgesOnly.ClippedReferenceDistanceMap?.Clone(),
                edgesOnly.ClippedDistancePixels,
                edgesOnly.NormalizedGray.Clone(),
                edgesOnly.EdgePyramid.Select(level => level.Clone()).ToArray(),
                keyPoints,
                descriptors,
                edgesOnly.RepeatedRegionMask.Clone(),
                timing,
                edgesOnly.RawVisibleMask?.Clone());
        }
        catch
        {
            descriptors.Dispose();
            throw;
        }
    }

}
