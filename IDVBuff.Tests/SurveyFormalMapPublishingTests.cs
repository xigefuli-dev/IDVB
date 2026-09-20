using System.Security.Cryptography;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class SurveyFormalMapPublishingTests
{
    [Fact]
    public async Task SurveyVisualAndStructureRemainIndependentAcrossIdvm10RoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var visualPath = Path.Combine(root, "visual.png");
            var structurePath = Path.Combine(root, "structure.png");
            using (var visual = new Mat(new Size(180, 120), MatType.CV_8UC3, new Scalar(20, 80, 180)))
            using (var structure = new Mat(new Size(180, 120), MatType.CV_8UC1, Scalar.Black))
            {
                Cv2.Rectangle(structure, new Rect(20, 30, 120, 60), Scalar.White, 3);
                Assert.True(Cv2.ImWrite(visualPath, visual));
                Assert.True(Cv2.ImWrite(structurePath, structure));
            }

            var projectId = Guid.NewGuid();
            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.1, Y = 0.2, Width = 0.1, Height = 0.1 };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.75, Y = 0.65, Width = 0.1, Height = 0.1 };
            var source = new MapRepository(Path.Combine(root, "source"));
            var saved = await source.SaveAsync(new MapDraft
            {
                Title = "Survey map",
                Source = "survey",
                SourceProjectId = projectId,
                SourceProjectRevision = 17,
                SourceVisualSha256 = new string('a', 64),
                SourceStructureSha256 = new string('b', 64),
                Floors =
                [
                    new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }
                ],
                FloorPaths = new Dictionary<string, string> { ["1f"] = visualPath },
                FloorRecognitionSourcePaths = new Dictionary<string, string>
                {
                    ["1f"] = structurePath
                },
                FloorOnePath = visualPath,
                Recognition = recognition
            });

            Assert.Equal("survey", saved.Source);
            Assert.Equal(projectId, saved.SourceProjectId);
            Assert.Equal(17, saved.SourceProjectRevision);
            Assert.Equal(await Sha256Async(structurePath),
                await Sha256Async(source.GetFloorRecognitionPath(saved, "1f")));
            Assert.NotEqual(
                await Sha256Async(source.GetFloorImagePath(saved, "1f")),
                await Sha256Async(source.GetFloorRecognitionPath(saved, "1f")));

            var packagePath = Path.Combine(root, "formal-map.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.AllClasses,
                null,
                packagePath);
            var target = new MapRepository(Path.Combine(root, "target"));
            var service = new IdvmPackageService(target);
            var imported = await service.ImportAsync(await service.InspectAsync(packagePath));
            var roundTripped = Assert.Single(imported.ImportedMaps);

            Assert.Equal("survey", roundTripped.Source);
            Assert.Equal(projectId, roundTripped.SourceProjectId);
            Assert.Equal(17, roundTripped.SourceProjectRevision);
            Assert.Equal(new string('a', 64), roundTripped.SourceVisualSha256);
            Assert.Equal(new string('b', 64), roundTripped.SourceStructureSha256);
            Assert.Equal(await Sha256Async(structurePath),
                await Sha256Async(target.GetFloorRecognitionPath(roundTripped, "1f")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
