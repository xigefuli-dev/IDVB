using System.Drawing;
using System.Drawing.Imaging;
using IDVBuff.Appearance;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapClassOverviewExporterTests
{
    [Fact]
    public void UsesRectangleCropAndMasksOutsideFreeCropPolygon()
    {
        var root = Path.Combine(Path.GetTempPath(), "idvb-overview-crop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.png");
            using (var source = new Bitmap(100, 100))
            {
                using (var graphics = Graphics.FromImage(source))
                {
                    graphics.Clear(Color.Red);
                    graphics.FillRectangle(Brushes.Lime, 25, 25, 50, 50);
                }
                source.Save(sourcePath, ImageFormat.Png);
            }
            var map = new MapRecord
            {
                Id = Guid.NewGuid(),
                SequenceNumber = 1,
                Floors = [
                    new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 },
                    new FloorDefinition { Key = "2f", DisplayName = "2F", SortOrder = 2 }
                ]
            };
            map.NormalizeRecognition();
            map.Recognition.GetFloor("1f")!.RecognitionRegion = new NormalizedRectangle
            {
                X = .25, Y = .25, Width = .5, Height = .5
            };
            var freeCrop = map.Recognition.GetFloor("2f")!;
            freeCrop.RecognitionRegion = new NormalizedRectangle
            {
                X = .25, Y = .25, Width = .5, Height = .5
            };
            freeCrop.FreeCropPoints = [
                new NormalizedPoint { X = .25, Y = .25 },
                new NormalizedPoint { X = .75, Y = .25 },
                new NormalizedPoint { X = .25, Y = .75 }
            ];

            var outputs = MapClassOverviewExporter.Export(root, [map],
                (_, _) => sourcePath, _ => null);

            Assert.Equal(2, outputs.Count);
            using var rectangle = new Bitmap(outputs[0]);
            using var free = new Bitmap(outputs[1]);
            Assert.Equal(Color.Lime.ToArgb(), rectangle.GetPixel(165, 165).ToArgb());
            Assert.Equal(Color.Lime.ToArgb(), free.GetPixel(165, 165).ToArgb());
            Assert.Equal(Color.White.ToArgb(), free.GetPixel(500, 450).ToArgb());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExportsOneImagePerFloorWithNaturalOrderAndVariantBorder()
    {
        var root = Path.Combine(Path.GetTempPath(), "idvb-overview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var numbers = new[] { 10, 1, 5, 2, 4, 3 };
            var maps = numbers.Select(number => new MapRecord
            {
                Id = Guid.NewGuid(),
                SequenceNumber = number,
                Title = number % 2 == 0 ? $"地图 {number}" : $"地图{number}",
                Floors = [
                    new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 },
                    new FloorDefinition { Key = "2f", DisplayName = "2F", SortOrder = 2 },
                    new FloorDefinition { Key = "basement", DisplayName = "地下室", SortOrder = 3 }
                ]
            }).ToArray();
            var sourcePaths = new Dictionary<Guid, string>();
            foreach (var map in maps)
            {
                var path = Path.Combine(root, $"source_{map.SequenceNumber}.png");
                using var source = new Bitmap(40, 30);
                using (var graphics = Graphics.FromImage(source))
                    graphics.Clear(Color.FromArgb(map.SequenceNumber, 0, 0));
                source.Save(path, ImageFormat.Png);
                sourcePaths[map.Id] = path;
            }
            var variant = maps.Single(map => map.SequenceNumber == 2);
            var colors = new MapCardColors(new RgbColor(20, 80, 100),
                new RgbColor(210, 30, 40), new RgbColor(255, 255, 255),
                new RgbColor(255, 255, 255));

            var outputs = MapClassOverviewExporter.Export(root, maps,
                (map, _) => sourcePaths[map.Id],
                map => map.Id == variant.Id ? colors : null);

            Assert.Equal(3, outputs.Count);
            Assert.Equal(new[] { "overview_floor_001.png", "overview_floor_002.png",
                "overview_floor_003.png" }, outputs.Select(Path.GetFileName));
            foreach (var output in outputs)
            {
                using var sheet = new Bitmap(output);
                Assert.Equal(3140, sheet.Width);
                Assert.Equal(1000, sheet.Height);
                Assert.Equal(1, sheet.GetPixel(250, 160).R);
                Assert.Equal(2, sheet.GetPixel(850, 160).R);
                Assert.Equal(10, sheet.GetPixel(250, 640).R);
                Assert.Equal(Color.FromArgb(210, 30, 40).ToArgb(),
                    sheet.GetPixel(652, 50).ToArgb());
            }
            Assert.Throws<IOException>(() => MapClassOverviewExporter.Export(root, maps,
                (map, _) => sourcePaths[map.Id], _ => null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
