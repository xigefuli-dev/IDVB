using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.IO.Compression;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed partial class IdvmPackageServiceTests
{
    [Fact]
    public async Task PrebuiltStructureLinesUseCroppedImageAndRoundTripThroughIdvm()
    {
        var root = CreateRoot();
        try
        {
            var draft = CreateDraft(root, "prebuilt-source.png", "S1", "预制线图地图");
            using (var hsv = new Mat(100, 160, MatType.CV_8UC3, new Scalar(15, 100, 120)))
            using (var bgr = new Mat())
            {
                Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
                Assert.True(Cv2.ImWrite(draft.FloorOnePath!, bgr));
            }
            draft.Recognition.FirstFloor.RecognitionRegion = new NormalizedRectangle
            {
                X = 0.1,
                Y = 0.1,
                Width = 0.8,
                Height = 0.8
            };
            var algorithmPath = Path.Combine(root, "normal.idva");
            await File.WriteAllTextAsync(algorithmPath, NormalIdva);
            var source = new MapRepository(Path.Combine(root, "source"));
            await source.SaveAsync(draft);
            var reports = new List<PrebuiltStructureBatchProgress>();
            var result = await source.GeneratePrebuiltStructureLinesAsync(
                "S1",
                algorithmPath,
                new InlineProgress<PrebuiltStructureBatchProgress>(reports.Add));
            var generated = Assert.Single((await source.GetCatalogSnapshotAsync()).Maps);
            var asset = Assert.Single(generated.Floors).PrebuiltStructureLine!;
            Assert.Equal(128, asset.Width);
            Assert.Equal(80, asset.Height);
            Assert.True(source.HasCompletePrebuiltStructureLines(generated));
            Assert.Equal(1, result.FloorCount);
            Assert.Contains(reports, report => report.CompletedFloors == 1);

            var firstLineHash = asset.Sha256;
            await File.WriteAllTextAsync(
                algorithmPath,
                NormalIdva.Replace("structure.normal.test", "structure.normal.updated")
                    .Replace("\"line_width_px\":2", "\"line_width_px\":3"));
            await source.GeneratePrebuiltStructureLinesAsync("S1", algorithmPath);
            generated = Assert.Single((await source.GetCatalogSnapshotAsync()).Maps);
            asset = Assert.Single(generated.Floors).PrebuiltStructureLine!;
            Assert.Equal("prebuilt-1f.png", asset.FileName);
            Assert.Equal("prebuilt-structure.idva", asset.AlgorithmFileName);
            Assert.NotEqual(firstLineHash, asset.Sha256);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(Path.Combine(root, "source"), "prebuilt-*", SearchOption.AllDirectories),
                path => Path.GetFileName(path).Contains("-[0-9a-f]", StringComparison.OrdinalIgnoreCase));

            var package = Path.Combine(root, "prebuilt.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.CurrentClass,
                "S1",
                package);
            using (var archive = ZipFile.OpenRead(package))
            {
                Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("prebuilt-structure.idva"));
                Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("prebuilt-structure.png"));
                using var manifest = JsonDocument.Parse(
                    archive.GetEntry("manifest.json")!.Open());
                Assert.True(manifest.RootElement.GetProperty("capabilities")
                    .GetProperty("prebuiltStructureLines").GetBoolean());
            }
            var target = new MapRepository(Path.Combine(root, "target"));
            var service = new IdvmPackageService(target);
            var imported = await service.ImportAsync(await service.InspectAsync(package));
            var importedMap = Assert.Single(imported.ImportedMaps);
            Assert.True(target.HasCompletePrebuiltStructureLines(importedMap));
            Assert.True(File.Exists(target.GetPrebuiltStructureLinePath(importedMap, "1f")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private const string NormalIdva = """
        {
          "format":"IDVA","schema_version":"1.1","algorithm_id":"structure.normal.test",
          "display_name":"Normal test","profile_family":"structure-map","profile_style":"normal",
          "geometry_policy":{"preserve_input_size":true,"allow_resize":false,"allow_rotation":false,"allow_warp":false},
          "runtime":{"engine":"idvb-opencv-pipeline","language":"declarative-json","minimum_engine_version":"1.0"},
          "input":{"type":"raster-image","color_order":"BGR"},
          "output":{"type":"binary-edge-map","background":0,"edge":255,"line_width_px":2},
          "pipeline":[
            {"stage":"color_classification","mode":"HSV_RANGE"},
            {"stage":"morph_open","kernel":[3,3]},
            {"stage":"remove_small_components","room_min_area":100,"corridor_min_area":100},
            {"stage":"morph_close","kernels":[[5,5],[9,9]]},
            {"stage":"fill_holes","mode":"all_enclosed"},
            {"stage":"remove_small_components","room_min_area":300,"corridor_min_area":200},
            {"stage":"contours","retrieval":"RETR_LIST","chain":"CHAIN_APPROX_SIMPLE"},
            {"stage":"draw_edges","line_width_px":2,"antialias":false}
          ],
          "parameters":{"room_hsv_lo":[7,35,55],"room_hsv_hi":[22,180,190],
            "corridor_hsv_lo":[0,0,55],"corridor_hsv_hi":[179,60,180]}
        }
        """;
}
