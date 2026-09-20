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

namespace IDVBuff.Tests;public sealed partial class SurveyLayerEditingTests
{

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

        public Task<SurveyAssetReference> CorrectVignetteAsync(
            Guid projectId,
            SurveyMapLayer layer,
            SurveyObservation observation,
            double compensationStart,
            double compensationStrength,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

        public Task<SurveyAssetReference> ApplyColorTemplateAsync(
            Guid projectId,
            SurveyMapLayer layer,
            SurveyObservation observation,
            IReadOnlyList<SurveyColorTemplateEntry> entries,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SurveyAssetReference?> ApplyHiddenMaskAsync(
            Guid projectId,
            SurveyMapLayer layer,
            SurveyObservation observation,
            IReadOnlyList<SurveyWorldPoint> worldPoints,
            double size,
            SurveyBrushShape shape,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SurveyAssetReference?> ApplyColorBrushAsync(
            Guid projectId, SurveyMapLayer layer, SurveyObservation observation,
            IReadOnlyList<SurveyWorldPoint> worldPoints, double size, SurveyBrushShape shape,
            SurveyColor color, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SurveyAssetReference?> ApplyColorFillAsync(
            Guid projectId, SurveyMapLayer layer, SurveyObservation observation,
            int pixelX, int pixelY, byte tolerance, SurveyColor color,
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
