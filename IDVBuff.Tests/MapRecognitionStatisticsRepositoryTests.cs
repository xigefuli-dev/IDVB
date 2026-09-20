using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapRecognitionStatisticsRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"idvbuff-recognition-statistics-{Guid.NewGuid():N}");

    [Fact]
    public async Task MissingFileReturnsEmptyStatistics()
    {
        var repository = CreateRepository();

        var statistics = await repository.GetAsync();

        Assert.Equal(0, statistics.TotalAttempts);
        Assert.Equal(0, statistics.SuccessfulAttempts);
        Assert.Equal(0d, statistics.SuccessRate);
    }

    [Fact]
    public async Task AttemptsAndAlignmentsArePersistedAcrossInstances()
    {
        var repository = CreateRepository();
        await repository.RecordAttemptStartedAsync();
        await repository.RecordAttemptStartedAsync();
        await repository.RecordAlignmentProducedAsync();

        var reloaded = await CreateRepository().GetAsync();

        Assert.Equal(2, reloaded.TotalAttempts);
        Assert.Equal(1, reloaded.SuccessfulAttempts);
        Assert.Equal(0.5d, reloaded.SuccessRate);
    }

    [Fact]
    public async Task ConcurrentUpdatesDoNotLoseCounters()
    {
        var repository = CreateRepository();

        await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => repository.RecordAttemptStartedAsync()));
        await Task.WhenAll(Enumerable.Range(0, 7)
            .Select(_ => repository.RecordAlignmentProducedAsync()));

        var statistics = await repository.GetAsync();
        Assert.Equal(12, statistics.TotalAttempts);
        Assert.Equal(7, statistics.SuccessfulAttempts);
    }

    private MapRecognitionStatisticsRepository CreateRepository() =>
        new(Path.Combine(_root, "recognition-statistics.json"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
