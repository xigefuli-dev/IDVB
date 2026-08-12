using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IDVBuff.Survey.Application;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using IDVBuff.Survey.Idvm;
using IDVBuff.Survey.Persistence.Sqlite;

namespace IDVBuff.Tests;

public sealed class SurveyIdvmPackageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "idvb-survey-idvm-tests",
        Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SurveyProjectRoundTripPreservesEditableLayerState()
    {
        var paths = new SurveyStoragePaths(_root);
        var repository = new SqliteSurveyProjectRepository(paths);
        var assets = new ContentAddressedSurveyAssetStore(paths);
        await using var coordinator = new SurveyCoordinator(repository, assets);
        var matchId = Guid.NewGuid();
        var start = await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(), matchId, 2, "S1", "1f", "往返测试",
            new string('a', 64), "1.0.0"));
        var project = start.Value!;
        var observation = await coordinator.AddObservationAsync(new SurveyObservationRequest(
            Guid.NewGuid(),
            project.Project.ProjectId,
            project.Project.Revision,
            new SurveyEncodedFrame(
                OnePixelPng,
                ".png",
                "image/png",
                1,
                1,
                new SurveyCaptureContext(
                    matchId, 2, 1, DateTimeOffset.UtcNow, 1920, 1080, 120,
                    new SurveyPixelRect(0, 0, 1, 1), "1f", new string('b', 64), "1.0.0"))));
        var committed = observation.Value!;
        var transform = new SurveyLayerTransform(-80, 42, -7.5, 1.25, 0.8);
        var edited = await coordinator.EditLayerAsync(new SurveyLayerEditRequest(
            Guid.NewGuid(),
            project.Project.ProjectId,
            committed.Layer.LayerId,
            committed.Snapshot.Project.Revision,
            ManualTransformOverride: transform,
            Opacity: 0.37,
            Brightness: 0.65,
            ZOrder: 17,
            IsVisible: false,
            IsLocked: true));
        Assert.True(edited.Succeeded);

        var packages = new SurveyIdvmPackageService(repository, assets);
        using var package = new MemoryStream();
        await packages.ExportProjectAsync(project.Project.ProjectId, package);
        package.Position = 0;
        var imported = await packages.ImportProjectAsync(package);

        Assert.NotEqual(project.Project.ProjectId, imported.Project.ProjectId);
        Assert.Equal(edited.Value!.Project.Revision, imported.Project.Revision);
        Assert.Equal(edited.Value.Project.Name, imported.Project.Name);
        var layer = Assert.Single(imported.Layers);
        Assert.Equal(transform, layer.ManualTransformOverride);
        Assert.Equal(0.37, layer.Opacity, 6);
        Assert.Equal(0.65, layer.Brightness, 6);
        Assert.Equal(17, layer.ZOrder);
        Assert.False(layer.IsVisible);
        Assert.True(layer.IsLocked);
        Assert.Equal(
            Assert.Single(edited.Value.Observations).SourceAsset.Sha256,
            Assert.Single(imported.Observations).SourceAsset.Sha256);
    }

    [Fact]
    public async Task ImportRejectsPathTraversalBeforeWritingAssets()
    {
        var paths = new SurveyStoragePaths(_root);
        var service = new SurveyIdvmPackageService(
            new SqliteSurveyProjectRepository(paths),
            new ContentAddressedSurveyAssetStore(paths));
        using var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("header");
            archive.CreateEntry("manifest.json");
            archive.CreateEntry("../escape.png");
        }
        package.Position = 0;

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportProjectAsync(package));
        Assert.Contains("不安全路径", exception.Message);
    }

    [Fact]
    public async Task Import11DefaultsLayersWithDisplayAssetsToCleanedDisplay()
    {
        var paths = new SurveyStoragePaths(_root);
        var repository = new SqliteSurveyProjectRepository(paths);
        var assets = new ContentAddressedSurveyAssetStore(paths);
        await using var coordinator = new SurveyCoordinator(repository, assets);
        var matchId = Guid.NewGuid();
        var started = (await coordinator.StartAsync(new SurveyStartRequest(
            Guid.NewGuid(), matchId, 1, "S1", "1f", "legacy display",
            new string('d', 64), "test"))).Value!;
        var committed = (await coordinator.AddObservationAsync(new SurveyObservationRequest(
            Guid.NewGuid(),
            started.Project.ProjectId,
            started.Project.Revision,
            new SurveyEncodedFrame(
                OnePixelPng,
                ".png",
                "image/png",
                1,
                1,
                new SurveyCaptureContext(
                    matchId, 1, 1, DateTimeOffset.UtcNow, 1920, 1080, 120,
                    new SurveyPixelRect(0, 0, 1, 1), "1f", new string('e', 64), "test"))))).Value!;
        var withDisplay = await repository.CommitProcessingAsync(new SurveyProcessingCommitRequest(
            Guid.NewGuid(),
            started.Project.ProjectId,
            committed.Observation.ObservationId,
            committed.Layer.LayerId,
            committed.Snapshot.Project.Revision,
            committed.Observation.State,
            committed.Observation.Quality,
            committed.Observation.ErrorCode,
            committed.Observation.ErrorMessage,
            committed.Layer.AutomaticTransform,
            null,
            DisplayAsset: committed.Observation.SourceAsset));
        Assert.False(Assert.Single(withDisplay.Layers).UsesCleanedDisplay);

        var packages = new SurveyIdvmPackageService(repository, assets);
        using var version12 = new MemoryStream();
        await packages.ExportProjectAsync(started.Project.ProjectId, version12);
        using var version11 = await DowngradeTo11Async(version12);
        var imported = await packages.ImportProjectAsync(version11);

        Assert.True(Assert.Single(imported.Layers).UsesCleanedDisplay);
        Assert.NotNull(Assert.Single(imported.Observations).DisplayAsset);
    }

    private static async Task<MemoryStream> DowngradeTo11Async(MemoryStream version12)
    {
        version12.Position = 0;
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var source = new ZipArchive(version12, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                await using var input = entry.Open();
                using var copy = new MemoryStream();
                await input.CopyToAsync(copy);
                entries.Add(entry.FullName, copy.ToArray());
            }
        }

        var manifest = JsonNode.Parse(entries["manifest.json"])!.AsObject();
        manifest["formatVersion"] = "1.1";
        manifest["minimumReaderVersion"] = "1.1";
        var capabilities = manifest["capabilities"]!.AsArray();
        for (var index = capabilities.Count - 1; index >= 0; index--)
        {
            if (capabilities[index]?.GetValue<string>() is "survey.display-state" or "survey.layer-masks")
                capabilities.RemoveAt(index);
        }
        foreach (var layer in manifest["project"]!["layers"]!.AsArray().OfType<JsonObject>())
        {
            layer.Remove("usesCleanedDisplay");
            layer.Remove("hiddenMaskAsset");
        }
        entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        BinaryPrimitives.WriteUInt16LittleEndian(entries["header"].AsSpan(6, 2), 1);
        SHA256.HashData(entries["manifest.json"]).CopyTo(entries["header"], 36);

        var result = new MemoryStream();
        using (var destination = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var pair in entries)
            {
                var entry = destination.CreateEntry(pair.Key, CompressionLevel.Optimal);
                await using var output = entry.Open();
                await output.WriteAsync(pair.Value);
            }
        }
        result.Position = 0;
        return result;
    }

    private static byte[] OnePixelPng { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
