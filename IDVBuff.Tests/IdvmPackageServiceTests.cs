using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.IO.Compression;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed class IdvmPackageServiceTests
{
    [Fact]
    public async Task SingleClassRoundTripCreatesANewClassAndFreshMapIdentity()
    {
        var root = CreateRoot();
        try
        {
            var source = new MapRepository(Path.Combine(root, "source"));
            var sourceDraft = CreateDraft(root, "source-map.png", "S1", "军工厂");
            sourceDraft.PortableGates.Add(new MapGateDefinition
            {
                Id = "1f-main",
                FloorKey = "1f",
                Role = "mainEntrance",
                Bounds = new NormalizedRectangle { X = 0.1, Y = 0.2, Width = 0.1, Height = 0.1 },
                DirectionDegrees = 123,
                Enabled = false,
                Confidence = 0.8
            });
            var saved = await source.SaveAsync(sourceDraft);
            var package = Path.Combine(root, "single.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.CurrentClass,
                "S1",
                package);

            Assert.True(File.Exists(package));
            using (var archive = ZipFile.OpenRead(package))
            {
                Assert.Equal("header", archive.Entries[0].FullName);
                Assert.Equal(80, archive.Entries[0].Length);
                Assert.Equal(archive.Entries[0].Length, archive.Entries[0].CompressedLength);
                Assert.Equal("manifest.json", archive.Entries[1].FullName);
                using var manifest = JsonDocument.Parse(archive.Entries[1].Open());
                var files = manifest.RootElement.GetProperty("files").EnumerateArray()
                    .Select(item => item.GetProperty("path").GetString()).ToArray();
                Assert.DoesNotContain("header", files);
                Assert.DoesNotContain("manifest.json", files);
                Assert.DoesNotContain(files, path => path!.Contains("thumbnail", StringComparison.Ordinal));
                Assert.DoesNotContain(files, path => path!.Contains("recognition.png", StringComparison.Ordinal));
                Assert.DoesNotContain(files, path => path!.Contains("overlay", StringComparison.Ordinal));
                var packageId = manifest.RootElement.GetProperty("packageId").GetGuid();
                using var headerStream = archive.Entries[0].Open();
                var header = new byte[80];
                await headerStream.ReadExactlyAsync(header);
                var rawGuid = Convert.ToHexString(header.AsSpan(12, 16));
                Assert.Equal(packageId, Guid.ParseExact(rawGuid, "N"));
            }

            var target = new MapRepository(Path.Combine(root, "target"));
            var service = new IdvmPackageService(target);
            var plan = await service.InspectAsync(package);
            Assert.Equal(1, plan.ClassCount);
            Assert.Equal(1, plan.MapCount);
            var imported = await service.ImportAsync(plan);

            Assert.Equal(["S1 - 新添加1"], imported.CreatedClasses);
            Assert.Single(imported.ImportedMaps);
            Assert.NotEqual(saved.Id, imported.ImportedMaps[0].Id);
            Assert.Equal("军工厂", imported.ImportedMaps[0].DisplayName);
            Assert.Equal("S1 - 新添加1", imported.ImportedMaps[0].Class);
            Assert.True(File.Exists(target.GetFloorOnePath(imported.ImportedMaps[0])));
            var importedProfile = imported.ImportedMaps[0].Recognition.FirstFloor;
            Assert.True(importedProfile.FindAnchor("custom-anchor")!.IsMarked);
            Assert.Single(importedProfile.WholeImageIgnoreRegions);
            Assert.Single(importedProfile.Annotations);
            var importedGate = imported.ImportedMaps[0].PortableGates.Single(gate => gate.Role == "mainEntrance");
            Assert.Equal(123, importedGate.DirectionDegrees);
            Assert.False(importedGate.Enabled);
            Assert.Equal(0.8, importedGate.Confidence);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MultiClassRepeatedImportAllocatesIndependentSuffixes()
    {
        var root = CreateRoot();
        try
        {
            var source = new MapRepository(Path.Combine(root, "source"));
            await source.CreateClassAsync("S2");
            await source.SaveAsync(CreateDraft(root, "s1.png", "S1", "地图 A"));
            await source.SaveAsync(CreateDraft(root, "s2.png", "S2", "地图 B"));
            var package = Path.Combine(root, "all.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.AllClasses,
                null,
                package);

            var target = new MapRepository(Path.Combine(root, "target"));
            await target.CreateClassAsync("S2");
            var service = new IdvmPackageService(target);
            var first = await service.ImportAsync(await service.InspectAsync(package));
            var second = await service.ImportAsync(await service.InspectAsync(package));

            Assert.Equal(["S1 - 新添加1", "S2 - 新添加1"], first.CreatedClasses);
            Assert.Equal(["S1 - 新添加2", "S2 - 新添加2"], second.CreatedClasses);
            var snapshot = await target.GetCatalogSnapshotAsync();
            Assert.Equal(4, snapshot.Maps.Count);
            Assert.All(first.ImportedMaps.Concat(second.ImportedMaps), map =>
                Assert.DoesNotContain(map.Class, new[] { "S1", "S2" }));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MultiFloorRoundTripPreservesOrderOrientationAndImages()
    {
        var root = CreateRoot();
        try
        {
            var draft = CreateDraft(root, "floor-one.png", "S1", "多层地图");
            var secondPath = Path.Combine(root, "floor-two.jpg");
            using (var image = new Mat(new Size(120, 90), MatType.CV_8UC3, Scalar.All(180)))
                Assert.True(Cv2.ImWrite(secondPath, image));
            draft.Floors.Add(new FloorDefinition { Key = "2f", DisplayName = "2F", SortOrder = 2 });
            draft.FloorPaths["2f"] = secondPath;
            draft.FloorTwoPath = secondPath;
            draft.Recognition.SecondFloor.OrientationDegrees = 90;
            draft.Recognition.SecondFloor.RecognitionRegion =
                new NormalizedRectangle { X = 0.1, Y = 0.1, Width = 0.8, Height = 0.8 };

            var source = new MapRepository(Path.Combine(root, "source"));
            await source.SaveAsync(draft);
            var package = Path.Combine(root, "floors.idvm");
            await new IdvmPackageService(source).ExportAsync(IdvmExportScope.AllClasses, null, package);
            var target = new MapRepository(Path.Combine(root, "target"));
            var imported = await new IdvmPackageService(target).ImportAsync(
                await new IdvmPackageService(target).InspectAsync(package));
            var map = Assert.Single(imported.ImportedMaps);

            Assert.Equal(["1f", "2f"], map.Floors.OrderBy(floor => floor.SortOrder).Select(floor => floor.Key));
            Assert.Equal(90, map.Recognition.GetFloor("2f")!.OrientationDegrees);
            Assert.False(map.Recognition.GetFloor("2f")!.FindAnchor("second-floor-primary")!.IsMarked);
            Assert.True(File.Exists(target.GetFloorImagePath(map, "1f")));
            Assert.True(File.Exists(target.GetFloorImagePath(map, "2f")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InspectRejectsManifestTamperingBeforeImport()
    {
        var root = CreateRoot();
        try
        {
            var source = new MapRepository(Path.Combine(root, "source"));
            await source.SaveAsync(CreateDraft(root, "map.png", "S1", "地图"));
            var package = Path.Combine(root, "tampered.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.AllClasses,
                null,
                package);
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("manifest.json")!;
                using var stream = entry.Open();
                stream.Position = 0;
                stream.WriteByte((byte)'!');
            }

            var target = new MapRepository(Path.Combine(root, "target"));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new IdvmPackageService(target).InspectAsync(package));
            Assert.Empty((await target.GetCatalogSnapshotAsync()).Maps);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InspectRejectsZipSlipEntry()
    {
        var root = CreateRoot();
        try
        {
            var package = Path.Combine(root, "unsafe.idvm");
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../outside.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("unsafe");
            }
            var repository = new MapRepository(Path.Combine(root, "target"));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new IdvmPackageService(repository).InspectAsync(package));
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RepositoryBatchImportRollsBackEarlierMapsAndClassesOnFailure()
    {
        var root = CreateRoot();
        try
        {
            var repository = new MapRepository(Path.Combine(root, "maps"));
            var valid = CreateDraft(root, "valid.png", "ignored", "有效地图");
            var invalid = new MapDraft
            {
                Floors = [new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }],
                Recognition = CreateRecognition()
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ImportBatchAsync(
            [
                new MapImportClassDraft("导入 A", [valid]),
                new MapImportClassDraft("导入 B", [invalid])
            ]));

            var snapshot = await repository.GetCatalogSnapshotAsync();
            Assert.Equal(["S1"], snapshot.Classes);
            Assert.Empty(snapshot.Maps);
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(root, "maps"),
                ".idvm-import-*.json"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task EditingAnExistingMapIncrementsPortableContentVersion()
    {
        var root = CreateRoot();
        try
        {
            var repository = new MapRepository(Path.Combine(root, "maps"));
            var saved = await repository.SaveAsync(CreateDraft(root, "versioned.png", "S1", "版本地图"));
            Assert.Equal(1, saved.ContentVersion);
            var edit = await repository.CreateDraftAsync(saved.Id);
            Assert.NotNull(edit);
            var edited = await repository.SaveAsync(edit!);
            Assert.Equal(2, edited.ContentVersion);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("S1", new[] { "S1" }, "S1 - 新添加1")]
    [InlineData("S1", new[] { "s1", "S1 - 新添加1" }, "S1 - 新添加2")]
    [InlineData("S2", new[] { "S1" }, "S2")]
    public void ImportedClassNameIsCaseInsensitiveAndDeterministic(
        string source,
        string[] existing,
        string expected)
    {
        Assert.Equal(expected, MapRepository.BuildUniqueImportedClassName(source, existing));
    }

    private static MapDraft CreateDraft(string root, string fileName, string className, string title)
    {
        var imagePath = Path.Combine(root, fileName);
        using (var image = new Mat(new Size(160, 100), MatType.CV_8UC3, Scalar.All(240)))
        {
            Cv2.Rectangle(image, new Rect(20, 20, 30, 20), Scalar.All(40), -1);
            Assert.True(Cv2.ImWrite(imagePath, image));
        }
        var recognition = CreateRecognition();
        return new MapDraft
        {
            Class = className,
            Title = title,
            Floors = [new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }],
            FloorPaths = new Dictionary<string, string> { ["1f"] = imagePath },
            FloorOnePath = imagePath,
            Recognition = recognition
        };
    }

    private static MapRecognitionProfile CreateRecognition()
    {
        var recognition = new MapRecognitionProfile();
        recognition.EnsureStandardAnchors();
        recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.1, Y = 0.2, Width = 0.1, Height = 0.1 };
        recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.7, Y = 0.6, Width = 0.1, Height = 0.1 };
        recognition.FirstFloor.Anchors.Add(new RecognitionAnchor
        {
            Key = "custom-anchor",
            DisplayName = "辅助锚点",
            Role = RecognitionAnchorRole.Optional,
            Weight = 0.45,
            Bounds = new NormalizedRectangle { X = 0.4, Y = 0.3, Width = 0.1, Height = 0.1 }
        });
        recognition.FirstFloor.WholeImageIgnoreRegions.Add(
            new NormalizedRectangle { X = 0, Y = 0, Width = 0.02, Height = 1 });
        recognition.FirstFloor.Annotations.Add(new MapAnnotation
        {
            Type = MapAnnotationType.Text,
            ColorIndex = 3,
            Bounds = new NormalizedRectangle { X = 0.2, Y = 0.2, Width = 0.1, Height = 0.1 },
            Text = "测试"
        });
        return recognition;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.IdvmTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
