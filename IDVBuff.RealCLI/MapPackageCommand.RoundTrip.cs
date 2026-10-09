using System.Text.Json;
using IDVBuff.Features.Maps;

namespace IDVBuff.RealCLI;

internal static partial class MapPackageCommand
{
    private static async Task VerifyRoundTripMapAsync(
        MapRepository sourceRepository,
        MapRecord sourceMap,
        MapRepository roundTripRepository,
        MapRecord importedMap,
        PreparedEntry entry)
    {
        var expectedFloorKeys = entry.Floors.Select(floor => floor.Key).ToArray();
        var actualFloorKeys = MapFloorRules.GetOrderedFloors(importedMap).Select(floor => floor.Key).ToArray();
        Require(sourceMap.Title == importedMap.Title && sourceMap.Class == importedMap.Class
            && actualFloorKeys.SequenceEqual(expectedFloorKeys, StringComparer.Ordinal),
            $"{entry.Title}: IDVM round-trip changed identity or floor order.");

        foreach (var floor in entry.Floors)
        {
            var sourceFloor = sourceMap.Floors.Single(item => item.Key == floor.Key);
            var importedFloor = importedMap.Floors.Single(item => item.Key == floor.Key);
            Require(JsonSerializer.Serialize(sourceFloor.SharedStructure?.Source)
                    == JsonSerializer.Serialize(importedFloor.SharedStructure?.Source)
                && sourceFloor.SharedStructure?.Id == importedFloor.SharedStructure?.Id
                && sourceFloor.SharedStructure?.Revision == importedFloor.SharedStructure?.Revision,
                $"{entry.Title}/{floor.Key}: IDVM round-trip changed canonical structure provenance.");
            Require(PrebuiltMetadataMatches(sourceFloor.PrebuiltStructureLine,
                    importedFloor.PrebuiltStructureLine),
                $"{entry.Title}/{floor.Key}: IDVM round-trip lost the current prebuilt structure metadata.");
            Require(RegistrationMatches(sourceFloor.ArtworkRegistration, importedFloor.ArtworkRegistration),
                $"{entry.Title}/{floor.Key}: IDVM round-trip changed artwork fit or crop metadata.");

            await RequireFilesEqualAsync(
                sourceRepository.GetFloorImagePath(sourceMap, floor.Key),
                roundTripRepository.GetFloorImagePath(importedMap, floor.Key),
                $"{entry.Title}/{floor.Key}: IDVM round-trip changed source artwork bytes.");
            await RequireFilesEqualAsync(
                sourceRepository.GetFloorRecognitionPath(sourceMap, floor.Key),
                roundTripRepository.GetFloorRecognitionPath(importedMap, floor.Key),
                $"{entry.Title}/{floor.Key}: IDVM round-trip changed canonical recognition bytes.");
            await RequireFilesEqualAsync(
                sourceRepository.GetPrebuiltStructureLinePath(sourceMap, floor.Key),
                roundTripRepository.GetPrebuiltStructureLinePath(importedMap, floor.Key),
                $"{entry.Title}/{floor.Key}: IDVM round-trip changed prebuilt binary structure bytes.");
            await RequireFilesEqualAsync(
                sourceRepository.GetPrebuiltStructureAlgorithmPath(sourceMap, floor.Key),
                roundTripRepository.GetPrebuiltStructureAlgorithmPath(importedMap, floor.Key),
                $"{entry.Title}/{floor.Key}: IDVM round-trip changed prebuilt structure algorithm bytes.");
            await VerifySideDoorFeatureRoundTripAsync(
                sourceRepository, sourceMap, roundTripRepository, importedMap, floor.Key, entry.Title);
        }
    }

    private static bool PrebuiltMetadataMatches(
        PrebuiltStructureLineAsset? source, PrebuiltStructureLineAsset? imported) =>
        source is { IsCurrent: true, IsComplete: true }
        && imported is { IsCurrent: true, IsComplete: true }
        && source.Width == imported.Width
        && source.Height == imported.Height
        && source.FileLength == imported.FileLength
        && source.AlgorithmId == imported.AlgorithmId
        && source.AlgorithmSchemaVersion == imported.AlgorithmSchemaVersion
        && source.EngineRevision == imported.EngineRevision;

    private static bool RegistrationMatches(
        MapArtworkRegistration? source, MapArtworkRegistration? imported)
    {
        if (source is null || imported is null
            || source.SourceWidth != imported.SourceWidth
            || source.SourceHeight != imported.SourceHeight
            || source.ReferenceWidth != imported.ReferenceWidth
            || source.ReferenceHeight != imported.ReferenceHeight
            || source.FitMethod != imported.FitMethod
            || source.ClipToStructureFootprint != imported.ClipToStructureFootprint
            || source.SourceCropConfigured != imported.SourceCropConfigured
            || !source.SourceToReference.SequenceEqual(imported.SourceToReference)
            || source.Landmarks.Count != imported.Landmarks.Count
            || source.SourceCropPoints.Count != imported.SourceCropPoints.Count)
            return false;

        var sourceCrop = source.SourceCropRegion;
        var importedCrop = imported.SourceCropRegion;
        if (sourceCrop?.X != importedCrop?.X || sourceCrop?.Y != importedCrop?.Y
            || sourceCrop?.Width != importedCrop?.Width || sourceCrop?.Height != importedCrop?.Height)
            return false;
        return source.Landmarks.Zip(imported.Landmarks).All(pair =>
                pair.First.SourceX == pair.Second.SourceX
                && pair.First.SourceY == pair.Second.SourceY
                && pair.First.ReferenceX == pair.Second.ReferenceX
                && pair.First.ReferenceY == pair.Second.ReferenceY)
            && source.SourceCropPoints.Zip(imported.SourceCropPoints).All(pair =>
                pair.First.X == pair.Second.X && pair.First.Y == pair.Second.Y);
    }

    private static void VerifySideDoorFeatureIfPresent(
        MapRepository repository, MapRecord map, string floorKey, string mapTitle)
    {
        var profile = map.Recognition.GetFloor(floorKey);
        if (string.IsNullOrWhiteSpace(profile?.SideEntranceFeatureFileName))
            return;
        Require(repository.TryGetValidSideEntranceFeaturePath(map, floorKey, out _, out var reason),
            $"{mapTitle}/{floorKey}: side-door feature is not valid against the prebuilt binary ({reason}).");
    }

    private static async Task VerifySideDoorFeatureRoundTripAsync(
        MapRepository sourceRepository,
        MapRecord sourceMap,
        MapRepository roundTripRepository,
        MapRecord importedMap,
        string floorKey,
        string mapTitle)
    {
        var sourceProfile = sourceMap.Recognition.GetFloor(floorKey);
        var importedProfile = importedMap.Recognition.GetFloor(floorKey);
        var sourceHasFeature = !string.IsNullOrWhiteSpace(sourceProfile?.SideEntranceFeatureFileName);
        var importedHasFeature = !string.IsNullOrWhiteSpace(importedProfile?.SideEntranceFeatureFileName);
        if (!sourceHasFeature)
        {
            if (importedHasFeature)
                Require(roundTripRepository.TryGetValidSideEntranceFeaturePath(
                        importedMap, floorKey, out _, out var reason),
                    $"{mapTitle}/{floorKey}: imported side-door feature is invalid ({reason}).");
            return;
        }

        Require(importedHasFeature,
            $"{mapTitle}/{floorKey}: IDVM round-trip dropped the side-door feature.");
        if (!sourceRepository.TryGetValidSideEntranceFeaturePath(
                sourceMap, floorKey, out _, out var sourceReason))
            throw new InvalidDataException(
                $"{mapTitle}/{floorKey}: source side-door feature is invalid ({sourceReason}).");
        Require(roundTripRepository.TryGetValidSideEntranceFeaturePath(
                importedMap, floorKey, out _, out var importedReason),
            $"{mapTitle}/{floorKey}: imported side-door feature is invalid ({importedReason}).");
        await RequireFilesEqualAsync(
            sourceRepository.GetSideEntranceFeaturePath(sourceMap, floorKey),
            roundTripRepository.GetSideEntranceFeaturePath(importedMap, floorKey),
            $"{mapTitle}/{floorKey}: IDVM round-trip changed side-door feature bytes.");
    }

    private static async Task RequireFilesEqualAsync(string left, string right, string message)
    {
        var leftBytes = await File.ReadAllBytesAsync(left);
        var rightBytes = await File.ReadAllBytesAsync(right);
        Require(leftBytes.AsSpan().SequenceEqual(rightBytes), message);
    }
}
