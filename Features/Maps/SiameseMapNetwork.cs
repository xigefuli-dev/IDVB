using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace IDVBuff.Features.Maps;

internal sealed class SiameseMapNetwork : IDisposable
{
    public const string ArchitectureVersion =
        MapLearningModelContract.ArchitectureVersion;
    public const int EmbeddingSize = 128;
    public static readonly string[] WeightFileNames =
        ["live-adapter.dat", "reference-adapter.dat", "shared-tower.dat",
            "head.dat"];

    private readonly Module<Tensor, Tensor> _liveAdapter;
    private readonly Module<Tensor, Tensor> _referenceAdapter;
    private readonly Module<Tensor, Tensor> _sharedTower;
    private readonly Module<Tensor, Tensor> _head;
    public Device Device { get; }

    public SiameseMapNetwork(Device? device = null)
    {
        Device = device ?? torch.CPU;
        _liveAdapter = CreateDomainAdapter();
        _referenceAdapter = CreateDomainAdapter();
        _sharedTower = CreateSharedSpatialTower();
        _head = Sequential(
            ("linear1", Linear(EmbeddingSize * 2, 128)),
            ("relu1", ReLU()),
            ("linear2", Linear(128, 32)),
            ("relu2", ReLU()),
            ("linear3", Linear(32, 1)));
        _liveAdapter.to(Device);
        _referenceAdapter.to(Device);
        _sharedTower.to(Device);
        _head.to(Device);
    }

    private static Module<Tensor, Tensor> CreateDomainAdapter() => Sequential(
        ("adapter", Conv2d(MapLearningPreprocessor.ChannelCount, 8, 3,
            padding: 1)),
        ("adapterRelu", ReLU()));

    private static Module<Tensor, Tensor> CreateSharedSpatialTower() => Sequential(
        ("conv1", Conv2d(8, 16, 5, padding: 2)),
        ("relu1", ReLU()),
        ("pool1", MaxPool2d(2)),
        ("conv2", Conv2d(16, 32, 3, padding: 1)),
        ("relu2", ReLU()),
        ("pool2", MaxPool2d(2)),
        ("conv3", Conv2d(32, 48, 3, padding: 1)),
        ("relu3", ReLU()),
        ("pool3", MaxPool2d(2)),
        ("conv4", Conv2d(48, 64, 3, padding: 1)),
        ("relu4", ReLU()),
        ("pool4", MaxPool2d(2)),
        ("flatten", Flatten()),
        ("spatial1", Linear(64 * 8 * 8, 256)),
        ("spatialRelu", ReLU()),
        ("embedding", Linear(256, EmbeddingSize)));

    public IEnumerable<TorchSharp.Modules.Parameter> Parameters() =>
        _liveAdapter.parameters()
            .Concat(_referenceAdapter.parameters())
            .Concat(_sharedTower.parameters())
            .Concat(_head.parameters());

    public Tensor Forward(Tensor live, Tensor reference)
    {
        using var liveEmbedding = EncodeLive(live);
        using var referenceEmbedding = EncodeReference(reference);
        return MatchEmbeddings(liveEmbedding, referenceEmbedding);
    }

    public Tensor EncodeLive(Tensor input)
    {
        using var adapted = _liveAdapter.forward(input);
        return _sharedTower.forward(adapted);
    }

    public Tensor EncodeReference(Tensor input)
    {
        using var adapted = _referenceAdapter.forward(input);
        return _sharedTower.forward(adapted);
    }
    public Tensor MatchEmbeddings(Tensor liveEmbedding, Tensor referenceEmbedding)
    {
        using var difference = (liveEmbedding - referenceEmbedding).abs();
        using var product = liveEmbedding * referenceEmbedding;
        using var combined = torch.cat([difference, product], 1);
        return _head.forward(combined).squeeze(1);
    }

    public void TrainMode()
    {
        _liveAdapter.train();
        _referenceAdapter.train();
        _sharedTower.train();
        _head.train();
    }

    public void EvaluationMode()
    {
        _liveAdapter.eval();
        _referenceAdapter.eval();
        _sharedTower.eval();
        _head.eval();
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var restoreDevice = Device.type != DeviceType.CPU;
        if (restoreDevice)
        {
            _liveAdapter.to(torch.CPU);
            _referenceAdapter.to(torch.CPU);
            _sharedTower.to(torch.CPU);
            _head.to(torch.CPU);
        }
        try
        {
            _liveAdapter.save(Path.Combine(directory, WeightFileNames[0]));
            _referenceAdapter.save(Path.Combine(directory, WeightFileNames[1]));
            _sharedTower.save(Path.Combine(directory, WeightFileNames[2]));
            _head.save(Path.Combine(directory, WeightFileNames[3]));
        }
        finally
        {
            if (restoreDevice)
            {
                _liveAdapter.to(Device);
                _referenceAdapter.to(Device);
                _sharedTower.to(Device);
                _head.to(Device);
            }
        }
    }

    public void Load(string directory)
    {
        _liveAdapter.load(Path.Combine(directory, WeightFileNames[0]));
        _referenceAdapter.load(Path.Combine(directory, WeightFileNames[1]));
        _sharedTower.load(Path.Combine(directory, WeightFileNames[2]));
        _head.load(Path.Combine(directory, WeightFileNames[3]));
    }

    public static Tensor ToTensor(
        IReadOnlyList<float[]> inputs,
        Device? device = null)
    {
        if (inputs.Count == 0)
            throw new ArgumentException("CNN batch 不能为空。", nameof(inputs));
        var sampleLength = MapLearningPreprocessor.ChannelCount
            * MapLearningPreprocessor.InputSize
            * MapLearningPreprocessor.InputSize;
        var data = new float[inputs.Count * sampleLength];
        for (var index = 0; index < inputs.Count; index++)
        {
            if (inputs[index].Length != sampleLength)
                throw new InvalidDataException("CNN 输入张量尺寸不匹配。");
            Array.Copy(inputs[index], 0, data, index * sampleLength, sampleLength);
        }
        return torch.tensor(data, dtype: ScalarType.Float32,
            device: device ?? torch.CPU).reshape(
            inputs.Count,
            MapLearningPreprocessor.ChannelCount,
            MapLearningPreprocessor.InputSize,
            MapLearningPreprocessor.InputSize);
    }

    public void Dispose()
    {
        _liveAdapter.Dispose();
        _referenceAdapter.Dispose();
        _sharedTower.Dispose();
        _head.Dispose();
    }
}
