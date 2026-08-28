using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IDVBuff.Tests;
public sealed partial class IdvmPackageServiceTests
{

    [Fact]
    public async Task Version12RequiresExplicitFloorMarkerCapability()
    {
        var root = CreateRoot();
        try
        {
            var source = new MapRepository(Path.Combine(root, "source"));
            await source.SaveAsync(CreateDraft(root, "capability.png", "S1", "Capability"));
            var package = Path.Combine(root, "capability.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.AllClasses,
                null,
                package);
            RewriteManifestDocument(
                package,
                manifest => manifest["capabilities"]!.AsObject()
                    .Remove("floorMarkerKeys"));

            var target = new IdvmPackageService(
                new MapRepository(Path.Combine(root, "target")));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => target.InspectAsync(package));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TagsRoundTripAndInspectDoesNotMutateLocalTagStore()
    {
        var root = CreateRoot();
        try
        {
            var group = new MapTagGroup
            {
                Id = Guid.NewGuid(),
                Name = "门方位",
                Tags = ["东"]
            };
            var sourceTagStore = new MapTagStore(Path.Combine(root, "source-tags.json"));
            await sourceTagStore.SaveAsync([group]);
            var source = new MapRepository(Path.Combine(root, "source"));
            var draft = CreateDraft(root, "tagged.png", "S1", "Tagged");
            draft.Tags[group.Id] = "东";
            await source.SaveAsync(draft);

            var package = Path.Combine(root, "tagged.idvm");
            await new IdvmPackageService(source, sourceTagStore).ExportAsync(
                IdvmExportScope.AllClasses,
                null,
                package);

            var targetTagPath = Path.Combine(root, "target-tags.json");
            var targetTagStore = new MapTagStore(targetTagPath);
            var targetRepository = new MapRepository(Path.Combine(root, "target"));
            var service = new IdvmPackageService(targetRepository, targetTagStore);
            await using (var plan = await service.InspectAsync(package))
            {
                Assert.False(File.Exists(targetTagPath));
            }

            var importPlan = await service.InspectAsync(package);
            var result = await service.ImportAsync(importPlan);
            var imported = Assert.Single(result.ImportedMaps);
            Assert.Equal("东", imported.Tags[group.Id]);
            var importedGroup = Assert.Single(await targetTagStore.LoadAsync());
            Assert.Equal(group.Id, importedGroup.Id);
            Assert.Equal("门方位", importedGroup.Name);
            Assert.Contains("东", importedGroup.Tags);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Version13RequiresExplicitMapTagsCapability()
    {
        var root = CreateRoot();
        try
        {
            var source = new MapRepository(Path.Combine(root, "source"));
            await source.SaveAsync(CreateDraft(root, "tags-capability.png", "S1", "Capability"));
            var package = Path.Combine(root, "tags-capability.idvm");
            await new IdvmPackageService(source).ExportAsync(IdvmExportScope.AllClasses, null, package);
            RewriteManifestDocument(
                package,
                manifest => manifest["capabilities"]!.AsObject().Remove("mapTags"));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new IdvmPackageService(new MapRepository(Path.Combine(root, "target")))
                    .InspectAsync(package));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static void RewriteAnchorsDocument(string package, Action<JsonObject> mutate)
        => RewriteDataDocument(package, "anchors.json", mutate);

    private static void RewriteDataDocument(
        string package,
        string documentName,
        Action<JsonObject> mutate)
    {
        var staging = Path.Combine(Path.GetDirectoryName(package)!, $"rewrite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(package, staging);
            var documentPath = Directory.EnumerateFiles(
                staging,
                documentName,
                SearchOption.AllDirectories).Single();
            var document = JsonNode.Parse(File.ReadAllText(documentPath))!.AsObject();
            mutate(document);
            File.WriteAllText(
                documentPath,
                document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var manifestPath = Path.Combine(staging, "manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var relative = Path.GetRelativePath(staging, documentPath).Replace('\\', '/');
            var manifestFile = manifest["files"]!.AsArray()
                .Select(item => item!.AsObject())
                .Single(item => item["path"]!.GetValue<string>() == relative);
            var documentBytes = File.ReadAllBytes(documentPath);
            manifestFile["size"] = documentBytes.LongLength;
            manifestFile["sha256"] = Convert.ToHexString(
                SHA256.HashData(documentBytes)).ToLowerInvariant();
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllBytes(manifestPath, manifestBytes);

            var headerPath = Path.Combine(staging, "header");
            var header = File.ReadAllBytes(headerPath);
            SHA256.HashData(manifestBytes).CopyTo(header, 36);
            File.WriteAllBytes(headerPath, header);

            File.Delete(package);
            using var stream = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var ordered = new[] { headerPath, manifestPath }.Concat(
                Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                    .Where(path => path != headerPath && path != manifestPath)
                    .OrderBy(path => Path.GetRelativePath(staging, path), StringComparer.Ordinal));
            foreach (var path in ordered)
            {
                var entryName = Path.GetRelativePath(staging, path).Replace('\\', '/');
                var entry = archive.CreateEntry(entryName,
                    entryName == "header" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var input = File.OpenRead(path);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        finally
        {
            Directory.Delete(staging, true);
        }
    }

    private static void RewriteManifestDocument(
        string package,
        Action<JsonObject> mutate)
    {
        var staging = Path.Combine(
            Path.GetDirectoryName(package)!,
            $"rewrite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(package, staging);
            var manifestPath = Path.Combine(staging, "manifest.json");
            var manifest = JsonNode.Parse(
                File.ReadAllText(manifestPath))!.AsObject();
            mutate(manifest);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllBytes(manifestPath, manifestBytes);

            var headerPath = Path.Combine(staging, "header");
            var header = File.ReadAllBytes(headerPath);
            SHA256.HashData(manifestBytes).CopyTo(header, 36);
            File.WriteAllBytes(headerPath, header);

            File.Delete(package);
            using var stream = new FileStream(
                package,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var ordered = new[] { headerPath, manifestPath }.Concat(
                Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                    .Where(path => path != headerPath && path != manifestPath)
                    .OrderBy(
                        path => Path.GetRelativePath(staging, path),
                        StringComparer.Ordinal));
            foreach (var path in ordered)
            {
                var entryName = Path.GetRelativePath(staging, path).Replace('\\', '/');
                var entry = archive.CreateEntry(
                    entryName,
                    entryName == "header"
                        ? CompressionLevel.NoCompression
                        : CompressionLevel.Optimal);
                using var input = File.OpenRead(path);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        finally
        {
            Directory.Delete(staging, true);
        }
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
