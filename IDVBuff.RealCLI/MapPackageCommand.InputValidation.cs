using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.RealCLI;

internal static partial class MapPackageCommand
{
    private static PreparedPlan PreparePlan(PackagePlan input, string manifestPath)
    {
        var mapClass = input.MapClass?.Trim() ?? string.Empty;
        Require(!string.IsNullOrWhiteSpace(mapClass), "mapClass is required.");
        Require(!string.IsNullOrWhiteSpace(input.SourcePackage), "sourcePackage is required.");
        var inputEntries = input.Entries
            ?? throw new InvalidDataException("entries must explicitly contain at least one author map.");
        Require(inputEntries.Count > 0, "entries must explicitly contain at least one author map.");
        Require(inputEntries.All(entry => entry is not null), "entries must not contain null author maps.");

        var entries = inputEntries.Select((entry, index) => PrepareEntry(entry, index, manifestPath)).ToArray();
        Require(entries.Select(entry => entry.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Length,
            "Author map titles must be unique within mapClass.");
        var groups = new List<IReadOnlyList<string>>();
        var groupedLayouts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in input.VariantGroups ?? [])
        {
            Require(group is not null && group.Count >= 2, "variantGroups require at least two layouts.");
            foreach (var layout in group!)
            {
                Require(entries.Count(entry => entry.SourceLayout == layout) == 1,
                    $"variantGroups layout '{layout}' must identify exactly one author map.");
                Require(groupedLayouts.Add(layout), $"variantGroups layout '{layout}' is repeated.");
            }
            groups.Add(group!.ToArray());
        }
        Require(groups.Count <= MapVariantGroup.PaletteSize, "Too many variantGroups for one class.");
        return new PreparedPlan(mapClass, entries, groups);
    }

    private static PreparedEntry PrepareEntry(EntryInput input, int index, string manifestPath)
    {
        var title = input.Title?.Trim() ?? string.Empty;
        var sourceLayout = input.SourceLayout?.Trim() ?? string.Empty;
        Require(!string.IsNullOrWhiteSpace(title), $"entries[{index}].title is required.");
        Require(!string.IsNullOrWhiteSpace(sourceLayout), $"entries[{index}].sourceLayout is required.");
        Require(!string.IsNullOrWhiteSpace(input.SourceImage), $"entries[{index}].sourceImage is required.");
        var sourceImage = ResolveInputPath(input.SourceImage!, manifestPath, $"entries[{index}].sourceImage");
        Require(File.Exists(sourceImage), $"Source image does not exist: {sourceImage}");
        using var artwork = DecodeImage(sourceImage, $"entries[{index}].sourceImage");

        var inputFloors = input.Floors
            ?? throw new InvalidDataException($"entries[{index}].floors must explicitly list at least one floor.");
        Require(inputFloors.Count > 0, $"entries[{index}].floors must explicitly list at least one floor.");
        Require(inputFloors.All(floor => floor is not null),
            $"entries[{index}].floors must not contain null floor entries.");

        var floorKeys = new HashSet<string>(StringComparer.Ordinal);
        var floors = new List<PreparedFloor>(inputFloors.Count);
        foreach (var floor in inputFloors)
        {
            var key = floor.Key?.Trim() ?? string.Empty;
            Require(!string.IsNullOrWhiteSpace(key)
                && !key.Contains('/') && !key.Contains('\\')
                && string.Equals(Path.GetFileName(key), key, StringComparison.Ordinal),
                $"{title}: floor key is empty or unsafe.");
            Require(floorKeys.Add(key), $"{title}: duplicate floor key '{key}'.");
            Require(floor.Crop is not null, $"{title}/{key}: crop is required as the source artwork scope.");

            var trainingInput = floor.Training
                ?? throw new InvalidDataException($"{title}/{key}: training must be explicitly supplied.");
            var heldOutInput = floor.HeldOut
                ?? throw new InvalidDataException($"{title}/{key}: heldOut must be explicitly supplied.");
            var fitMethod = floor.FitMethod ?? MapArtworkFitMethod.LeastSquares;
            Require(Enum.IsDefined(fitMethod), $"{title}/{key}: unsupported fitMethod '{fitMethod}'.");
            var maximumResidual = floor.MaximumResidual
                ?? throw new InvalidDataException($"{title}/{key}: maximumResidual is required.");
            Require(double.IsFinite(maximumResidual) && maximumResidual >= 0,
                $"{title}/{key}: maximumResidual must be finite and non-negative.");
            var clipToStructureFootprint = floor.ClipToStructureFootprint ?? true;
            var calibrationOnly = floor.CalibrationOnly ?? false;

            var crop = ToNormalizedRectangle(floor.Crop!, $"{title}/{key} crop");
            Require(floor.CropPoints?.All(point => point is not null) ?? true,
                $"{title}/{key}: cropPoints must not contain null entries.");
            Require(trainingInput.All(point => point is not null)
                && heldOutInput.All(point => point is not null),
                $"{title}/{key}: training and heldOut must not contain null landmarks.");
            var cropPoints = (floor.CropPoints ?? [])
                .Select(point => ToNormalizedPoint(point, $"{title}/{key} cropPoints"))
                .ToArray();
            Require(cropPoints.Length is not 1 and not 2,
                $"{title}/{key}: cropPoints must be omitted or contain at least three points.");
            if (cropPoints.Length >= 3)
                Require(Math.Abs(PolygonTwiceArea(cropPoints)) > 1e-8,
                    $"{title}/{key}: cropPoints polygon has zero area.");

            var training = trainingInput.Select(ReadLandmark).ToArray();
            var heldOut = heldOutInput.Select(ReadLandmark).ToArray();
            ValidateLandmarks(training, artwork.Width, artwork.Height, null, null,
                $"{title}/{key} training");
            ValidateLandmarks(heldOut, artwork.Width, artwork.Height, null, null,
                $"{title}/{key} heldOut");
            Require(training.Length >= 3, $"{title}/{key}: at least three training landmarks are required.");
            Require(calibrationOnly ? heldOut.Length == 0 : heldOut.Length > 0,
                $"{title}/{key}: calibrationOnly floors require an empty heldOut list; other floors require independent heldOut landmarks.");
            floors.Add(new PreparedFloor(key, crop, cropPoints, training, heldOut,
                maximumResidual, clipToStructureFootprint, fitMethod, calibrationOnly));
        }

        return new PreparedEntry(title, sourceLayout, sourceImage, floors);
    }

    private static Dictionary<string, MapRecord> ResolveReferences(
        IReadOnlyList<PreparedEntry> entries, IReadOnlyList<MapRecord> importedMaps)
    {
        var result = new Dictionary<string, MapRecord>(StringComparer.Ordinal);
        foreach (var layout in entries.Select(entry => entry.SourceLayout).Distinct(StringComparer.Ordinal))
        {
            var matches = importedMaps.Where(map => MapFloorRules.GetOrderedFloors(map).Any(floor =>
                    floor.SharedStructure?.Source?.LayoutId == layout))
                .ToArray();
            Require(matches.Length == 1,
                $"Expected one canonical map whose real SharedStructure.Source.LayoutId is '{layout}', found {matches.Length}.");
            Require(MapFloorRules.GetOrderedFloors(matches[0]).All(floor =>
                    floor.SharedStructure?.Source?.LayoutId == layout
                    && floor.PrebuiltStructureLine is { IsCurrent: true, IsComplete: true }),
                $"{layout}: canonical floors must retain their sourceLayout and a current prebuilt structure.");
            result.Add(layout, matches[0]);
        }
        return result;
    }

    private static MapDraft CreateDraft(PreparedEntry entry, string mapClass)
    {
        var recognition = new MapRecognitionProfile
        {
            Floors = new Dictionary<string, FloorRecognitionProfile>(StringComparer.Ordinal)
        };
        var floors = new List<FloorDefinition>(entry.Floors.Count);
        for (var index = 0; index < entry.Floors.Count; index++)
        {
            var floor = entry.Floors[index];
            recognition.Floors.Add(floor.Key, new FloorRecognitionProfile
            {
                FloorKey = floor.Key,
                RecognitionRegion = floor.Crop.Clone(),
                FreeCropPoints = floor.CropPoints.Select(point => point.Clone()).ToList()
            });
            floors.Add(new FloorDefinition { Key = floor.Key, DisplayName = floor.Key, SortOrder = index + 1 });
        }

        return new MapDraft
        {
            Class = mapClass,
            Title = entry.Title,
            Floors = floors,
            FloorPaths = entry.Floors.ToDictionary(floor => floor.Key, _ => entry.SourceImage, StringComparer.Ordinal),
            FloorOnePath = entry.SourceImage,
            FloorTwoPath = entry.Floors.Count > 1 ? entry.SourceImage : null,
            Recognition = recognition,
            Tags = []
        };
    }

    private static void VerifyDraftFloorCompatibility(MapDraft draft, PreparedEntry entry)
    {
        var expected = entry.Floors.Select(floor => floor.Key).ToArray();
        var actual = draft.Floors.OrderBy(floor => floor.SortOrder).Select(floor => floor.Key).ToArray();
        Require(actual.SequenceEqual(expected, StringComparer.Ordinal)
            && draft.Recognition.Floors.Count == expected.Length
            && draft.Recognition.Floors.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected),
            $"{entry.Title}: explicit floor order or recognition profiles changed before save.");
    }

    private static void VerifySavedMap(
        MapRepository repository, MapRecord saved, PreparedEntry entry, string mapClass)
    {
        var expected = entry.Floors.Select(floor => floor.Key).ToArray();
        var actual = MapFloorRules.GetOrderedFloors(saved).Select(floor => floor.Key).ToArray();
        Require(saved.Class == mapClass && saved.Title == entry.Title
            && actual.SequenceEqual(expected, StringComparer.Ordinal),
            $"{entry.Title}: production save changed map identity or floor order.");
        Require(saved.Recognition.Floors.Count == expected.Length
            && saved.Recognition.Floors.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected)
            && saved.Recognition.FirstFloor.FloorKey == expected[0],
            $"{entry.Title}: production save changed explicit floor profiles.");
        if (expected.Length > 1)
            Require(saved.Recognition.SecondFloor.FloorKey == expected[1],
                $"{entry.Title}: second-floor compatibility profile changed floor order.");
        Require(saved.FloorOneFileName == saved.Floors.Single(floor => floor.Key == expected[0]).ImageFileName,
            $"{entry.Title}: first-floor compatibility image changed floor order.");
        if (expected.Length > 1)
            Require(saved.FloorTwoFileName == saved.Floors.Single(floor => floor.Key == expected[1]).ImageFileName,
                $"{entry.Title}: second-floor compatibility image changed floor order.");

        var registrations = new List<MapArtworkRegistration>(expected.Length);
        foreach (var floor in entry.Floors)
        {
            var definition = saved.Floors.Single(item => item.Key == floor.Key);
            Require(definition.SharedStructure?.Source?.LayoutId == entry.SourceLayout
                && definition.PrebuiltStructureLine is { IsCurrent: true, IsComplete: true }
                && repository.HasPrebuiltStructureLine(saved, floor.Key),
                $"{entry.Title}/{floor.Key}: production save lost canonical structure or prebuilt binary resource.");
            var registration = definition.ArtworkRegistration
                ?? throw new InvalidDataException($"{entry.Title}/{floor.Key}: artwork registration is missing.");
            Require(registration.FitMethod == floor.FitMethod
                && registration.ClipToStructureFootprint == floor.ClipToStructureFootprint
                && registration.SourceCropConfigured
                && registration.SourceCropRegion is { } crop
                && crop.X == floor.Crop.X && crop.Y == floor.Crop.Y
                && crop.Width == floor.Crop.Width && crop.Height == floor.Crop.Height
                && registration.SourceCropPoints.Count == floor.CropPoints.Count
                && registration.SourceCropPoints.Zip(floor.CropPoints).All(pair =>
                    pair.First.X == pair.Second.X && pair.First.Y == pair.Second.Y),
                $"{entry.Title}/{floor.Key}: production save changed the fit mode or source artwork scope.");
            registrations.Add(registration);

            var sourceBytes = File.ReadAllBytes(entry.SourceImage);
            var storedPath = repository.GetFloorImagePath(saved, floor.Key);
            Require(sourceBytes.AsSpan().SequenceEqual(File.ReadAllBytes(storedPath)),
                $"{entry.Title}/{floor.Key}: source artwork bytes changed during save.");
            Require(string.Equals(Path.GetExtension(storedPath), Path.GetExtension(entry.SourceImage),
                    StringComparison.OrdinalIgnoreCase),
                $"{entry.Title}/{floor.Key}: source artwork extension changed during save.");
            VerifySideDoorFeatureIfPresent(repository, saved, floor.Key, entry.Title);
        }
        Require(registrations.Distinct(ReferenceEqualityComparer.Instance).Count() == registrations.Count,
            $"{entry.Title}: floors share a mutable artwork registration.");
        VerifyCompatibilityPath(repository.GetFloorOnePath(saved),
            repository.GetFloorImagePath(saved, expected[0]), expected[0], entry);
        if (expected.Length > 1)
            VerifyCompatibilityPath(repository.GetFloorTwoPath(saved),
                repository.GetFloorImagePath(saved, expected[1]), expected[1], entry);
    }

    private static void ValidateLandmarks(
        IReadOnlyList<MapArtworkLandmark> points, int sourceWidth, int sourceHeight,
        int? referenceWidth, int? referenceHeight, string label)
    {
        foreach (var point in points)
            Require(double.IsFinite(point.SourceX) && double.IsFinite(point.SourceY)
                && double.IsFinite(point.ReferenceX) && double.IsFinite(point.ReferenceY)
                && point.SourceX >= 0 && point.SourceX < sourceWidth
                && point.SourceY >= 0 && point.SourceY < sourceHeight
                && point.ReferenceX >= 0 && (!referenceWidth.HasValue || point.ReferenceX < referenceWidth.Value)
                && point.ReferenceY >= 0 && (!referenceHeight.HasValue || point.ReferenceY < referenceHeight.Value),
                $"{label}: use raw full-source and canonical reference image coordinates inside both images.");
    }

    private static MapArtworkLandmark ReadLandmark(LandmarkInput point) => new()
    {
        SourceX = point.SourceX,
        SourceY = point.SourceY,
        ReferenceX = point.ReferenceX,
        ReferenceY = point.ReferenceY
    };

    private static NormalizedRectangle ToNormalizedRectangle(RectangleInput crop, string label)
    {
        Require(double.IsFinite(crop.X) && double.IsFinite(crop.Y)
            && double.IsFinite(crop.Width) && double.IsFinite(crop.Height)
            && crop.X >= 0 && crop.Y >= 0 && crop.Width >= 0.01 && crop.Height >= 0.01
            && crop.X + crop.Width <= 1.000001 && crop.Y + crop.Height <= 1.000001,
            $"{label} must be a normalized rectangle inside the full source image.");
        return new NormalizedRectangle { X = crop.X, Y = crop.Y, Width = crop.Width, Height = crop.Height };
    }

    private static NormalizedPoint ToNormalizedPoint(PointInput point, string label)
    {
        Require(double.IsFinite(point.X) && double.IsFinite(point.Y)
            && point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1,
            $"{label} coordinates must be normalized inside the full source image.");
        return new NormalizedPoint { X = point.X, Y = point.Y };
    }

    private static double PolygonTwiceArea(IReadOnlyList<NormalizedPoint> points)
    {
        var area = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }
        return area;
    }

    private static Mat DecodeImage(string path, string label)
    {
        var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Unchanged);
        if (!image.Empty())
            return image;
        image.Dispose();
        throw new InvalidDataException($"{label} cannot be decoded as an image: {path}");
    }

    private static string ResolveInputPath(string value, string manifestPath, string field)
    {
        Require(!string.IsNullOrWhiteSpace(value), $"{field} is required.");
        var directory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("Manifest path has no containing directory.");
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(directory, value));
    }

    private static bool IsSameOrWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void VerifyCompatibilityPath(
        string compatibilityPath, string expectedPath, string floorKey, PreparedEntry entry)
    {
        Require(string.Equals(Path.GetFullPath(compatibilityPath), Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase),
            $"{entry.Title}/{floorKey}: compatibility image path points to the wrong floor.");
        Require(string.Equals(Path.GetExtension(compatibilityPath), Path.GetExtension(entry.SourceImage),
                StringComparison.OrdinalIgnoreCase),
            $"{entry.Title}/{floorKey}: compatibility image path changed source extension.");
    }

    private static CommandOptions ParseArguments(string[] args)
    {
        string? manifest = null;
        string? output = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--manifest":
                case "-m": manifest = ReadOptionValue(args, ref index, "--manifest"); break;
                case "--out":
                case "-o": output = ReadOptionValue(args, ref index, "--out"); break;
                default: throw new InvalidDataException($"Unknown map-package option: {args[index]}");
            }
        }
        Require(!string.IsNullOrWhiteSpace(manifest), "--manifest <portable-manifest.json> is required.");
        Require(!string.IsNullOrWhiteSpace(output), "--out <fresh-output-root> is required.");
        return new CommandOptions(manifest!, output!);
    }

    private static string ReadOptionValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
            throw new InvalidDataException($"{option} requires a value.");
        return args[++index];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private static void PrintUsage() => Console.WriteLine("""
        Usage:
          IDVB.RealCLI map-package --manifest <portable-manifest.json> --out <fresh-output-root>

        The manifest uses mapClass, sourcePackage, and entries containing sourceLayout, title,
        sourceImage, and explicit floors. Each floor supplies crop, training, heldOut, fitMethod,
        clipToStructureFootprint, calibrationOnly, and maximumResidual. Input paths are relative
        to the manifest. Landmark source coordinates use full-image pixels; crop is normalized.
        Output contains map-package.idvm and summary.json.
        """);
}
