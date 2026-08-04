using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IDVBuff.Tests;

public sealed class MapRepositoryTests
{
    [Fact]
    public async Task SaveRejectsReorderWhenNewPrimaryFloorHasNoGatePair()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var roofPath = Path.Combine(root, "roof.png");
            var groundPath = Path.Combine(root, "ground.png");
            using (var source = new Mat(new Size(160, 100), MatType.CV_8UC3, Scalar.White))
            {
                Assert.True(Cv2.ImWrite(roofPath, source));
                Assert.True(Cv2.ImWrite(groundPath, source));
            }
            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.FloorKey = "roof";
            recognition.SecondFloor.FloorKey = "ground";
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.1d, Height = 0.1d };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.8d, Y = 0.8d, Width = 0.1d, Height = 0.1d };
            recognition.Floors = new Dictionary<string, FloorRecognitionProfile>
            {
                ["roof"] = recognition.FirstFloor,
                ["ground"] = recognition.SecondFloor
            };
            var repository = new MapRepository(Path.Combine(root, "maps"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(
                new MapDraft
                {
                    Floors =
                    [
                        new FloorDefinition { Key = "ground", DisplayName = "Ground", SortOrder = 1 },
                        new FloorDefinition { Key = "roof", DisplayName = "Roof", SortOrder = 2 }
                    ],
                    FloorPaths = new Dictionary<string, string>
                    {
                        ["roof"] = roofPath,
                        ["ground"] = groundPath
                    },
                    Recognition = recognition
                }));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveKeepsReorderedCustomFloorsAndRecognitionProfilesAligned()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var firstSource = Path.Combine(root, "first.png");
            var secondSource = Path.Combine(root, "second.png");
            using (var source = new Mat(new Size(160, 100), MatType.CV_8UC3, Scalar.All(255)))
            {
                Assert.True(Cv2.ImWrite(firstSource, source));
                Assert.True(Cv2.ImWrite(secondSource, source));
            }

            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.FloorKey = "roof";
            recognition.SecondFloor.FloorKey = "ground";
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.1d, Y = 0.2d, Width = 0.1d, Height = 0.1d };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.7d, Y = 0.6d, Width = 0.1d, Height = 0.1d };
            recognition.Floors = new Dictionary<string, FloorRecognitionProfile>
            {
                ["roof"] = recognition.FirstFloor,
                ["ground"] = recognition.SecondFloor
            };

            var repository = new MapRepository(Path.Combine(root, "maps"));
            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorPaths = new Dictionary<string, string>
                {
                    ["roof"] = firstSource,
                    ["ground"] = secondSource
                },
                Floors =
                [
                    new FloorDefinition { Key = "roof", DisplayName = "屋顶", SortOrder = 1 },
                    new FloorDefinition { Key = "ground", DisplayName = "地面", SortOrder = 2 }
                ],
                Recognition = recognition
            });

            var reloaded = (await repository.GetMapsAsync()).Single();

            Assert.Equal(["roof", "ground"], reloaded.Floors
                .OrderBy(floor => floor.SortOrder)
                .Select(floor => floor.Key));
            Assert.Equal(["屋顶", "地面"], reloaded.Floors
                .OrderBy(floor => floor.SortOrder)
                .Select(floor => floor.DisplayName));
            Assert.Equal("floor-roof.png", Path.GetFileName(repository.GetFloorOnePath(reloaded)));
            Assert.Equal("floor-ground.png", Path.GetFileName(repository.GetFloorTwoPath(reloaded)));
            Assert.Equal("floor-roof.png", reloaded.Floors[0].ImageFileName);
            Assert.Equal("floor-ground.png", reloaded.Floors[1].ImageFileName);
            Assert.All(reloaded.Floors, floor =>
            {
                Assert.Equal(160, floor.ImageWidth);
                Assert.Equal(100, floor.ImageHeight);
                Assert.Matches("^[0-9a-f]{64}$", floor.ImageSha256);
            });
            Assert.True(File.Exists(repository.GetFloorRecognitionPath(reloaded, "roof")));
            Assert.True(File.Exists(repository.GetFloorRecognitionPath(reloaded, "ground")));
            Assert.True(reloaded.Recognition.GetFloor("roof")!
                .FindAnchor("main-entrance")!.IsMarked);
            Assert.Equal("roof", reloaded.Recognition.FirstFloor.FloorKey);
            Assert.Equal("ground", reloaded.Recognition.SecondFloor.FloorKey);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveCreatesRecognitionCropAndWhiteKeyOverlay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var floorOne = Path.Combine(root, "source-1.png");
            var floorTwo = Path.Combine(root, "source-2.png");
            using (var source = new Mat(new Size(200, 100), MatType.CV_8UC3, Scalar.All(255)))
            {
                Cv2.Rectangle(source, new Rect(40, 25, 50, 30), Scalar.All(0), -1);
                Assert.True(Cv2.ImWrite(floorOne, source));
                Assert.True(Cv2.ImWrite(floorTwo, source));
            }

            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.RecognitionRegion =
                new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.8d, Height = 0.8d };
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.1d, Y = 0.2d, Width = 0.1d, Height = 0.1d };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.7d, Y = 0.6d, Width = 0.1d, Height = 0.1d };
            var repository = new MapRepository(Path.Combine(root, "maps"));

            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorOnePath = floorOne,
                FloorTwoPath = floorTwo,
                Recognition = recognition
            });

            var recognitionPath = repository.GetFloorOneRecognitionPath(saved);
            var overlayPath = repository.GetFloorOneOverlayPath(saved);
            var firstFloor = saved.Floors.OrderBy(floor => floor.SortOrder).First();
            Assert.Equal(Path.GetFileName(recognitionPath), firstFloor.RecognitionFileName);
            Assert.Equal(Path.GetFileName(overlayPath), firstFloor.OverlayFileName);
            Assert.Equal(firstFloor.ImageSha256, firstFloor.RecognitionSourceSha256);
            Assert.Equal(firstFloor.RecognitionSha256, firstFloor.OverlaySourceSha256);
            Assert.True(File.Exists(recognitionPath));
            Assert.True(File.Exists(overlayPath));
            using var crop = Cv2.ImRead(recognitionPath, ImreadModes.Unchanged);
            using var overlay = Cv2.ImRead(overlayPath, ImreadModes.Unchanged);
            Assert.Equal(160, crop.Width);
            Assert.Equal(80, crop.Height);
            Assert.Equal(4, overlay.Channels());
            Assert.Equal(0, overlay.At<Vec4b>(0, 0).Item3);
            Assert.Equal(255, overlay.At<Vec4b>(30, 40).Item3);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TamperedRecognitionAssetIsRegeneratedForTheSameFloor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.png");
            using (var source = new Mat(new Size(200, 100), MatType.CV_8UC3, Scalar.All(255)))
            {
                Cv2.Rectangle(source, new Rect(40, 25, 50, 30), Scalar.All(0), -1);
                Assert.True(Cv2.ImWrite(sourcePath, source));
            }

            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.RecognitionRegion =
                new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.8d, Height = 0.8d };
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.1d, Y = 0.2d, Width = 0.1d, Height = 0.1d };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.7d, Y = 0.6d, Width = 0.1d, Height = 0.1d };
            var repository = new MapRepository(Path.Combine(root, "maps"));
            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorOnePath = sourcePath,
                Floors = [new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }],
                Recognition = recognition
            });

            var originalHash = saved.Floors.Single().RecognitionSha256;
            var recognitionPath = repository.GetFloorOneRecognitionPath(saved);
            using (var tampered = new Mat(new Size(160, 80), MatType.CV_8UC3, Scalar.All(0)))
                Assert.True(Cv2.ImWrite(recognitionPath, tampered));

            var loaded = (await repository.GetMapsAsync()).ToArray();
            await repository.EnsureDerivedAssetsAsync(loaded);

            Assert.Equal(originalHash, loaded.Single().Floors.Single().RecognitionSha256);
            Assert.Equal(
                originalHash,
                (await repository.GetMapsAsync()).Single().Floors.Single().RecognitionSha256);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyTwentyEightMapCatalogMigratesBoundsWithoutChangingAnchors()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var maps = Enumerable.Range(1, 28)
                .Select(index =>
                {
                    var profile = new MapRecognitionProfile
                    {
                        SchemaVersion = 3,
                        FirstFloor = new FloorRecognitionProfile
                        {
                            Floor = MapFloor.First,
                            RecognitionPixelWidth = 1000,
                            RecognitionPixelHeight = 800,
                            Anchors =
                            [
                                new RecognitionAnchor
                                {
                                    Key = "main-entrance",
                                    DisplayName = "main",
                                    Role = RecognitionAnchorRole.Required,
                                    Bounds = new NormalizedRectangle
                                    {
                                        X = 0.1d,
                                        Y = 0.1d,
                                        Width = 0.05d,
                                        Height = 0.05d
                                    }
                                },
                                new RecognitionAnchor
                                {
                                    Key = "side-entrance",
                                    DisplayName = "side",
                                    Role = RecognitionAnchorRole.Required,
                                    Bounds = new NormalizedRectangle
                                    {
                                        X = 0.8d,
                                        Y = 0.7d,
                                        Width = 0.05d,
                                        Height = 0.05d
                                    }
                                },
                                new RecognitionAnchor
                                {
                                    Key = $"optional-{index}",
                                    DisplayName = "optional",
                                    Role = RecognitionAnchorRole.Optional,
                                    Bounds = new NormalizedRectangle
                                    {
                                        X = 0.4d,
                                        Y = 0.4d,
                                        Width = 0.05d,
                                        Height = 0.05d
                                    }
                                }
                            ]
                        },
                        SecondFloor = new FloorRecognitionProfile
                        {
                            Floor = MapFloor.Second
                        }
                    };
                    return new MapRecord
                    {
                        Id = Guid.NewGuid(),
                        SequenceNumber = index,
                        Recognition = profile,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                })
                .ToArray();
            using var localImage = new Mat(new Size(32, 24), MatType.CV_8UC3, Scalar.All(255));
            foreach (var map in maps)
            {
                map.FloorOneFileName = "floor-1.png";
                map.FloorTwoFileName = "floor-2.png";
                var mapDirectory = Path.Combine(root, map.Id.ToString("N"));
                Directory.CreateDirectory(mapDirectory);
                Assert.True(Cv2.ImWrite(Path.Combine(mapDirectory, map.FloorOneFileName), localImage));
                Assert.True(Cv2.ImWrite(Path.Combine(mapDirectory, map.FloorTwoFileName), localImage));
            }
            var catalogPath = Path.Combine(root, "maps.json");
            await File.WriteAllTextAsync(
                catalogPath,
                JsonSerializer.Serialize(
                    new
                    {
                        NextSequenceNumber = 29,
                        Maps = maps
                    }));
            var repository = new MapRepository(root);

            var migrated = await repository.GetMapsAsync();

            Assert.Equal(28, migrated.Count);
            Assert.Equal(["S1"], (await repository.GetCatalogSnapshotAsync()).Classes);
            Assert.All(migrated, map =>
            {
                Assert.Equal(6, map.Recognition.SchemaVersion);
                Assert.Equal(
                    3,
                    map.Recognition.FirstFloor.Anchors.Count);
                var bounds = map.Recognition.FirstFloor.ValidMapBounds;
                Assert.NotNull(bounds);
                Assert.Equal(1000d, bounds.Width);
                Assert.Equal(800d, bounds.Height);
            });
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(catalogPath));
            Assert.All(
                document.RootElement.GetProperty("Maps")
                    .EnumerateArray(),
                map =>
                {
                    var recognition = map.GetProperty("Recognition");
                    Assert.Equal(
                        6,
                        recognition.GetProperty("SchemaVersion")
                            .GetInt32());
                    Assert.True(
                        recognition.GetProperty("Floors")
                            .GetProperty("1f")
                            .TryGetProperty(
                                "ValidMapBounds",
                                out var bounds)
                        && bounds.ValueKind
                            == JsonValueKind.Object);
                });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task ClassesPersistEmptyGroupsAndRejectCaseInsensitiveDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        try
        {
            var repository = new MapRepository(root);
            var initial = await repository.GetCatalogSnapshotAsync();
            Assert.Equal(["S1"], initial.Classes);

            Assert.Equal("ClassA", await repository.CreateClassAsync("  ClassA  "));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateClassAsync("classa"));

            var reloaded = await repository.GetCatalogSnapshotAsync();
            Assert.Equal(["S1", "ClassA"], reloaded.Classes);
            Assert.Empty(reloaded.Maps);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteClassRemovesItsMapsButKeepsTheRemainingClass()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var map = new MapRecord
            {
                Id = Guid.NewGuid(),
                SequenceNumber = 1,
                Class = "ClassA",
                FloorOneFileName = "floor-1.png",
                FloorTwoFileName = "floor-2.png",
                Recognition = new MapRecognitionProfile { SchemaVersion = 6 },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var mapDirectory = Path.Combine(root, map.Id.ToString("N"));
            Directory.CreateDirectory(mapDirectory);
            using (var image = new Mat(new Size(32, 24), MatType.CV_8UC3, Scalar.All(255)))
            {
                Assert.True(Cv2.ImWrite(Path.Combine(mapDirectory, map.FloorOneFileName), image));
                Assert.True(Cv2.ImWrite(Path.Combine(mapDirectory, map.FloorTwoFileName), image));
            }
            await File.WriteAllTextAsync(Path.Combine(root, "maps.json"), JsonSerializer.Serialize(new
            {
                StorageSchemaVersion = 7,
                NextSequenceNumber = 2,
                Classes = new[] { "S1", "ClassA" },
                Maps = new[] { map }
            }));

            var repository = new MapRepository(root);
            var deleted = await repository.DeleteClassAsync("classa");
            var remaining = await repository.GetCatalogSnapshotAsync();

            Assert.Equal("ClassA", deleted.ClassName);
            Assert.Equal(1, deleted.DeletedMapCount);
            Assert.Equal(["S1"], remaining.Classes);
            Assert.Empty(remaining.Maps);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteClassAsync("S1"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveStoresImagesLocallyWithoutBase64AndSurvivesSourceDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "sources");
        var mapDirectory = Path.Combine(root, "maps");
        Directory.CreateDirectory(sourceDirectory);
        try
        {
            var firstSource = Path.Combine(sourceDirectory, "selected-first.png");
            var secondSource = Path.Combine(sourceDirectory, "selected-second.png");
            using (var image = new Mat(new Size(96, 64), MatType.CV_8UC3, Scalar.All(255)))
            {
                Assert.True(Cv2.ImWrite(firstSource, image));
                Assert.True(Cv2.ImWrite(secondSource, image));
            }

            var repository = new MapRepository(mapDirectory);
            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorOnePath = firstSource,
                FloorTwoPath = secondSource,
                Recognition = CreateRecognitionWithGateMarkers()
            });

            var catalogPath = Path.Combine(mapDirectory, "maps.json");
            var catalogJson = await File.ReadAllTextAsync(catalogPath);
            Assert.DoesNotContain("FloorImageBase64", catalogJson, StringComparison.Ordinal);
            Assert.DoesNotContain(firstSource, catalogJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secondSource, catalogJson, StringComparison.OrdinalIgnoreCase);

            var localFirstPath = repository.GetFloorOnePath(saved);
            var localSecondPath = repository.GetFloorTwoPath(saved);
            Assert.True(File.Exists(localFirstPath));
            Assert.True(File.Exists(localSecondPath));
            Assert.StartsWith(Path.GetFullPath(mapDirectory), Path.GetFullPath(localFirstPath), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(mapDirectory), Path.GetFullPath(localSecondPath), StringComparison.OrdinalIgnoreCase);

            File.Delete(firstSource);
            File.Delete(secondSource);

            var reloaded = (await new MapRepository(mapDirectory).GetMapsAsync()).Single();
            Assert.True(File.Exists(repository.GetFloorImagePath(reloaded, "1f")));
            Assert.True(File.Exists(repository.GetFloorImagePath(reloaded, "2f")));
            using var reloadedImage = Cv2.ImRead(repository.GetFloorImagePath(reloaded, "1f"), ImreadModes.Unchanged);
            Assert.False(reloadedImage.Empty());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyCatalogRemovesBase64WhenLocalImagesExist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var map = CreateCatalogMapRecord();
            var mapDirectory = Path.Combine(root, map.Id.ToString("N"));
            Directory.CreateDirectory(mapDirectory);
            using (var image = new Mat(new Size(32, 24), MatType.CV_8UC3, Scalar.All(255)))
            {
                Assert.True(Cv2.ImWrite(Path.Combine(mapDirectory, map.FloorOneFileName), image));
                Assert.True(Cv2.ImWrite(Path.Combine(mapDirectory, map.FloorTwoFileName), image));
            }

            var mapJson = JsonSerializer.SerializeToNode(map)!.AsObject();
            mapJson["FloorImageBase64"] = new JsonObject
            {
                ["1f"] = "legacy-inline-image",
                ["2f"] = "legacy-inline-image"
            };
            var catalogJson = new JsonObject
            {
                ["NextSequenceNumber"] = 2,
                ["Maps"] = new JsonArray(mapJson)
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var catalogPath = Path.Combine(root, "maps.json");
            await File.WriteAllTextAsync(catalogPath, catalogJson);

            var repository = new MapRepository(root);
            Assert.Single(await repository.GetMapsAsync());

            var cleaned = await File.ReadAllTextAsync(catalogPath);
            Assert.DoesNotContain("FloorImageBase64", cleaned, StringComparison.Ordinal);
            Assert.DoesNotContain("legacy-inline-image", cleaned, StringComparison.Ordinal);
            Assert.Contains("StorageSchemaVersion", cleaned, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepairImageMetadataBackfillsStampsAndThumbnails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.png");
            using (var image = new Mat(new Size(96, 64), MatType.CV_8UC3, Scalar.All(255)))
                Assert.True(Cv2.ImWrite(source, image));

            var repository = new MapRepository(Path.Combine(root, "maps"));
            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorOnePath = source,
                FloorTwoPath = source,
                Recognition = CreateRecognitionWithGateMarkers()
            });
            var catalogPath = Path.Combine(root, "maps", "maps.json");
            var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!.AsObject();
            var firstFloor = catalog["Maps"]![0]!["Floors"]![0]!.AsObject();
            firstFloor["ImageFileLength"] = 0;
            firstFloor["ImageLastWriteUtcTicks"] = 0;
            firstFloor["ThumbnailFileName"] = "";
            firstFloor["ThumbnailFileLength"] = 0;
            firstFloor["ThumbnailLastWriteUtcTicks"] = 0;
            catalog["StorageSchemaVersion"] = 10;
            await File.WriteAllTextAsync(catalogPath, catalog.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            Assert.Single(await repository.GetMapsAsync());
            await repository.RepairImageMetadataAsync(CancellationToken.None);

            var repaired = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!.AsObject();
            var repairedFloor = repaired["Maps"]![0]!["Floors"]![0]!.AsObject();
            Assert.True(repairedFloor["ImageFileLength"]!.GetValue<long>() > 0);
            Assert.True(repairedFloor["ImageLastWriteUtcTicks"]!.GetValue<long>() > 0);
            Assert.False(string.IsNullOrWhiteSpace(repairedFloor["ThumbnailFileName"]!.GetValue<string>()));
            Assert.Equal(12, repaired["StorageSchemaVersion"]!.GetValue<int>());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedLocalImageIsRejectedByExplicitBindingMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "selected.png");
            using (var image = new Mat(new Size(96, 64), MatType.CV_8UC3, Scalar.All(255)))
                Assert.True(Cv2.ImWrite(source, image));

            var repository = new MapRepository(Path.Combine(root, "maps"));
            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorOnePath = source,
                FloorTwoPath = source,
                Recognition = CreateRecognitionWithGateMarkers()
            });

            using (var changedImage = new Mat(new Size(96, 64), MatType.CV_8UC3, Scalar.All(0)))
                Assert.True(Cv2.ImWrite(repository.GetFloorOnePath(saved), changedImage));

            Assert.Single(await repository.GetMapsAsync());
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new MapRepository(Path.Combine(root, "maps")).VerifyMapContentAsync(saved.Id));
            Assert.Contains("metadata does not match", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyCatalogMigrationFailsWithoutLocalImagesAndLeavesCatalogUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var map = CreateCatalogMapRecord();
            var mapJson = JsonSerializer.SerializeToNode(map)!.AsObject();
            mapJson["FloorImageBase64"] = new JsonObject
            {
                ["1f"] = "legacy-inline-image",
                ["2f"] = "legacy-inline-image"
            };
            var original = new JsonObject
            {
                ["NextSequenceNumber"] = 2,
                ["Maps"] = new JsonArray(mapJson)
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var catalogPath = Path.Combine(root, "maps.json");
            await File.WriteAllTextAsync(catalogPath, original);

            var repository = new MapRepository(root);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => repository.GetMapsAsync());

            Assert.Contains(map.Id.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("missing its local image", exception.Message, StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllTextAsync(catalogPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static MapRecord CreateCatalogMapRecord()
    {
        return new MapRecord
        {
            Id = Guid.NewGuid(),
            SequenceNumber = 1,
            FloorOneFileName = "floor-1.png",
            FloorTwoFileName = "floor-2.png",
            Recognition = CreateRecognitionWithGateMarkers(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static MapRecognitionProfile CreateRecognitionWithGateMarkers()
    {
        var recognition = new MapRecognitionProfile();
        recognition.EnsureStandardAnchors();
        recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.1d, Height = 0.1d };
        recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.8d, Y = 0.8d, Width = 0.1d, Height = 0.1d };
        return recognition;
    }
}
