using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using IDVBuff.Survey.Application;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using IDVBuff.Survey.Fusion.OpenCv;
using IDVBuff.Survey.Idvm;
using IDVBuff.Survey.Persistence.Sqlite;
using IDVBuff.Survey.Preprocessing.OpenCv;
using OpenCvSharp;

namespace IDVBuff.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SurveyLayerEditingTestCollection
{
    public const string Name = "Survey layer editing serial tests";
}

[Collection(SurveyLayerEditingTestCollection.Name)]
public sealed partial class SurveyLayerEditingTests
{
    [Fact]
    public async Task ColorNormalizationIsVisibleAndKeepsOriginalAssets()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SurveyStoragePaths(root);
            var repository = new SqliteSurveyProjectRepository(paths);
            var assets = new ContentAddressedSurveyAssetStore(paths);
            await using var coordinator = new SurveyCoordinator(
                repository, assets, null, null, null, new SurveyRegistrationTuning(),
                null, null, new OpenCvSurveyLayerRasterEditor(assets));
            var matchId = Guid.NewGuid();
            var started = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), matchId, 1, "S1", "1f", "color test",
                new string('a', 64), "test"))).Value!;
            var anchor = (await coordinator.AddObservationAsync(CreateObservation(
                started, CreateSolidPng(64, 64, new Scalar(40, 100, 210)), matchId, 1, 1))).Value!;
            var target = (await coordinator.AddObservationAsync(CreateObservation(
                anchor.Snapshot, CreateSolidPng(64, 64, new Scalar(190, 80, 30)), matchId, 1, 2))).Value!;
            var targetSource = target.Observation.SourceAsset;

            var result = (await coordinator.NormalizeLayerColorsAsync(
                new SurveyLayerColorNormalizationRequest(
                    Guid.NewGuid(), target.Snapshot.Project.ProjectId,
                    target.Snapshot.Project.Revision, anchor.Layer.LayerId,
                    [anchor.Layer.LayerId, target.Layer.LayerId]))).Value!;

            var normalizedLayer = result.Snapshot.Layers.Single(layer => layer.LayerId == target.Layer.LayerId);
            var normalizedObservation = result.Snapshot.Observations.Single(
                observation => observation.ObservationId == target.Observation.ObservationId);
            Assert.NotNull(normalizedLayer.ColorFilterAsset);
            Assert.Equal(targetSource.Sha256, normalizedObservation.SourceAsset.Sha256);
            Assert.NotEqual(targetSource.Sha256, normalizedLayer.ColorFilterAsset!.Sha256);
            using var rendered = await ReadRenderedLayerAsync(
                coordinator, result.Snapshot.Project.ProjectId, target.Layer.LayerId);
            var pixel = rendered.At<Vec4b>(32, 32);
            Assert.InRange(pixel.Item0, (byte)35, (byte)45);
            Assert.InRange(pixel.Item1, (byte)95, (byte)105);
            Assert.InRange(pixel.Item2, (byte)205, (byte)215);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ColorTemplateUsesSemanticPaletteAndKeepsOriginalAssets()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SurveyStoragePaths(root);
            var repository = new SqliteSurveyProjectRepository(paths);
            var assets = new ContentAddressedSurveyAssetStore(paths);
            await using var coordinator = new SurveyCoordinator(
                repository, assets, null, null, null, new SurveyRegistrationTuning(),
                null, null, new OpenCvSurveyLayerRasterEditor(assets));
            var matchId = Guid.NewGuid();
            var started = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), matchId, 1, "S1", "1f", "template test",
                new string('a', 64), "test"))).Value!;
            var committed = (await coordinator.AddObservationAsync(CreateObservation(
                started, CreateSolidPng(32, 32, new Scalar(10, 20, 30)), matchId, 1, 1))).Value!;
            var sourceSha = committed.Observation.SourceAsset.Sha256;
            var result = (await coordinator.ApplyColorTemplateAsync(
                new SurveyLayerColorTemplateRequest(
                    Guid.NewGuid(),
                    committed.Snapshot.Project.ProjectId,
                    committed.Snapshot.Project.Revision,
                    [committed.Layer.LayerId],
                    [new SurveyColorTemplateEntry(220, 120, 35, SurveyTemplateColorType.Fill)]))).Value!;

            var layer = result.Snapshot.Layers.Single(item => item.LayerId == committed.Layer.LayerId);
            Assert.NotNull(layer.ColorFilterAsset);
            Assert.Equal(sourceSha, result.Snapshot.Observations
                .Single(item => item.ObservationId == committed.Observation.ObservationId)
                .SourceAsset.Sha256);
            using var rendered = await ReadRenderedLayerAsync(
                coordinator, result.Snapshot.Project.ProjectId, committed.Layer.LayerId);
            var pixel = rendered.At<Vec4b>(16, 16);
            Assert.Equal(new Vec4b(35, 120, 220, 255), pixel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ColorTemplateAppliesToSelectedLayersOnceIncludingLockedLayers()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SurveyStoragePaths(root);
            var repository = new SqliteSurveyProjectRepository(paths);
            var assets = new ContentAddressedSurveyAssetStore(paths);
            await using var coordinator = new SurveyCoordinator(
                repository, assets, null, null, null, new SurveyRegistrationTuning(),
                null, null, new OpenCvSurveyLayerRasterEditor(assets));
            var matchId = Guid.NewGuid();
            var started = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), matchId, 1, "S1", "1f", "template batch test",
                new string('a', 64), "test"))).Value!;
            var first = (await coordinator.AddObservationAsync(CreateObservation(
                started, CreateSolidPng(32, 32, new Scalar(10, 20, 30)), matchId, 1, 1))).Value!;
            var second = (await coordinator.AddObservationAsync(CreateObservation(
                first.Snapshot, CreateSolidPng(32, 32, new Scalar(40, 50, 60)), matchId, 1, 2))).Value!;
            var locked = (await coordinator.EditLayerAsync(new SurveyLayerEditRequest(
                Guid.NewGuid(),
                second.Snapshot.Project.ProjectId,
                second.Layer.LayerId,
                second.Snapshot.Project.Revision,
                IsLocked: true))).Value!;
            var beforeRevision = locked.Project.Revision;

            var result = (await coordinator.ApplyColorTemplateAsync(
                new SurveyLayerColorTemplateRequest(
                    Guid.NewGuid(),
                    locked.Project.ProjectId,
                    beforeRevision,
                    [first.Layer.LayerId, second.Layer.LayerId],
                    [new SurveyColorTemplateEntry(220, 120, 35, SurveyTemplateColorType.Fill)]))).Value!;

            Assert.Equal(beforeRevision + 1, result.Snapshot.Project.Revision);
            Assert.Equal(2, result.Items.Count);
            Assert.All(result.Items, item => Assert.True(item.Succeeded));
            Assert.NotNull(result.Snapshot.Layers.Single(item => item.LayerId == first.Layer.LayerId).ColorFilterAsset);
            Assert.NotNull(result.Snapshot.Layers.Single(item => item.LayerId == second.Layer.LayerId).ColorFilterAsset);
            Assert.True(result.Snapshot.Layers.Single(item => item.LayerId == second.Layer.LayerId).IsLocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompositePixelSamplerHonorsZOrderTransparencyOpacityTransformsAndVisibility()
    {
        var bottom = new SurveyCompositeLayerPixel(
            1,
            IsVisible: true,
            IsDeleted: false,
            Opacity: 1d,
            SurveyLayerTransform.Identity,
            10,
            10,
            new SurveyRasterPixel(220, 20, 10, 255));
        var transparentTop = bottom with
        {
            ZOrder = 2,
            Pixel = new SurveyRasterPixel(10, 220, 20, 0)
        };
        Assert.Equal(
            new SurveyRasterPixel(220, 20, 10, 255),
            SurveyCompositePixelSampler.Composite(new SurveyWorldPoint(4, 4),
                [transparentTop, bottom]));

        var halfTransparentTop = transparentTop with
        {
            Pixel = new SurveyRasterPixel(20, 220, 20, 255),
            Opacity = 0.5d
        };
        Assert.Equal(
            new SurveyRasterPixel(120, 120, 15, 255),
            SurveyCompositePixelSampler.Composite(new SurveyWorldPoint(4, 4),
                [halfTransparentTop, bottom]));

        var transformed = new SurveyCompositeLayerPixel(
            3,
            IsVisible: true,
            IsDeleted: false,
            Opacity: 1d,
            new SurveyLayerTransform(5, 5, 90, 2, 1),
            4,
            4,
            new SurveyRasterPixel(30, 40, 230, 255));
        var transformedWorldPoint = transformed.Transform.Transform(new SurveyWorldPoint(1, 2));
        Assert.Equal(
            new SurveyRasterPixel(30, 40, 230, 255),
            SurveyCompositePixelSampler.Composite(transformedWorldPoint,
                [transformed, bottom]));

        var ignored = new[]
        {
            bottom with { ZOrder = 4, IsVisible = false, Pixel = new SurveyRasterPixel(1, 2, 3, 255) },
            bottom with { ZOrder = 5, IsDeleted = true, Pixel = new SurveyRasterPixel(4, 5, 6, 255) },
            bottom with { ZOrder = 6, Opacity = 0d, Pixel = new SurveyRasterPixel(7, 8, 9, 255) }
        };
        Assert.Equal(
            new SurveyRasterPixel(220, 20, 10, 255),
            SurveyCompositePixelSampler.Composite(new SurveyWorldPoint(4, 4), ignored.Append(bottom)));
        Assert.Null(SurveyCompositePixelSampler.Composite(new SurveyWorldPoint(100, 100), [bottom]));
    }

    [Fact]
    public async Task ColorFillCreatesColorFilterAssetAndChangesTheRenderedLayer()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SurveyStoragePaths(root);
            var repository = new SqliteSurveyProjectRepository(paths);
            var assets = new ContentAddressedSurveyAssetStore(paths);
            await using var coordinator = new SurveyCoordinator(
                repository, assets, null, null, null, new SurveyRegistrationTuning(),
                null, null, new OpenCvSurveyLayerRasterEditor(assets));
            var matchId = Guid.NewGuid();
            var started = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), matchId, 1, "S1", "1f", "fill test",
                new string('a', 64), "test"))).Value!;
            var committed = (await coordinator.AddObservationAsync(CreateObservation(
                started, CreateSolidPng(32, 32, new Scalar(10, 20, 30)), matchId, 1, 1))).Value!;
            var result = (await coordinator.ApplyColorFillAsync(new SurveyColorFillRequest(
                Guid.NewGuid(), committed.Snapshot.Project.ProjectId, committed.Layer.LayerId,
                committed.Snapshot.Project.Revision, 16, 16, 0, new SurveyColor(220, 120, 35)))).Value!;
            var layer = result.Snapshot.Layers.Single(item => item.LayerId == committed.Layer.LayerId);
            Assert.NotNull(layer.ColorFilterAsset);
            Assert.NotEqual(committed.Layer.ColorFilterAsset?.Sha256, layer.ColorFilterAsset!.Sha256);
            using var rendered = await ReadRenderedLayerAsync(
                coordinator, result.Snapshot.Project.ProjectId, committed.Layer.LayerId);
            Assert.Equal(new Vec4b(35, 120, 220, 255), rendered.At<Vec4b>(16, 16));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StaleColorNormalizationIsRejectedBeforeRasterWorkStarts()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SurveyStoragePaths(root);
            var repository = new SqliteSurveyProjectRepository(paths);
            var assets = new ContentAddressedSurveyAssetStore(paths);
            var rasterEditor = new RecordingRasterEditor();
            await using var coordinator = new SurveyCoordinator(
                repository, assets, null, null, null, new SurveyRegistrationTuning(),
                null, null, rasterEditor);
            var matchId = Guid.NewGuid();
            var started = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), matchId, 1, "S1", "1f", "stale color test",
                new string('a', 64), "test"))).Value!;
            var anchor = (await coordinator.AddObservationAsync(CreateObservation(
                started, CreateSolidPng(64, 64, Scalar.White), matchId, 1, 1))).Value!;
            var target = (await coordinator.AddObservationAsync(CreateObservation(
                anchor.Snapshot, CreateSolidPng(64, 64, new Scalar(128, 128, 128)), matchId, 1, 2))).Value!;
            var staleRevision = target.Snapshot.Project.Revision;
            var advanced = (await coordinator.EditLayerAsync(new SurveyLayerEditRequest(
                Guid.NewGuid(),
                target.Snapshot.Project.ProjectId,
                target.Layer.LayerId,
                staleRevision,
                Name: "revision advanced"))).Value!;

            var result = await coordinator.NormalizeLayerColorsAsync(
                new SurveyLayerColorNormalizationRequest(
                    Guid.NewGuid(),
                    advanced.Project.ProjectId,
                    staleRevision,
                    anchor.Layer.LayerId,
                    [anchor.Layer.LayerId, target.Layer.LayerId]));

            Assert.False(result.Succeeded);
            Assert.Equal(SurveyErrorCode.RevisionConflict, result.ErrorCode);
            Assert.Equal(0, rasterEditor.NormalizeCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrushMasksAreAtomicNonDestructiveAndRoundTripThroughIdvm12()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SurveyStoragePaths(root);
            var repository = new SqliteSurveyProjectRepository(paths);
            var assets = new ContentAddressedSurveyAssetStore(paths);
            await using var coordinator = new SurveyCoordinator(
                repository,
                assets,
                null,
                null,
                null,
                new SurveyRegistrationTuning(),
                null,
                null,
                new OpenCvSurveyLayerRasterEditor(assets));
            var matchId = Guid.NewGuid();
            var started = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), matchId, 1, "S1", "1f", "mask test",
                new string('a', 64), "test"))).Value!;
            var sourceBytes = CreateSolidPng(64, 64, new Scalar(240, 240, 240));
            var first = (await coordinator.AddObservationAsync(CreateObservation(
                started, sourceBytes, matchId, 1, 1))).Value!;
            var second = (await coordinator.AddObservationAsync(CreateObservation(
                first.Snapshot, sourceBytes, matchId, 1, 2))).Value!;

            var transformed = new SurveyLayerTransform(48, -16, 90, 1.5, 0.5);
            var edited = (await coordinator.EditLayerAsync(new SurveyLayerEditRequest(
                Guid.NewGuid(),
                started.Project.ProjectId,
                second.Layer.LayerId,
                second.Snapshot.Project.Revision,
                ManualTransformOverride: transformed))).Value!;
            Assert.Equal(
                new SurveyWorldPoint(32, 32),
                transformed.Transform(new SurveyWorldPoint(32, 32)));

            var beforeRevision = edited.Project.Revision;
            var circle = (await coordinator.ApplyMaskStrokeAsync(new SurveyMaskStrokeRequest(
                Guid.NewGuid(),
                edited.Project.ProjectId,
                beforeRevision,
                first.Layer.FloorId,
                [first.Layer.LayerId, second.Layer.LayerId],
                [new SurveyWorldPoint(32, 32)],
                20,
                SurveyBrushShape.Circle))).Value!;
            Assert.Equal(beforeRevision + 1, circle.Snapshot.Project.Revision);
            Assert.All(circle.Items, item => Assert.True(item.Succeeded));
            Assert.All(circle.Snapshot.Layers, layer => Assert.NotNull(layer.HiddenMaskAsset));
            Assert.Equal(
                first.Observation.SourceAsset.Sha256,
                circle.Snapshot.Observations.Single(item => item.ObservationId == first.Observation.ObservationId)
                    .SourceAsset.Sha256);

            using (var rendered = await ReadRenderedLayerAsync(
                coordinator, edited.Project.ProjectId, first.Layer.LayerId))
            {
                Assert.Equal(0, rendered.At<Vec4b>(32, 32).Item3);
                Assert.Equal(255, rendered.At<Vec4b>(41, 41).Item3);
            }
            using (var transformedRendered = await ReadRenderedLayerAsync(
                coordinator, edited.Project.ProjectId, second.Layer.LayerId))
            {
                Assert.Equal(0, transformedRendered.At<Vec4b>(32, 32).Item3);
            }

            var cleared = (await coordinator.ApplyLayerBatchAsync(new SurveyLayerBatchEditRequest(
                Guid.NewGuid(),
                edited.Project.ProjectId,
                circle.Snapshot.Project.Revision,
                circle.Snapshot.Layers.Select(layer => new SurveyLayerMutation(
                    layer.LayerId,
                    HiddenMaskAsset: null,
                    ReplaceHiddenMask: true)).ToArray()))).Value!;
            var square = (await coordinator.ApplyMaskStrokeAsync(new SurveyMaskStrokeRequest(
                Guid.NewGuid(),
                cleared.Project.ProjectId,
                cleared.Project.Revision,
                first.Layer.FloorId,
                [first.Layer.LayerId, second.Layer.LayerId],
                [new SurveyWorldPoint(32, 32)],
                20,
                SurveyBrushShape.Square))).Value!;
            using (var rendered = await ReadRenderedLayerAsync(
                coordinator, edited.Project.ProjectId, first.Layer.LayerId))
            {
                Assert.Equal(0, rendered.At<Vec4b>(41, 41).Item3);
            }

            var noOp = (await coordinator.ApplyMaskStrokeAsync(new SurveyMaskStrokeRequest(
                Guid.NewGuid(),
                square.Snapshot.Project.ProjectId,
                square.Snapshot.Project.Revision,
                first.Layer.FloorId,
                [first.Layer.LayerId, second.Layer.LayerId],
                [new SurveyWorldPoint(32, 32)],
                20,
                SurveyBrushShape.Square))).Value!;
            Assert.Equal(square.Snapshot.Project.Revision, noOp.Snapshot.Project.Revision);
            Assert.All(noOp.Items, item => Assert.False(item.Succeeded));

            var firstLayer = square.Snapshot.Layers.Single(item => item.LayerId == first.Layer.LayerId);
            var firstObservation = square.Snapshot.Observations.Single(
                item => item.ObservationId == first.Observation.ObservationId);
            var fusionSnapshot = square.Snapshot with
            {
                Observations = [firstObservation with { StructureAsset = firstObservation.SourceAsset }],
                Layers = [firstLayer],
                Constraints = []
            };
            var tuning = new SurveyFusionTuning { MaximumOutputPixels = 1_000_000 };
            var visual = await new OpenCvSurveyVisualComposer(assets, tuning)
                .ComposeAsync(fusionSnapshot, "1f");
            var structure = await new OpenCvSurveyStructureFusion(assets, tuning)
                .FuseAsync(fusionSnapshot, "1f");
            using (var image = await ReadAssetAsync(
                assets, edited.Project.ProjectId, visual.Asset, ImreadModes.Color))
            {
                Assert.Equal(3, image.Channels());
                Assert.Equal(new Vec3b(240, 240, 240), image.At<Vec3b>(32, 32));
                Assert.NotEqual(new Vec3b(0, 0, 0), image.At<Vec3b>(2, 2));
            }
            using (var image = await ReadAssetAsync(
                assets, edited.Project.ProjectId, structure.Asset, ImreadModes.Grayscale))
            {
                Assert.Equal(0, image.At<byte>(32, 32));
                Assert.Equal(255, image.At<byte>(2, 2));
            }

            var packages = new SurveyIdvmPackageService(repository, assets);
            using var package = new MemoryStream();
            await packages.ExportProjectAsync(edited.Project.ProjectId, package);
            package.Position = 0;
            using (var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true))
            {
                var header = await ReadEntryAsync(archive.GetEntry("header")!);
                Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2)));
                var manifestBytes = await ReadEntryAsync(archive.GetEntry("manifest.json")!);
                using var manifest = JsonDocument.Parse(manifestBytes);
                Assert.Equal("1.2", manifest.RootElement.GetProperty("formatVersion").GetString());
                var capabilities = manifest.RootElement.GetProperty("capabilities")
                    .EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert.Contains("survey.display-state", capabilities);
                Assert.Contains("survey.layer-masks", capabilities);
            }
            package.Position = 0;
            var imported = await packages.ImportProjectAsync(package);
            Assert.All(imported.Layers, layer => Assert.NotNull(layer.HiddenMaskAsset));
            Assert.Equal(
                square.Snapshot.Layers.OrderBy(item => item.LayerId).Select(item => item.HiddenMaskAsset!.Sha256),
                imported.Layers.OrderBy(item => item.LayerId).Select(item => item.HiddenMaskAsset!.Sha256));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
