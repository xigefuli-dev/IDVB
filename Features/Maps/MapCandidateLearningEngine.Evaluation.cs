using TorchSharp;
using static TorchSharp.torch;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCandidateLearningEngine
{
    private const double SpatialHitRadius = 0.25d;

    private readonly record struct SpatialEvaluationMetrics(
        double Accuracy,
        double CalibrationError,
        int TrustedSpatialCount,
        double SpatialAccuracy,
        double SpatialMeanError);

    private SpatialEvaluationMetrics EvaluateSpatial(
        SiameseMapNetwork network,
        IReadOnlyList<SpatialTrainingCase> cases,
        CancellationToken cancellationToken,
        Action<int, int> reportProgress)
    {
        network.EvaluationMode();
        using var noGrad = torch.no_grad();
        var correct = 0;
        var brierTotal = 0d;
        var trustedSpatialCount = 0;
        var spatialCorrect = 0;
        var spatialErrorTotal = 0d;
        for (var index = 0; index < cases.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = EvaluateSpatialCase(network, cases[index]);
            var probabilities = Softmax(result.CandidateLogits);
            var predicted = Array.IndexOf(probabilities, probabilities.Max());
            if (predicted == cases[index].PositiveIndex)
                correct++;
            var brier = 0d;
            for (var candidate = 0; candidate < probabilities.Length; candidate++)
            {
                var expected = candidate == cases[index].PositiveIndex ? 1d : 0d;
                brier += Math.Pow(probabilities[candidate] - expected, 2d);
            }
            brierTotal += brier / probabilities.Length;
            var positive = cases[index].Candidates[cases[index].PositiveIndex];
            if (positive.Manifest.HasTrustedSpatialLabel)
            {
                trustedSpatialCount++;
                var tile = positive.Tiles[
                    result.BestTileIndices[cases[index].PositiveIndex]];
                var error = Math.Sqrt(
                    Math.Pow(tile.CenterX - positive.Manifest.SpatialCenterX, 2d)
                    + Math.Pow(tile.CenterY - positive.Manifest.SpatialCenterY, 2d));
                spatialErrorTotal += error;
                if (error <= SpatialHitRadius)
                    spatialCorrect++;
            }
            reportProgress(index + 1, cases.Count);
        }
        return cases.Count == 0
            ? new SpatialEvaluationMetrics(0d, 1d, 0, 0d, 1d)
            : new SpatialEvaluationMetrics(
                (double)correct / cases.Count,
                brierTotal / cases.Count,
                trustedSpatialCount,
                trustedSpatialCount == 0
                    ? 0d : (double)spatialCorrect / trustedSpatialCount,
                trustedSpatialCount == 0
                    ? 1d : spatialErrorTotal / trustedSpatialCount);
    }

    private static double EvaluateTraditionalTopOne(
        IReadOnlyList<MapLearningSampleManifest> samples)
    {
        if (samples.Count == 0)
            return 0d;
        var correct = samples.Count(sample => sample.Candidates
            .OrderByDescending(candidate => candidate.TraditionalScore
                ?? double.NegativeInfinity)
            .FirstOrDefault()?.MapId == sample.SelectedMapId);
        return (double)correct / samples.Count;
    }

    private async Task VerifySpatialReloadConsistencyAsync(
        SiameseMapNetwork original,
        MapModelManifest manifest,
        SpatialTrainingCase evaluationCase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reloaded = new SiameseMapNetwork(original.Device);
        reloaded.Load(_repository.GetModelDirectory(manifest.Version));
        original.EvaluationMode();
        reloaded.EvaluationMode();
        using var noGrad = torch.no_grad();
        var expected = EvaluateSpatialCase(original, evaluationCase);
        var actual = EvaluateSpatialCase(reloaded, evaluationCase);
        if (expected.CandidateLogits.Length != actual.CandidateLogits.Length
            || expected.CandidateLogits.Where((value, index) =>
                Math.Abs(value - actual.CandidateLogits[index]) > 0.0001d).Any()
            || !expected.BestTileIndices.SequenceEqual(actual.BestTileIndices))
        {
            throw new InvalidDataException("模型保存后空间匹配结果不一致。");
        }
    }

    private static (double[] CandidateLogits, int[] BestTileIndices)
        EvaluateSpatialCase(
            SiameseMapNetwork network,
            SpatialTrainingCase evaluationCase)
    {
        var allTiles = evaluationCase.Candidates
            .SelectMany(candidate => candidate.Tiles).ToArray();
        using var live = SiameseMapNetwork.ToTensor(
            [evaluationCase.Live], network.Device);
        using var reference = SiameseMapNetwork.ToTensor(
            allTiles.Select(tile => tile.Input).ToArray(), network.Device);
        using var liveEmbedding = network.EncodeLive(live);
        using var referenceEmbeddings = network.EncodeReference(reference);
        using var liveBatch = RepeatEmbedding(liveEmbedding, allTiles.Length);
        using var tileLogits = network.MatchEmbeddings(liveBatch,
            referenceEmbeddings);
        var candidateLogits = new double[evaluationCase.Candidates.Count];
        var bestTileIndices = new int[evaluationCase.Candidates.Count];
        var offset = 0;
        for (var index = 0; index < candidateLogits.Length; index++)
        {
            var count = evaluationCase.Candidates[index].Tiles.Count;
            using var slice = tileLogits.narrow(0, offset, count);
            var best = ReadBestTile(slice, count);
            candidateLogits[index] = best.Logit;
            bestTileIndices[index] = best.Index;
            offset += count;
        }
        return (candidateLogits, bestTileIndices);
    }
}
