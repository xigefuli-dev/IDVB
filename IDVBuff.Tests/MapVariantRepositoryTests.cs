using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IDVBuff.Tests;

public sealed class MapVariantRepositoryTests
{
    [Fact]
    public async Task ToggleEnforcesWholeGroupsAndReusesFreedPaletteSlots()
    {
        var root = CreateRoot();
        try
        {
            var repository = new MapRepository(Path.Combine(root, "Maps"));
            var maps = await SaveMapsAsync(repository, root, "A", "B", "C", "D", "E");

            var first = await repository.ToggleVariantGroupAsync(
                "S1", [maps[0].Id, maps[1].Id]);
            var second = await repository.ToggleVariantGroupAsync(
                "S1", [maps[2].Id, maps[3].Id]);
            Assert.Equal(0, first.Group.PaletteSlot);
            Assert.Equal(1, second.Group.PaletteSlot);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.ToggleVariantGroupAsync("S1", [maps[0].Id, maps[4].Id]));

            var removed = await repository.ToggleVariantGroupAsync(
                "S1", [maps[1].Id, maps[0].Id]);
            Assert.Equal(MapVariantGroupChangeKind.Unbound, removed.Kind);
            var reused = await repository.ToggleVariantGroupAsync(
                "S1", [maps[0].Id, maps[4].Id]);
            Assert.Equal(0, reused.Group.PaletteSlot);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ReorderTreatsVariantAsStableBlockWithoutChangingIdentityOrContent()
    {
        var root = CreateRoot();
        try
        {
            var repository = new MapRepository(Path.Combine(root, "Maps"));
            var maps = await SaveMapsAsync(repository, root, "A", "B", "C", "D", "E");
            await repository.ToggleVariantGroupAsync("S1", [maps[1].Id, maps[3].Id]);
            var before = (await repository.GetMapsAsync()).ToDictionary(map => map.Id);

            await repository.ReorderClassAsync("S1");

            var reordered = (await repository.GetMapsAsync())
                .OrderBy(map => map.SequenceNumber).ToArray();
            Assert.Equal(
                [maps[0].Id, maps[1].Id, maps[3].Id, maps[2].Id, maps[4].Id],
                reordered.Select(map => map.Id));
            Assert.Equal([1, 2, 3, 4, 5], reordered.Select(map => map.SequenceNumber));
            Assert.All(reordered, map => Assert.True(string.IsNullOrEmpty(map.Title)));
            foreach (var map in reordered)
            {
                Assert.Equal(before[map.Id].ContentVersion, map.ContentVersion);
                Assert.Equal(before[map.Id].UpdatedAt, map.UpdatedAt);
                Assert.Equal(
                    repository.GetFloorImagePath(before[map.Id], "1f"),
                    repository.GetFloorImagePath(map, "1f"));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DeleteDissolvesTwoMemberGroupAndClassRenameMovesGroup()
    {
        var root = CreateRoot();
        try
        {
            var repository = new MapRepository(Path.Combine(root, "Maps"));
            var maps = await SaveMapsAsync(repository, root, "A", "B", "C");
            await repository.ToggleVariantGroupAsync("S1", [maps[0].Id, maps[1].Id]);
            await repository.RenameClassAsync("S1", "Ranked");
            Assert.Equal("Ranked", (await repository.GetCatalogSnapshotAsync())
                .VariantGroups.Single().Class);

            await repository.DeleteAsync(maps[0].Id);
            Assert.Empty((await repository.GetCatalogSnapshotAsync()).VariantGroups);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ClassAllowsTwelveGroupsAndReclaimsSlotAfterUnbind()
    {
        var root = CreateRoot();
        try
        {
            var repository = new MapRepository(Path.Combine(root, "Maps"));
            var maps = await SaveMapsAsync(
                repository,
                root,
                Enumerable.Range(1, 26).Select(index => $"Map {index}").ToArray());
            for (var index = 0; index < 12; index++)
            {
                var group = await repository.ToggleVariantGroupAsync(
                    "S1", [maps[index * 2].Id, maps[index * 2 + 1].Id]);
                Assert.Equal(index, group.Group.PaletteSlot);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.ToggleVariantGroupAsync("S1", [maps[24].Id, maps[25].Id]));

            await repository.ToggleVariantGroupAsync("S1", [maps[6].Id, maps[7].Id]);
            var reclaimed = await repository.ToggleVariantGroupAsync(
                "S1", [maps[24].Id, maps[25].Id]);
            Assert.Equal(3, reclaimed.Group.PaletteSlot);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Schema15MigrationCreatesVerifiedBackupBeforeSchema16Write()
    {
        var root = CreateRoot();
        try
        {
            var mapRoot = Path.Combine(root, "Maps");
            var repository = new MapRepository(mapRoot);
            await SaveMapsAsync(repository, root, "A", "B");
            var catalogPath = Path.Combine(mapRoot, "maps.json");
            var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!.AsObject();
            catalog["StorageSchemaVersion"] = 15;
            catalog.Remove("VariantGroups");
            await File.WriteAllTextAsync(
                catalogPath,
                catalog.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var migrating = new MapRepository(mapRoot);
            Assert.Equal(2, (await migrating.GetMapsAsync()).Count);
            var migrated = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!.AsObject();
            Assert.Equal(16, migrated["StorageSchemaVersion"]!.GetValue<int>());
            var backups = Directory.GetDirectories(migrating.VariantMigrationBackupRoot)
                .Where(path => !path.EndsWith(".pending", StringComparison.Ordinal)).ToArray();
            Assert.Single(backups);
            Assert.True(File.Exists(Path.Combine(backups[0], "backup-manifest.json")));
            Assert.True(File.Exists(Path.Combine(backups[0], "Maps", "maps.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task<MapRecord[]> SaveMapsAsync(
        MapRepository repository,
        string root,
        params string[] titles)
    {
        var imagePath = Path.Combine(root, "map.png");
        if (!File.Exists(imagePath))
        {
            using var image = new Mat(new Size(160, 100), MatType.CV_8UC3, Scalar.All(240));
            Assert.True(Cv2.ImWrite(imagePath, image));
        }
        var result = new List<MapRecord>();
        foreach (var title in titles)
        {
            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.1, Y = 0.2, Width = 0.1, Height = 0.1 };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.7, Y = 0.6, Width = 0.1, Height = 0.1 };
            result.Add(await repository.SaveAsync(new MapDraft
            {
                Class = "S1",
                Title = title,
                FloorOnePath = imagePath,
                FloorTwoPath = imagePath,
                Recognition = recognition
            }));
        }
        return result.ToArray();
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.VariantTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
