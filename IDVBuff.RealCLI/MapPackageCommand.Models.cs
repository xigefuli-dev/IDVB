using IDVBuff.Features.Maps;

namespace IDVBuff.RealCLI;

internal static partial class MapPackageCommand
{
    private sealed class PackagePlan
    {
        public string? MapClass { get; set; }
        public string? SourcePackage { get; set; }
        public List<EntryInput>? Entries { get; set; }
        public List<List<string>>? VariantGroups { get; set; }
    }

    private sealed class EntryInput
    {
        public string? Title { get; set; }
        public string? SourceLayout { get; set; }
        public string? SourceImage { get; set; }
        public List<FloorInput>? Floors { get; set; }
    }

    private sealed class FloorInput
    {
        public string? Key { get; set; }
        public RectangleInput? Crop { get; set; }
        public List<PointInput>? CropPoints { get; set; }
        public List<LandmarkInput>? Training { get; set; }
        public List<LandmarkInput>? HeldOut { get; set; }
        public double? MaximumResidual { get; set; }
        public bool? ClipToStructureFootprint { get; set; }
        public MapArtworkFitMethod? FitMethod { get; set; }
        public bool? CalibrationOnly { get; set; }
    }

    private sealed class RectangleInput
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private sealed class PointInput
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    private sealed class LandmarkInput
    {
        public double SourceX { get; set; }
        public double SourceY { get; set; }
        public double ReferenceX { get; set; }
        public double ReferenceY { get; set; }
    }

    private sealed record CommandOptions(string ManifestPath, string OutputRoot);
    private sealed record PreparedPlan(string MapClass, IReadOnlyList<PreparedEntry> Entries,
        IReadOnlyList<IReadOnlyList<string>> VariantGroups);
    private sealed record PreparedEntry(
        string Title, string SourceLayout, string SourceImage, IReadOnlyList<PreparedFloor> Floors);
    private sealed record PreparedFloor(
        string Key, NormalizedRectangle Crop, IReadOnlyList<NormalizedPoint> CropPoints,
        IReadOnlyList<MapArtworkLandmark> Training, IReadOnlyList<MapArtworkLandmark> HeldOut,
        double MaximumResidual, bool ClipToStructureFootprint,
        MapArtworkFitMethod FitMethod, bool CalibrationOnly);

    private sealed record FloorPackageSummary(
        string FloorKey, MapArtworkFitMethod FitMethod,
        int SourceWidth, int SourceHeight, int ReferenceWidth, int ReferenceHeight,
        double[] SourceToReference, NormalizedRectangle? SourceCropRegion,
        NormalizedPoint[] SourceCropPoints, double MaximumTrainingResidual,
        double? MaximumHeldOutResidual, double CheckedResidual);

    private sealed record AuthorPackageSummary(
        string Title, string SourceLayout, string[] FloorKeys,
        IReadOnlyList<FloorPackageSummary> Floors);

    private sealed record MapPackageSummary(
        bool Succeeded, string MapClass, string SourcePackage, string PackagePath,
        int ImportedCanonicalMapCount, int CreatedAuthorMapCount, int CreatedFloorCount,
        IReadOnlyList<AuthorPackageSummary> Authors, string RoundTrip,
        bool RecognitionReplayCompleted, bool RealGameAcceptance);
}
