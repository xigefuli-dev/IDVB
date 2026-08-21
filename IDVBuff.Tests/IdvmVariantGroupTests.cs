using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IDVBuff.Tests;

public sealed class IdvmVariantGroupTests
{
    [Fact]
    public async Task Version11RoundTripRemapsMapAndGroupIdsButPreservesOrderAndPalette()
    {
        var root = CreateRoot();
        try
        {
            var source = new MapRepository(Path.Combine(root, "source"));
            var maps = await SaveMapsAsync(source, root, "Variant A", "Variant B", "Other");
            var sourceGroup = (await source.ToggleVariantGroupAsync(
                "S1", [maps[1].Id, maps[0].Id])).Group;
            var package = Path.Combine(root, "variants.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.CurrentClass, "S1", package);

            using (var archive = ZipFile.OpenRead(package))
            using (var manifest = JsonDocument.Parse(
                       archive.GetEntry("manifest.json")!.Open()))
            {
                Assert.Equal("1.1", manifest.RootElement.GetProperty("formatVersion").GetString());
                Assert.Equal("1.1", manifest.RootElement.GetProperty("minimumReader").GetString());
                Assert.Single(manifest.RootElement.GetProperty("variantGroups").EnumerateArray());
            }

            var target = new MapRepository(Path.Combine(root, "target"));
            var service = new IdvmPackageService(target);
            var result = await service.ImportAsync(await service.InspectAsync(package));
            var snapshot = await target.GetCatalogSnapshotAsync();
            var importedGroup = Assert.Single(snapshot.VariantGroups);
            Assert.NotEqual(sourceGroup.Id, importedGroup.Id);
            Assert.Equal(sourceGroup.PaletteSlot, importedGroup.PaletteSlot);
            Assert.Equal(result.ImportedMaps.Select(map => map.Id).Take(2), importedGroup.MapIds);
            Assert.DoesNotContain(importedGroup.MapIds, id => sourceGroup.MapIds.Contains(id));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TamperedVariantRelationshipIsRejectedBeforeImport()
    {
        var root = CreateRoot();
        try
        {
            var source = new MapRepository(Path.Combine(root, "source"));
            var maps = await SaveMapsAsync(source, root, "Variant A", "Variant B");
            await source.ToggleVariantGroupAsync("S1", maps.Select(map => map.Id).ToArray());
            var package = Path.Combine(root, "tampered.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.CurrentClass, "S1", package);
            RewriteManifest(package, manifest =>
            {
                manifest["variantGroups"]![0]!["mapIds"]![1] = Guid.NewGuid();
            });

            var target = new MapRepository(Path.Combine(root, "target"));
            var service = new IdvmPackageService(target);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectAsync(package));
            Assert.Empty((await target.GetCatalogSnapshotAsync()).VariantGroups);
            Assert.Empty(await target.GetMapsAsync());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Version10PackageWithoutVariantGroupsRemainsReadable()
    {
        var root = CreateRoot();
        try
        {
            var source = new MapRepository(Path.Combine(root, "source"));
            await SaveMapsAsync(source, root, "Legacy");
            var package = Path.Combine(root, "legacy.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.CurrentClass, "S1", package);
            RewriteManifest(package, manifest =>
            {
                manifest["formatVersion"] = "1.0";
                manifest["minimumReader"] = "1.0";
                manifest.Remove("variantGroups");
            }, version10: true);

            var target = new MapRepository(Path.Combine(root, "target"));
            var service = new IdvmPackageService(target);
            var imported = await service.ImportAsync(await service.InspectAsync(package));
            Assert.Single(imported.ImportedMaps);
            Assert.Empty(imported.ImportedVariantGroups ?? []);
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
        var imagePath = Path.Combine(root, "idvm-map.png");
        using (var image = new Mat(new Size(160, 100), MatType.CV_8UC3, Scalar.All(220)))
            Assert.True(Cv2.ImWrite(imagePath, image));
        var maps = new List<MapRecord>();
        foreach (var title in titles)
        {
            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.1, Y = 0.2, Width = 0.1, Height = 0.1 };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.7, Y = 0.6, Width = 0.1, Height = 0.1 };
            maps.Add(await repository.SaveAsync(new MapDraft
            {
                Class = "S1",
                Title = title,
                FloorOnePath = imagePath,
                FloorTwoPath = imagePath,
                Recognition = recognition
            }));
        }
        return maps.ToArray();
    }

    private static void RewriteManifest(
        string package,
        Action<JsonObject> mutate,
        bool version10 = false)
    {
        var staging = Path.Combine(Path.GetDirectoryName(package)!, $"rewrite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(package, staging);
            var manifestPath = Path.Combine(staging, "manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            mutate(manifest);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllBytes(manifestPath, manifestBytes);
            var headerPath = Path.Combine(staging, "header");
            var header = File.ReadAllBytes(headerPath);
            if (version10)
            {
                header[6] = 0;
                header[7] = 0;
            }
            SHA256.HashData(manifestBytes).CopyTo(header, 36);
            File.WriteAllBytes(headerPath, header);

            File.Delete(package);
            using var output = new FileStream(package, FileMode.CreateNew);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var path in new[] { headerPath, manifestPath }.Concat(
                         Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                             .Where(path => path != headerPath && path != manifestPath)
                             .OrderBy(path => Path.GetRelativePath(staging, path), StringComparer.Ordinal)))
            {
                var name = Path.GetRelativePath(staging, path).Replace('\\', '/');
                var entry = archive.CreateEntry(name,
                    name == "header" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var input = File.OpenRead(path);
                using var entryOutput = entry.Open();
                input.CopyTo(entryOutput);
            }
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.IdvmVariantTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
