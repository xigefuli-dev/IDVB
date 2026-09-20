using System.Diagnostics;
using OpenCvSharp;
using TorchSharp;
using static TorchSharp.torch;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCandidateLearningEngine
{
    internal static (
        IReadOnlyList<MapLearningSampleManifest> Training,
        IReadOnlyList<MapLearningSampleManifest> Validation)
        PartitionSpatialSamples(
            IReadOnlyList<MapLearningSampleManifest> samples)
    {
        var training = new List<MapLearningSampleManifest>();
        var validation = new List<MapLearningSampleManifest>();
        foreach (var group in samples.GroupBy(sample =>
            (sample.SelectedMapId, Floor: ResolveSelectedFloor(sample))))
        {
            var ordered = group.OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.SampleId,
                    StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length == 1)
            {
                training.Add(ordered[0]);
                continue;
            }
            validation.Add(ordered[0]);
            training.AddRange(ordered.Skip(1));
        }
        return (training.OrderBy(item => item.CreatedAt).ToArray(),
            validation.OrderBy(item => item.CreatedAt).ToArray());
    }

    private static string ResolveSelectedFloor(
        MapLearningSampleManifest sample) => sample.Candidates
        .FirstOrDefault(candidate => candidate.IsPositive
            && candidate.MapId == sample.SelectedMapId)?.FloorKey
        ?? string.Empty;

    private async Task<string?> SelectCompatibleParentAsync(
        CancellationToken cancellationToken)
    {
        var current = _repository.ReadCurrentVersion();
        if (current is not null
            && await _repository.VerifyModelAsync(current, cancellationToken))
            return current;

        var bestExperimental = _repository.ReadBestExperimentalVersion();
        if (bestExperimental is not null
            && await _repository.VerifyModelAsync(
                bestExperimental, cancellationToken))
            return bestExperimental;

        var manifests = await _repository.LoadModelManifestsAsync(
            cancellationToken);
        foreach (var manifest in manifests
            .OrderByDescending(item => item.IsQualified)
            .ThenByDescending(item => item.ActivatedAsBestExperimental)
            .ThenByDescending(item => item.CreatedAt))
        {
            if (!manifest.IsQualified
                && !manifest.ActivatedAsBestExperimental)
                continue;
            if (await _repository.VerifyModelAsync(
                    manifest.Version, cancellationToken))
                return manifest.Version;
        }
        return null;
    }

    private async Task<MapLearningScoreResult> ScoreSpatialCoreAsync(
        Mat liveViewport,
        IReadOnlyList<MapRecognitionChoice> traditional,
        MapCandidateDecisionMode requestedMode,
        CancellationToken cancellationToken)
    {
        var liveData = MapLearningPreprocessor.CreateInput(liveViewport);
        using var liveTensor = SiameseMapNetwork.ToTensor(
            [liveData], _network!.Device);
        using var noGrad = torch.no_grad();
        _network.EvaluationMode();
        using var liveEmbedding = _network.EncodeLive(liveTensor);
        var scored = new List<(MapRecognitionChoice Choice, double Logit)>(
            traditional.Count);

        foreach (var choice in traditional)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var cacheKey = CreateReferenceEmbeddingKey(choice);
                if (!_referenceEmbeddings.TryGetValue(cacheKey, out var cached))
                {
                    using var reference =
                        MapLearningPreprocessor.LoadReferenceRegion(choice);
                    var tiles = MapLearningPreprocessor
                        .CreateReferenceTiles(reference);
                    using var referenceTensor = SiameseMapNetwork.ToTensor(
                        tiles.Select(tile => tile.Input).ToArray(), _network.Device);
                    cached = new CachedReferenceTiles(
                        _network.EncodeReference(referenceTensor), tiles);
                    _referenceEmbeddings[cacheKey] = cached;
                }

                using var liveBatch = RepeatEmbedding(
                    liveEmbedding, cached.Tiles.Count);
                using var logits = _network.MatchEmbeddings(
                    liveBatch, cached.Embeddings);
                var (bestLogit, bestIndex) = ReadBestTile(logits,
                    cached.Tiles.Count);
                stopwatch.Stop();
                var tile = cached.Tiles[bestIndex];
                scored.Add((CloneChoice(choice,
                    modelVersion: _activeVersion,
                    modelFailure: string.Empty,
                    inferenceMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
                    sources: choice.EvidenceSources
                        | MapCandidateEvidenceSource.Model,
                    modelMatchedFloorKey: choice.Recognition.Result.Floor,
                    modelMatchedCenterX: tile.CenterX,
                    modelMatchedCenterY: tile.CenterY,
                    modelMatchedExtent: tile.Extent), bestLogit));
            }
            catch (Exception exception)
            {
                scored.Add((CloneChoice(choice,
                    modelVersion: _activeVersion,
                    modelFailure: exception.Message,
                    inferenceMilliseconds: 0d,
                    sources: choice.EvidenceSources), double.NaN));
            }
        }

        var probabilities = Softmax(scored.Select(item => item.Logit).ToArray());
        var withProbabilities = scored.Select((item, index) =>
        {
            var probability = probabilities[index];
            var fusion = _activeQualified
                && requestedMode == MapCandidateDecisionMode.Fusion
                && double.IsFinite(probability)
                    ? 0.65d * probability
                        + 0.35d * (item.Choice.TraditionalScore ?? 0d)
                    : (double?)null;
            return CloneChoice(item.Choice,
                modelProbability: double.IsFinite(probability)
                    ? probability : null,
                fusionScore: fusion,
                modelVersion: _activeVersion,
                modelFailure: item.Choice.ModelFailureReason,
                inferenceMilliseconds: item.Choice.ModelInferenceMilliseconds,
                sources: item.Choice.EvidenceSources,
                modelMatchedFloorKey: item.Choice.ModelMatchedFloorKey,
                modelMatchedCenterX: item.Choice.ModelMatchedCenterX,
                modelMatchedCenterY: item.Choice.ModelMatchedCenterY,
                modelMatchedExtent: item.Choice.ModelMatchedExtent);
        }).ToArray();

        var fellBack = !_activeQualified;
        var ordered = fellBack
            ? withProbabilities.OrderByDescending(item => item.TraditionalScore)
                .ThenBy(item => item.PreferredOrder).ToArray()
            : requestedMode == MapCandidateDecisionMode.ModelOnly
                ? withProbabilities.OrderByDescending(item => item.ModelProbability)
                    .ThenBy(item => item.PreferredOrder).ToArray()
                : withProbabilities.OrderByDescending(item => item.FusionScore)
                    .ThenBy(item => item.PreferredOrder).ToArray();
        return new MapLearningScoreResult
        {
            Choices = ordered,
            ModelAvailable = withProbabilities.Any(item =>
                item.ModelProbability.HasValue),
            ModelQualified = _activeQualified,
            ModelVersion = _activeVersion,
            FellBackToTraditionalOrdering = fellBack,
            FailureReason = fellBack
                ? "实验模型尚未晋级，仅展示空间匹配证据；候选顺序保持传统算法。"
                : string.Empty
        };
    }

    private List<SpatialTrainingCase> LoadSpatialCases(
        IReadOnlyList<MapLearningSampleManifest> samples,
        bool augment)
    {
        var result = new List<SpatialTrainingCase>();
        var tileCache = new Dictionary<string,
            IReadOnlyList<MapLearningReferenceTile>>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            using var live = _repository.LoadLiveImage(sample);
            if (live.Empty())
                continue;
            var candidates = new List<SpatialTrainingCandidate>();
            foreach (var candidate in sample.Candidates)
            {
                if (!string.Equals(candidate.ReferenceScope, "floor",
                        StringComparison.Ordinal))
                    continue;
                if (!tileCache.TryGetValue(candidate.ReferenceHash, out var tiles))
                {
                    using var reference = _repository.LoadReferenceImage(candidate);
                    if (reference.Empty())
                        continue;
                    tiles = MapLearningPreprocessor.CreateReferenceTiles(reference);
                    tileCache[candidate.ReferenceHash] = tiles;
                }
                candidates.Add(new SpatialTrainingCandidate(candidate, tiles));
            }
            var positiveIndex = candidates.FindIndex(item =>
                item.Manifest.MapId == sample.SelectedMapId);
            if (positiveIndex < 0 || candidates.Count < 2)
                continue;

            var inputs = augment
                ? MapLearningPreprocessor.CreateTrainingInputs(live)
                    .Where((_, index) => index is 0 or 3 or 6)
                    .ToArray()
                : [MapLearningPreprocessor.CreateInput(live)];
            result.AddRange(inputs.Select(input => new SpatialTrainingCase(
                sample, input, candidates, positiveIndex)));
        }
        return result;
    }

    private static void TrainSpatialCandidate(
        SiameseMapNetwork network,
        IReadOnlyList<SpatialTrainingCase> cases,
        CancellationToken cancellationToken,
        Action<int, int, int, int, long, long> reportProgress)
    {
        torch.manual_seed(260830);
        var random = new Random(260830);
        using var optimizer = torch.optim.AdamW(network.Parameters(), lr: 0.0003);
        network.TrainMode();
        const int epochCount = 20;
        var total = (long)epochCount * cases.Count;
        long processed = 0;
        for (var epoch = 0; epoch < epochCount; epoch++)
        {
            var order = Enumerable.Range(0, cases.Count)
                .OrderBy(_ => random.Next()).ToArray();
            for (var position = 0; position < order.Length; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TrainSpatialCase(network, cases[order[position]], optimizer);
                processed++;
                reportProgress(epoch + 1, epochCount, position + 1,
                    cases.Count, processed, total);
            }
        }
    }

    private static void TrainSpatialCase(
        SiameseMapNetwork network,
        SpatialTrainingCase trainingCase,
        torch.optim.Optimizer optimizer)
    {
        var allTiles = trainingCase.Candidates
            .SelectMany(candidate => candidate.Tiles).ToArray();
        using var liveTensor = SiameseMapNetwork.ToTensor(
            [trainingCase.Live], network.Device);
        using var referenceTensor = SiameseMapNetwork.ToTensor(
            allTiles.Select(tile => tile.Input).ToArray(), network.Device);
        using var liveEmbedding = network.EncodeLive(liveTensor);
        using var referenceEmbeddings = network.EncodeReference(referenceTensor);
        using var liveBatch = RepeatEmbedding(liveEmbedding, allTiles.Length);
        using var tileLogits = network.MatchEmbeddings(
            liveBatch, referenceEmbeddings);
        var candidateScores = new List<Tensor>(trainingCase.Candidates.Count);
        var offset = 0;
        Tensor? localizationLoss = null;
        try
        {
            for (var index = 0; index < trainingCase.Candidates.Count; index++)
            {
                var candidate = trainingCase.Candidates[index];
                using var slice = tileLogits.narrow(0, offset,
                    candidate.Tiles.Count);
                using var scaled = slice / 0.20d;
                candidateScores.Add(
                    (scaled.logsumexp(0) * 0.20d).reshape(1));
                if (index == trainingCase.PositiveIndex
                    && candidate.Manifest.HasTrustedSpatialLabel)
                {
                    var targetIndex = FindNearestTile(candidate.Tiles,
                        candidate.Manifest.SpatialCenterX,
                        candidate.Manifest.SpatialCenterY);
                    using var locationTarget = torch.tensor(
                        new long[] { targetIndex }, dtype: ScalarType.Int64,
                        device: network.Device);
                    localizationLoss = torch.nn.functional.cross_entropy(
                        slice.reshape(1, candidate.Tiles.Count), locationTarget);
                }
                offset += candidate.Tiles.Count;
            }
            using var scores = torch.cat(candidateScores.ToArray(), 0)
                .reshape(1, trainingCase.Candidates.Count);
            using var target = torch.tensor(
                new long[] { trainingCase.PositiveIndex },
                dtype: ScalarType.Int64, device: network.Device);
            using var rankingLoss = torch.nn.functional.cross_entropy(scores, target);
            using var loss = localizationLoss is null
                ? rankingLoss.clone()
                : rankingLoss + localizationLoss * 0.25;
            optimizer.zero_grad();
            loss.backward();
            optimizer.step();
        }
        finally
        {
            localizationLoss?.Dispose();
            foreach (var score in candidateScores)
                score.Dispose();
        }
    }

    private static Tensor RepeatEmbedding(Tensor embedding, int count) =>
        torch.cat(Enumerable.Repeat(embedding, count).ToArray(), 0);

    private static (double Logit, int Index) ReadBestTile(
        Tensor logits,
        int count)
    {
        var best = double.NegativeInfinity;
        var bestIndex = 0;
        for (var index = 0; index < count; index++)
        {
            using var item = logits.narrow(0, index, 1);
            var value = item.item<float>();
            if (value <= best)
                continue;
            best = value;
            bestIndex = index;
        }
        return (best, bestIndex);
    }

    private static int FindNearestTile(
        IReadOnlyList<MapLearningReferenceTile> tiles,
        double x,
        double y) => tiles.Select((tile, index) => new
        {
            index,
            distance = Math.Pow(tile.CenterX - x, 2d)
                + Math.Pow(tile.CenterY - y, 2d)
        }).MinBy(item => item.distance)!.index;

    private static double[] Softmax(IReadOnlyList<double> logits)
    {
        var finite = logits.Where(double.IsFinite).ToArray();
        if (finite.Length == 0)
            return Enumerable.Repeat(double.NaN, logits.Count).ToArray();
        var maximum = finite.Max();
        var exponents = logits.Select(value => double.IsFinite(value)
            ? Math.Exp(value - maximum) : 0d).ToArray();
        var total = exponents.Sum();
        return exponents.Select(value => total > 0d
            ? value / total : double.NaN).ToArray();
    }
}
