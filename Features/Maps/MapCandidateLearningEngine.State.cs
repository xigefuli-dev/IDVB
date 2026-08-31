using TorchSharp;
using static TorchSharp.torch;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCandidateLearningEngine
{
    private sealed record SpatialTrainingCandidate(
        MapLearningCandidateManifest Manifest,
        IReadOnlyList<MapLearningReferenceTile> Tiles);

    private sealed record SpatialTrainingCase(
        MapLearningSampleManifest Sample,
        float[] Live,
        IReadOnlyList<SpatialTrainingCandidate> Candidates,
        int PositiveIndex);

    private sealed class CachedReferenceTiles(
        Tensor embeddings,
        IReadOnlyList<MapLearningReferenceTile> tiles) : IDisposable
    {
        public Tensor Embeddings { get; } = embeddings;
        public IReadOnlyList<MapLearningReferenceTile> Tiles { get; } = tiles;
        public void Dispose() => Embeddings.Dispose();
    }

    private readonly MapLearningRepository _repository;
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private SiameseMapNetwork? _network;
    private readonly Dictionary<string, CachedReferenceTiles> _referenceEmbeddings =
        new(StringComparer.Ordinal);
    private string _activeVersion = string.Empty;
    private bool _activeQualified;
    private Device _computeDevice = torch.CPU;
    private string _computeFallbackReason = string.Empty;
    private int _consecutiveInferenceFailures;
    private int _queuedTraining;
    private Task? _queuedTrainingTask;
    private MapLearningStatus _status = new();
    private bool _disposed;

    public MapCandidateLearningEngine(string? repositoryRoot = null)
    {
        _repository = new MapLearningRepository(repositoryRoot);
    }

    public MapLearningStatus Status => Volatile.Read(ref _status);
    public string RepositoryRoot => _repository.RootDirectory;
}
