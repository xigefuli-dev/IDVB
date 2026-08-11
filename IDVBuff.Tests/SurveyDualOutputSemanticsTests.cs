using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using IDVBuff.Survey.Fusion.OpenCv;
using IDVBuff.Survey.Persistence.Sqlite;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class SurveyDualOutputSemanticsTests
{
    [Fact]
    public async Task VisualPropertiesDoNotChangeStructureButGeometryDoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "idvb-survey-fusion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projectId = Guid.NewGuid();
            var paths = new SurveyStoragePaths(root);
            var assets = new ContentAddressedSurveyAssetStore(paths);
            var snapshot = await CreateSnapshotAsync(projectId, assets);
            var tuning = new SurveyFusionTuning
            {
                MaximumOutputPixels = 2_000_000,
                StructureBinaryThreshold = 0.5
            };
            var visual = new OpenCvSurveyVisualComposer(assets, tuning);
            var structure = new OpenCvSurveyStructureFusion(assets, tuning);
            var initialVisual = await visual.ComposeAsync(snapshot, "1f");
            var initialStructure = await structure.FuseAsync(snapshot, "1f");

            var visualOnlyChange = snapshot with
            {
                Layers =
                [
                    snapshot.Layers[0] with { Opacity = 0.15, ZOrder = 5, IsVisible = false },
                    snapshot.Layers[1] with { Opacity = 0.65, ZOrder = 0 }
                ]
            };
            var changedVisual = await visual.ComposeAsync(visualOnlyChange, "1f");
            var unchangedStructure = await structure.FuseAsync(visualOnlyChange, "1f");
            Assert.NotEqual(initialVisual.Asset.Sha256, changedVisual.Asset.Sha256);
            Assert.Equal(initialStructure.Asset.Sha256, unchangedStructure.Asset.Sha256);

            var geometryChange = visualOnlyChange with
            {
                Layers =
                [
                    visualOnlyChange.Layers[0],
                    visualOnlyChange.Layers[1] with
                    {
                        ManualTransformOverride = new SurveyLayerTransform(18, 7, 0, 1, 1)
                    }
                ]
            };
            var movedStructure = await structure.FuseAsync(geometryChange, "1f");
            Assert.NotEqual(initialStructure.Asset.Sha256, movedStructure.Asset.Sha256);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SurveyProjectSnapshot> CreateSnapshotAsync(
        Guid projectId,
        ISurveyAssetStore assets)
    {
        var now = DateTimeOffset.UtcNow;
        var capture = new SurveyCaptureContext(
            Guid.NewGuid(), 1, 1, now, 1280, 720, 120,
            new SurveyPixelRect(0, 0, 96, 72), "1f", new string('a', 64), "test");
        var firstAsset = await PutPatternAsync(projectId, assets, capture, flip: false);
        var secondAsset = await PutPatternAsync(projectId, assets, capture, flip: true);
        var floorId = Guid.NewGuid();
        var firstObservation = new SurveyObservation(
            Guid.NewGuid(), projectId, floorId, "first", capture, firstAsset,
            SurveyObservationState.Registered, 1, SurveyErrorCode.None, null, null, null);
        var secondObservation = new SurveyObservation(
            Guid.NewGuid(), projectId, floorId, "second", capture with { MapToggleVersion = 2 }, secondAsset,
            SurveyObservationState.Registered, 1, SurveyErrorCode.None, null, null, null);
        var firstLayer = new SurveyMapLayer(
            Guid.NewGuid(), projectId, floorId, firstObservation.ObservationId, "first", 0,
            true, false, false, 1, SurveyBlendMode.Normal,
            SurveyLayerTransform.Identity, null, 1, 0);
        var secondLayer = new SurveyMapLayer(
            Guid.NewGuid(), projectId, floorId, secondObservation.ObservationId, "second", 1,
            true, false, false, 1, SurveyBlendMode.Normal,
            SurveyLayerTransform.Identity, null, 1, 0);
        return new SurveyProjectSnapshot(
            new SurveyProject(
                projectId, 3, "fusion", "S1", SurveyProjectState.NeedsReview,
                now, now, 1, new string('a', 64), "test", "1f", null),
            [new SurveyFloor(floorId, "1f", "1F", 1, firstLayer.LayerId, null)],
            [firstObservation, secondObservation],
            [firstLayer, secondLayer],
            []);
    }

    private static async Task<SurveyAssetReference> PutPatternAsync(
        Guid projectId,
        ISurveyAssetStore assets,
        SurveyCaptureContext capture,
        bool flip)
    {
        using var image = new Mat(new Size(96, 72), MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, flip ? new Rect(44, 12, 38, 42) : new Rect(8, 8, 45, 30), Scalar.White, 4);
        Cv2.Line(image, flip ? new Point(5, 65) : new Point(18, 60), new Point(90, 45),
            new Scalar(30, 180, 220), 3);
        Cv2.ImEncode(".png", image, out var bytes);
        return await assets.PutAsync(projectId, new SurveyEncodedFrame(
            bytes, ".png", "image/png", image.Width, image.Height, capture));
    }
}
