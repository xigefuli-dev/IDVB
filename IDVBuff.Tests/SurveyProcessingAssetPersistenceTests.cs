using IDVBuff.Survey.Application;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using IDVBuff.Survey.Idvm;
using IDVBuff.Survey.Persistence.Sqlite;
using IDVBuff.Survey.Preprocessing.OpenCv;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class SurveyProcessingAssetPersistenceTests
{
    [Fact]
    public async Task VisibleAwareDisplayAssetMakesFogAndUnseenPixelsTransparent()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "idvb-survey-visible-mask",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projectId = Guid.NewGuid();
            var assets = new ContentAddressedSurveyAssetStore(new SurveyStoragePaths(root));
            var capture = new SurveyCaptureContext(
                Guid.NewGuid(), 1, 1, DateTimeOffset.UtcNow,
                1280, 720, 120,
                new SurveyPixelRect(0, 0, 160, 120),
                "1f", new string('c', 64), "test");
            using var source = new Mat(new Size(160, 120), MatType.CV_8UC3, new Scalar(34, 34, 34));
            Cv2.Rectangle(source, new Rect(0, 0, 35, 120), new Scalar(210, 210, 210), -1);
            Cv2.Rectangle(source, new Rect(50, 34, 68, 58), new Scalar(68, 82, 100), -1);
            Cv2.Line(source, new Point(58, 54), new Point(110, 54), Scalar.White, 2);
            Cv2.Line(source, new Point(82, 40), new Point(82, 85), Scalar.Black, 3);
            Cv2.Line(source, new Point(50, 103), new Point(120, 103), new Scalar(255, 255, 0), 2);
            Cv2.Circle(source, new Point(122, 96), 3, new Scalar(68, 82, 100), -1);
            Cv2.ImEncode(".png", source, out var bytes);
            var sourceAsset = await assets.PutAsync(projectId, new SurveyEncodedFrame(
                bytes, ".png", "image/png", source.Width, source.Height, capture));
            var observation = new SurveyObservation(
                Guid.NewGuid(), projectId, Guid.NewGuid(), "visible-mask", capture, sourceAsset,
                SurveyObservationState.Captured, 0d, SurveyErrorCode.None, null, null, null);
            var preprocessor = new OpenCvSurveyPreprocessor(
                assets,
                new SurveyPreprocessingTuning { MinimumShapeComponentAreaRatio = 0.00008d });

            var result = await preprocessor.ProcessAsync(new SurveyPreprocessRequest(projectId, observation));

            Assert.NotNull(result.DisplayAsset);
            Assert.NotNull(result.VisibleMaskAsset);
            Assert.NotEqual(sourceAsset.Sha256, result.DisplayAsset.Sha256);
            using var mask = await ReadAssetAsync(assets, projectId, result.VisibleMaskAsset, ImreadModes.Grayscale);
            using var display = await ReadAssetAsync(assets, projectId, result.DisplayAsset, ImreadModes.Unchanged);
            Assert.Equal(0, mask.At<byte>(5, 5));
            Assert.Equal(0, mask.At<byte>(60, 10));
            Assert.Equal(0, mask.At<byte>(103, 80));
            Assert.Equal(0, mask.At<byte>(96, 122));
            Assert.True(mask.At<byte>(60, 60) > 0);
            Assert.Equal(4, display.Channels());
            Assert.Equal(0, display.At<Vec4b>(5, 5)[3]);
            Assert.True(display.At<Vec4b>(60, 60)[3] > 0);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RootDerivedAssetsSurviveRestartAndIdvmRoundTrip()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "idvb-survey-derived-assets",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new SurveyStoragePaths(root);
            var repository = new SqliteSurveyProjectRepository(paths);
            var assets = new ContentAddressedSurveyAssetStore(paths);
            var preprocessor = new OpenCvSurveyPreprocessor(
                assets,
                new SurveyPreprocessingTuning());
            await using var coordinator = new SurveyCoordinator(
                repository,
                assets,
                preprocessor,
                registrar: null,
                poseGraph: null,
                new SurveyRegistrationTuning());
            var matchId = Guid.NewGuid();
            var started = (await coordinator.StartAsync(new SurveyStartRequest(
                Guid.NewGuid(), matchId, 1, "S1", "1f", "derived asset test",
                new string('a', 64), "test"))).Value!;
            var frameBytes = CreateFeatureRichFrame();
            var committed = (await coordinator.AddObservationAsync(
                new SurveyObservationRequest(
                    Guid.NewGuid(),
                    started.Project.ProjectId,
                    started.Project.Revision,
                    new SurveyEncodedFrame(
                        frameBytes,
                        ".png",
                        "image/png",
                        320,
                        240,
                        new SurveyCaptureContext(
                            matchId, 1, 1, DateTimeOffset.UtcNow,
                            1920, 1080, 120,
                            new SurveyPixelRect(0, 0, 320, 240),
                            "1f", new string('b', 64), "test"))))).Value!;

            Assert.Null(committed.Observation.StructureAsset);
            Assert.Null(committed.Observation.FeatureAsset);
            Assert.Null(committed.Observation.DisplayAsset);
            Assert.Null(committed.Observation.VisibleMaskAsset);
            Assert.False(committed.Layer.UsesCleanedDisplay);

            var decontaminated = (await coordinator.ToggleLayerDecontaminationAsync(
                new SurveyLayerDecontaminationRequest(
                    Guid.NewGuid(),
                    started.Project.ProjectId,
                    committed.Layer.LayerId,
                    committed.Snapshot.Project.Revision))).Value!;
            var processedLayer = Assert.Single(decontaminated.Layers);
            var processedObservation = Assert.Single(decontaminated.Observations);
            Assert.True(processedLayer.UsesCleanedDisplay);
            Assert.NotNull(processedObservation.StructureAsset);
            Assert.NotNull(processedObservation.FeatureAsset);
            Assert.NotNull(processedObservation.DisplayAsset);
            Assert.NotNull(processedObservation.VisibleMaskAsset);
            Assert.True(processedObservation.Quality > 0d);

            var originalDisplayAsset = processedObservation.DisplayAsset;
            var rawDisplay = (await coordinator.ToggleLayerDecontaminationAsync(
                new SurveyLayerDecontaminationRequest(
                    Guid.NewGuid(),
                    started.Project.ProjectId,
                    processedLayer.LayerId,
                    decontaminated.Project.Revision))).Value!;
            Assert.False(Assert.Single(rawDisplay.Layers).UsesCleanedDisplay);
            Assert.Equal(
                originalDisplayAsset!.Sha256,
                Assert.Single(rawDisplay.Observations).DisplayAsset!.Sha256);
            var cleanedAgain = (await coordinator.ToggleLayerDecontaminationAsync(
                new SurveyLayerDecontaminationRequest(
                    Guid.NewGuid(),
                    started.Project.ProjectId,
                    processedLayer.LayerId,
                    rawDisplay.Project.Revision))).Value!;
            Assert.True(Assert.Single(cleanedAgain.Layers).UsesCleanedDisplay);
            Assert.Equal(
                originalDisplayAsset.Sha256,
                Assert.Single(cleanedAgain.Observations).DisplayAsset!.Sha256);

            var restarted = new SqliteSurveyProjectRepository(paths);
            await restarted.InitializeAsync();
            var restored = await restarted.GetAsync(started.Project.ProjectId);
            var restoredObservation = Assert.Single(restored!.Observations);
            Assert.Equal(
                processedObservation.StructureAsset!.Sha256,
                restoredObservation.StructureAsset!.Sha256);
            Assert.Equal(
                processedObservation.FeatureAsset!.Sha256,
                restoredObservation.FeatureAsset!.Sha256);
            Assert.Equal(
                processedObservation.DisplayAsset!.Sha256,
                restoredObservation.DisplayAsset!.Sha256);
            Assert.Equal(
                processedObservation.VisibleMaskAsset!.Sha256,
                restoredObservation.VisibleMaskAsset!.Sha256);
            await AssertAssetReadableAsync(assets, restored.Project.ProjectId, restoredObservation.StructureAsset);
            await AssertAssetReadableAsync(assets, restored.Project.ProjectId, restoredObservation.FeatureAsset);

            var packages = new SurveyIdvmPackageService(restarted, assets);
            using var package = new MemoryStream();
            await packages.ExportProjectAsync(restored.Project.ProjectId, package);
            package.Position = 0;
            var imported = await packages.ImportProjectAsync(package);
            var importedObservation = Assert.Single(imported.Observations);
            Assert.Equal(restoredObservation.StructureAsset.Sha256, importedObservation.StructureAsset!.Sha256);
            Assert.Equal(restoredObservation.FeatureAsset.Sha256, importedObservation.FeatureAsset!.Sha256);
            Assert.Equal(restoredObservation.DisplayAsset.Sha256, importedObservation.DisplayAsset!.Sha256);
            Assert.Equal(restoredObservation.VisibleMaskAsset.Sha256, importedObservation.VisibleMaskAsset!.Sha256);
            await AssertAssetReadableAsync(assets, imported.Project.ProjectId, importedObservation.StructureAsset);
            await AssertAssetReadableAsync(assets, imported.Project.ProjectId, importedObservation.FeatureAsset);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertAssetReadableAsync(
        ISurveyAssetStore assets,
        Guid projectId,
        SurveyAssetReference asset)
    {
        await using var stream = await assets.OpenReadAsync(projectId, asset);
        Assert.True(stream.Length > 0);
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

    private static byte[] CreateFeatureRichFrame()
    {
        using var image = new Mat(new Size(320, 240), MatType.CV_8UC3, new Scalar(24, 27, 30));
        var corridor = new Scalar(105, 91, 80);
        var room = new Scalar(68, 82, 100);
        Cv2.Rectangle(image, new Rect(92, 66, 72, 70), corridor, -1);
        Cv2.Rectangle(image, new Rect(153, 91, 72, 24), corridor, -1);
        Cv2.Rectangle(image, new Rect(214, 72, 39, 74), room, -1);
        Cv2.Rectangle(image, new Rect(132, 126, 25, 66), corridor, -1);
        Cv2.Rectangle(image, new Rect(105, 179, 52, 26), room, -1);
        Cv2.Rectangle(image, new Rect(226, 95, 16, 25), new Scalar(24, 27, 30), -1);
        Cv2.Rectangle(image, new Rect(108, 82, 12, 9), new Scalar(76, 88, 108), -1);
        Cv2.Rectangle(image, new Rect(137, 104, 10, 12), new Scalar(71, 86, 105), -1);
        Cv2.ImEncode(".png", image, out var bytes);
        return bytes;
    }
}
