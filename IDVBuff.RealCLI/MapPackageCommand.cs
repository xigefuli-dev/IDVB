using System.Text.Json;
using System.Text.Json.Serialization;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.RealCLI;

/// <summary>Creates and round-trips an author map class through production IDVM APIs.</summary>
internal static partial class MapPackageCommand
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var options = ParseArguments(args);
            var manifestPath = Path.GetFullPath(options.ManifestPath);
            Require(File.Exists(manifestPath), $"Manifest does not exist: {manifestPath}");
            var outputRoot = Path.GetFullPath(options.OutputRoot);
            Require(!File.Exists(outputRoot) && !Directory.Exists(outputRoot),
                $"Output root must be a fresh path that does not already exist: {outputRoot}");

            var input = JsonSerializer.Deserialize<PackagePlan>(
                await File.ReadAllTextAsync(manifestPath), ManifestJsonOptions)
                ?? throw new InvalidDataException("Manifest is empty or invalid.");
            var plan = PreparePlan(input, manifestPath);
            var sourcePackagePath = ResolveInputPath(input.SourcePackage!, manifestPath, "sourcePackage");
            Require(File.Exists(sourcePackagePath) || Directory.Exists(sourcePackagePath),
                $"Canonical source IDVM does not exist: {sourcePackagePath}");
            if (Directory.Exists(sourcePackagePath))
                Require(!IsSameOrWithin(outputRoot, sourcePackagePath),
                    "Output root must be outside the canonical source package directory.");

            Directory.CreateDirectory(outputRoot);
            var summary = await CreatePackageAsync(plan, sourcePackagePath, outputRoot);
            var json = JsonSerializer.Serialize(summary, OutputJsonOptions);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "summary.json"), json);
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"map-package failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<MapPackageSummary> CreatePackageAsync(
        PreparedPlan plan, string sourcePackagePath, string outputRoot)
    {
        var seedRoot = Path.Combine(outputRoot, "seed");
        var seedRepository = new MapRepository(Path.Combine(seedRoot, "Maps"));
        var seedPackages = new IdvmPackageService(
            seedRepository, new MapTagStore(Path.Combine(seedRoot, "map-tags.json")));

        IdvmImportResult sourceImport;
        await using (var sourcePlan = await seedPackages.InspectAsync(sourcePackagePath))
            sourceImport = await seedPackages.ImportAsync(sourcePlan);

        var importedSnapshot = await seedRepository.GetCatalogSnapshotAsync();
        Require(!importedSnapshot.Classes.Any(candidate => string.Equals(
                candidate, plan.MapClass, StringComparison.OrdinalIgnoreCase)),
            $"The canonical IDVM already contains mapClass '{plan.MapClass}'. Choose a new author class.");
        var mapClass = await seedRepository.CreateClassAsync(plan.MapClass);
        var references = ResolveReferences(plan.Entries, importedSnapshot.Maps);
        var authorSummaries = new List<AuthorPackageSummary>(plan.Entries.Count);

        foreach (var entry in plan.Entries)
        {
            using var artwork = DecodeImage(entry.SourceImage, $"{entry.Title} sourceImage");
            var draft = CreateDraft(entry, mapClass);
            var floorSummaries = new List<FloorPackageSummary>(entry.Floors.Count);

            foreach (var floor in entry.Floors)
            {
                var reference = references[entry.SourceLayout];
                var referenceFloor = reference.Floors.SingleOrDefault(item => item.Key == floor.Key)
                    ?? throw new InvalidDataException(
                        $"{entry.SourceLayout}: canonical floor '{floor.Key}' is missing.");
                Require(referenceFloor.SharedStructure?.Source?.LayoutId == entry.SourceLayout,
                    $"{entry.SourceLayout}/{floor.Key}: canonical floor does not own the requested sourceLayout.");
                Require(referenceFloor.PrebuiltStructureLine is { IsCurrent: true, IsComplete: true },
                    $"{entry.SourceLayout}/{floor.Key}: current prebuilt structure is missing.");

                var canonicalPath = seedRepository.GetFloorRecognitionPath(reference, floor.Key);
                var prebuiltPath = seedRepository.GetPrebuiltStructureLinePath(reference, floor.Key);
                var algorithmPath = seedRepository.GetPrebuiltStructureAlgorithmPath(reference, floor.Key);
                Require(File.Exists(algorithmPath),
                    $"{entry.SourceLayout}/{floor.Key}: prebuilt structure algorithm file is missing.");
                using var canonical = DecodeImage(canonicalPath,
                    $"{entry.SourceLayout}/{floor.Key} canonical recognition image");
                ValidateLandmarks(floor.Training, artwork.Width, artwork.Height,
                    canonical.Width, canonical.Height, $"{entry.Title}/{floor.Key} training");
                ValidateLandmarks(floor.HeldOut, artwork.Width, artwork.Height,
                    canonical.Width, canonical.Height, $"{entry.Title}/{floor.Key} heldOut");

                var registration = MapArtworkRegistrationService.Fit(
                    artwork.Width, artwork.Height, canonical.Width, canonical.Height,
                    floor.Training, floor.FitMethod);
                registration.ClipToStructureFootprint = floor.ClipToStructureFootprint;
                var trainingResiduals = MapArtworkRegistrationService.EvaluateResiduals(registration, floor.Training);
                var heldOutResiduals = MapArtworkRegistrationService.EvaluateResiduals(registration, floor.HeldOut);
                var checkedResidual = floor.CalibrationOnly ? trainingResiduals.Max() : heldOutResiduals.Max();
                Require(checkedResidual <= floor.MaximumResidual,
                    $"{entry.Title}/{floor.Key}: {(floor.CalibrationOnly ? "calibration" : "held-out")} residual {checkedResidual:F2}px exceeded maximumResidual {floor.MaximumResidual:F2}px.");

                var sourceProfile = draft.Recognition.GetFloor(floor.Key)
                    ?? throw new InvalidOperationException($"{entry.Title}/{floor.Key}: source crop profile is missing.");
                registration.SetSourceCrop(sourceProfile);
                draft.FloorPaths[floor.Key] = entry.SourceImage;
                await seedRepository.ReuseFloorStructureAsync(
                    draft, floor.Key, reference.Id, floor.Key, registration);
                floorSummaries.Add(new FloorPackageSummary(
                    floor.Key, floor.FitMethod, artwork.Width, artwork.Height,
                    canonical.Width, canonical.Height, (double[])registration.SourceToReference.Clone(),
                    registration.SourceCropRegion?.Clone(),
                    registration.SourceCropPoints.Select(point => point.Clone()).ToArray(),
                    trainingResiduals.Max(), heldOutResiduals.Length == 0 ? null : heldOutResiduals.Max(),
                    checkedResidual));

                // Resolve these paths before saving so missing binary resources fail closed.
                Require(File.Exists(prebuiltPath) && File.Exists(algorithmPath),
                    $"{entry.SourceLayout}/{floor.Key}: canonical binary structure resources are incomplete.");
            }

            VerifyDraftFloorCompatibility(draft, entry);
            var saved = await seedRepository.SaveAsync(draft);
            var reopened = await seedRepository.CreateDraftAsync(saved.Id)
                ?? throw new InvalidOperationException($"Could not reopen saved map '{entry.Title}'.");
            var savedAgain = await seedRepository.SaveAsync(reopened);
            await seedRepository.EnsureDerivedAssetsAsync([savedAgain]);
            authorSummaries.Add(new AuthorPackageSummary(
                entry.Title, entry.SourceLayout, entry.Floors.Select(floor => floor.Key).ToArray(), floorSummaries));
        }

        var seedSnapshot = await seedRepository.GetCatalogSnapshotAsync();
        var authoredMaps = seedSnapshot.Maps
            .Where(map => string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase))
            .OrderBy(map => map.SequenceNumber).ToArray();
        Require(authoredMaps.Length == plan.Entries.Count,
            $"Production save returned {authoredMaps.Length} maps; expected {plan.Entries.Count}.");
        foreach (var entry in plan.Entries)
        {
            var map = authoredMaps.SingleOrDefault(candidate => candidate.Title == entry.Title)
                ?? throw new InvalidOperationException($"Saved map '{entry.Title}' is missing from the catalog.");
            VerifySavedMap(seedRepository, map, entry, mapClass);
        }

        foreach (var group in plan.VariantGroups)
        {
            var ids = group.Select(layout => authoredMaps.Single(map => map.Title ==
                plan.Entries.Single(entry => entry.SourceLayout == layout).Title).Id).ToArray();
            var change = await seedRepository.ToggleVariantGroupAsync(mapClass, ids);
            Require(change.Kind == MapVariantGroupChangeKind.Bound,
                "Production variant binding did not create the requested group.");
        }

        var packagePath = Path.Combine(outputRoot, "map-package.idvm");
        await seedPackages.ExportAsync(IdvmExportScope.CurrentClass, mapClass, packagePath);
        Require(File.Exists(packagePath) && new FileInfo(packagePath).Length > 0,
            "IDVM export did not create a non-empty package.");

        var roundTripRoot = Path.Combine(outputRoot, "roundtrip");
        var roundTripRepository = new MapRepository(Path.Combine(roundTripRoot, "Maps"));
        var roundTripPackages = new IdvmPackageService(
            roundTripRepository, new MapTagStore(Path.Combine(roundTripRoot, "map-tags.json")));
        IdvmImportResult roundTripImport;
        await using (var exportedPlan = await roundTripPackages.InspectAsync(packagePath))
        {
            Require(exportedPlan.ClassCount == 1 && exportedPlan.MapCount == plan.Entries.Count,
                $"Exported IDVM contains {exportedPlan.ClassCount} classes and {exportedPlan.MapCount} maps; expected one class and {plan.Entries.Count} maps.");
            roundTripImport = await roundTripPackages.ImportAsync(exportedPlan);
        }
        Require(roundTripImport.ImportedMaps.Count == plan.Entries.Count
            && roundTripImport.CreatedClasses.Count == 1
            && string.Equals(roundTripImport.CreatedClasses[0], mapClass, StringComparison.OrdinalIgnoreCase),
            "Exported IDVM round-trip did not restore exactly the author class and maps.");
        await roundTripRepository.EnsureDerivedAssetsAsync(roundTripImport.ImportedMaps);
        var importedSnapshotAfterRoundTrip = await roundTripRepository.GetCatalogSnapshotAsync();
        var roundTripMaps = importedSnapshotAfterRoundTrip.Maps
            .Where(map => string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(map => map.Title, StringComparer.Ordinal);

        foreach (var entry in plan.Entries)
        {
            var sourceMap = authoredMaps.Single(map => map.Title == entry.Title);
            var importedMap = roundTripMaps.GetValueOrDefault(entry.Title)
                ?? throw new InvalidDataException($"Round-trip map '{entry.Title}' is missing.");
            await VerifyRoundTripMapAsync(
                seedRepository, sourceMap, roundTripRepository, importedMap, entry);
        }
        Require(importedSnapshotAfterRoundTrip.VariantGroups.Count == plan.VariantGroups.Count,
            "IDVM round-trip changed the number of variant groups.");
        foreach (var group in plan.VariantGroups)
        {
            var ids = group.Select(layout => roundTripMaps[
                plan.Entries.Single(entry => entry.SourceLayout == layout).Title].Id).ToHashSet();
            Require(importedSnapshotAfterRoundTrip.VariantGroups.Any(imported =>
                    imported.MapIds.Count == ids.Count && imported.MapIds.All(ids.Contains)),
                "IDVM round-trip changed variant group membership.");
        }

        return new MapPackageSummary(
            true, mapClass, sourcePackagePath, packagePath, sourceImport.ImportedMaps.Count,
            authoredMaps.Length, authoredMaps.Sum(map => MapFloorRules.GetOrderedFloors(map).Count),
            authorSummaries, "export-inspect-import", false, false);
    }
}
