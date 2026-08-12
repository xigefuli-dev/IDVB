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
public sealed class SurveyLayerEditingTests
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

    [Fact]
    public async Task MagicAlignmentUsesCurrentDisplayAssetsAndCommitsPartialSuccessOnce()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SurveyStoragePaths(root);
            var repository = new SqliteSurveyProjectRepository(paths);
            var assets = new ContentAddressedSurveyAssetStore(paths);
            var registrar = new RecordingRegistrar();
            var preprocessor = new FixedDisplayPreprocessor();
            await using var coordinator = new SurveyCoordinator(
                repository,
                assets,
                preprocessor,
                registrar,
                null,
                new SurveyRegistrationTuning());
            var matchId = Guid.NewGuid();
            var current = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), matchId, 3, "S1", "1f", "alignment test",
                new string('b', 64), "test"))).Value!;
            var rawBytes = CreateSolidPng(64, 64, Scalar.White);
            var commits = new List<SurveyObservationCommitResult>();
            for (var toggle = 1; toggle <= 4; toggle++)
            {
                var committed = (await coordinator.AddObservationAsync(CreateObservation(
                    current, rawBytes, matchId, 3, toggle))).Value!;
                commits.Add(committed);
                current = committed.Snapshot;
            }
            preprocessor.DisplayAsset = await assets.PutAsync(
                current.Project.ProjectId,
                CreateFrame(CreateSolidPng(64, 64, new Scalar(30, 30, 30)), matchId, 3, 20));
            current = (await coordinator.ToggleLayerDecontaminationAsync(
                new SurveyLayerDecontaminationRequest(
                    Guid.NewGuid(), current.Project.ProjectId,
                    commits[1].Layer.LayerId, current.Project.Revision))).Value!;
            current = (await coordinator.EditLayerAsync(new SurveyLayerEditRequest(
                Guid.NewGuid(), current.Project.ProjectId, commits[0].Layer.LayerId,
                current.Project.Revision, IsLocked: true))).Value!;
            current = (await coordinator.EditLayerAsync(new SurveyLayerEditRequest(
                Guid.NewGuid(), current.Project.ProjectId, commits[3].Layer.LayerId,
                current.Project.Revision, IsLocked: true))).Value!;

            registrar.AcceptedObservationId = commits[1].Observation.ObservationId;
            var beforeRevision = current.Project.Revision;
            var aligned = (await coordinator.AlignLayersAsync(new SurveyLayerAlignmentRequest(
                Guid.NewGuid(),
                current.Project.ProjectId,
                beforeRevision,
                commits[0].Layer.LayerId,
                commits.Select(item => item.Layer.LayerId).ToArray()))).Value!;

            Assert.Equal(beforeRevision + 1, aligned.Snapshot.Project.Revision);
            Assert.Equal(3, aligned.Items.Count);
            Assert.Single(aligned.Items, item => item.Succeeded);
            Assert.Equal(2, registrar.Requests.Count);
            var cleanedRequest = registrar.Requests.Single(
                item => item.SourceObservation.ObservationId == commits[1].Observation.ObservationId);
            Assert.Equal(preprocessor.DisplayAsset!.Sha256, cleanedRequest.SourceImageAsset!.Sha256);
            var rawRequest = registrar.Requests.Single(
                item => item.SourceObservation.ObservationId == commits[2].Observation.ObservationId);
            Assert.Equal(commits[2].Observation.SourceAsset.Sha256, rawRequest.SourceImageAsset!.Sha256);
            Assert.All(registrar.Requests, request =>
                Assert.Equal(commits[0].Observation.SourceAsset.Sha256, request.TargetImageAsset!.Sha256));
            Assert.Equal(
                new SurveyLayerTransform(12, -7, 2, 1, 1),
                aligned.Snapshot.Layers.Single(item => item.LayerId == commits[1].Layer.LayerId)
                    .ManualTransformOverride);
            var registered = aligned.Snapshot.Observations.Single(
                item => item.ObservationId == commits[1].Observation.ObservationId);
            Assert.Equal(SurveyObservationState.Registered, registered.State);
            Assert.Equal(SurveyErrorCode.None, registered.ErrorCode);
            Assert.Null(aligned.Snapshot.Layers.Single(
                item => item.LayerId == commits[2].Layer.LayerId).ManualTransformOverride);
            Assert.Null(aligned.Snapshot.Layers.Single(
                item => item.LayerId == commits[3].Layer.LayerId).ManualTransformOverride);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SurveyObservationRequest CreateObservation(
        SurveyProjectSnapshot project,
        byte[] bytes,
        Guid matchId,
        long epoch,
        long toggle) => new(
        Guid.NewGuid(),
        project.Project.ProjectId,
        project.Project.Revision,
        CreateFrame(bytes, matchId, epoch, toggle));

    private static SurveyEncodedFrame CreateFrame(
        byte[] bytes,
        Guid matchId,
        long epoch,
        long toggle) => new(
        bytes,
        ".png",
        "image/png",
        64,
        64,
        new SurveyCaptureContext(
            matchId, epoch, toggle, DateTimeOffset.UtcNow,
            1920, 1080, 120,
            new SurveyPixelRect(0, 0, 64, 64),
            "1f", new string('c', 64), "test"));

    private static byte[] CreateSolidPng(int width, int height, Scalar color)
    {
        using var image = new Mat(new Size(width, height), MatType.CV_8UC3, color);
        Cv2.ImEncode(".png", image, out var bytes);
        return bytes;
    }

    private static async Task<Mat> ReadRenderedLayerAsync(
        ISurveyCoordinator coordinator,
        Guid projectId,
        Guid layerId)
    {
        await using var stream = await coordinator.OpenRenderedLayerAsync(projectId, layerId);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return Cv2.ImDecode(memory.ToArray(), ImreadModes.Unchanged);
    }

    private static async Task<Mat> ReadAssetAsync(
        ISurveyAssetStore assets,
        Guid projectId,
        SurveyAssetReference asset,
        ImreadModes mode)
    {
        await using var stream = await assets.OpenReadAsync(projectId, asset);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return Cv2.ImDecode(memory.ToArray(), mode);
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry)
    {
        await using var input = entry.Open();
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "idvb-survey-layer-editing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FixedDisplayPreprocessor : ISurveyPreprocessor
    {
        public SurveyAssetReference? DisplayAsset { get; set; }

        public Task<SurveyPreprocessResult> ProcessAsync(
            SurveyPreprocessRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new SurveyPreprocessResult(
            null, null, 1, true, null, DisplayAsset));
    }

    private sealed class RecordingRasterEditor : ISurveyLayerRasterEditor
    {
        public int NormalizeCallCount { get; private set; }

        public Task<SurveyAssetReference> NormalizeColorsAsync(
            Guid projectId,
            SurveyMapLayer layer,
            SurveyObservation observation,
            SurveyMapLayer anchorLayer,
            SurveyObservation anchorObservation,
            CancellationToken cancellationToken = default)
        {
            NormalizeCallCount++;
            return Task.FromException<SurveyAssetReference>(
                new InvalidOperationException("Stale work should have been rejected."));
        }

        public Task<SurveyAssetReference?> ApplyHiddenMaskAsync(
            Guid projectId,
            SurveyMapLayer layer,
            SurveyObservation observation,
            IReadOnlyList<SurveyWorldPoint> worldPoints,
            double size,
            SurveyBrushShape shape,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReadOnlyMemory<byte>> RenderLayerAsync(
            Guid projectId,
            SurveyMapLayer layer,
            SurveyObservation observation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRegistrar : ISurveyPairRegistrar
    {
        public Guid AcceptedObservationId { get; set; }
        public List<SurveyRegistrationRequest> Requests { get; } = [];

        public Task<SurveyRegistrationResult> RegisterAsync(
            SurveyRegistrationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var accepted = request.SourceObservation.ObservationId == AcceptedObservationId;
            return Task.FromResult(new SurveyRegistrationResult(
                accepted,
                accepted ? new SurveyLayerTransform(12, -7, 2, 1, 1) : SurveyLayerTransform.Identity,
                accepted ? 0.95 : 0,
                accepted ? 0.5 : double.PositiveInfinity,
                accepted ? 30 : 0,
                "test",
                "1",
                accepted ? null : "rejected for test"));
        }
    }
}
