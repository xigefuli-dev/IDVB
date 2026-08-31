using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class MapCandidateLearningEngineTests
{
    [Fact]
    public async Task ExportNormalizesLegacyFullViewportBeforePrivacyValidation()
    {
        var root = CreateTemporaryDirectory();
        var referencePath = Path.Combine(root, "reference.png");
        using (var reference = new Mat(720, 960, MatType.CV_8UC3, Scalar.White))
            Cv2.ImWrite(referencePath, reference);
        var choices = new[]
        {
            CreateChoice(sequence: 1, confidence: 0.8, referencePath: referencePath),
            CreateChoice(sequence: 2, confidence: 0.7, referencePath: referencePath)
        };
        var packagePath = Path.Combine(root, "legacy-training.zip");
        await using var engine = new MapCandidateLearningEngine(root);
        await engine.InitializeAsync();
        using (var live = new Mat(640, 900, MatType.CV_8UC3, Scalar.Black))
        {
            await engine.RecordHumanSelectionAsync(
                Guid.NewGuid(), live, choices, choices[0].Recognition.Map.Id);
        }

        var repository = new MapLearningRepository(root);
        var sample = Assert.Single(await repository.LoadSamplesAsync(
            CancellationToken.None));
        using (var legacyViewport = new Mat(
            1062, 1333, MatType.CV_8UC4, Scalar.White))
        {
            Cv2.ImWrite(
                Path.Combine(repository.SamplesDirectory,
                    sample.SampleId, sample.LiveImageFile),
                legacyViewport);
        }

        await engine.ExportAsync(packagePath);
        var validation = MapLearningExportValidator.Validate(packagePath);

        Assert.True(validation.IsValid, validation.Message);
        Assert.Equal(1, validation.SampleCount);
    }
}
