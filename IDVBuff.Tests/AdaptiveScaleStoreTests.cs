using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using System.Text.Json;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AdaptiveScaleStoreTests
{
    [Fact]
    public async Task CalibrationRequiresFiveConsecutiveSamples()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-scale-");
        try
        {
            var path = Path.Combine(directory.FullName, "adaptive-scale-cache.json");
            var store = new AdaptiveScaleStore(path);
            var key = Key("1f");

            for (var count = 1; count <= 4; count++)
                Assert.False(AdaptiveScaleStore.IsTrusted(
                    await store.RecordInitialStreakAsync(Streak(key, count, 1.1))));
            var fifth = await store.RecordInitialStreakAsync(Streak(key, 5, 1.1));

            Assert.True(AdaptiveScaleStore.IsTrusted(fifth));
            Assert.Equal(2, JsonDocument.Parse(await File.ReadAllTextAsync(path))
                .RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FloorEntriesNeverMerge()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-scale-");
        try
        {
            var store = Store(directory);
            var first = Key("1f");
            var second = first with { FloorKey = "2f" };
            await store.RecordInitialStreakAsync(Streak(first, 2, 1.0));
            await store.RecordInitialStreakAsync(Streak(second, 1, 1.2));

            Assert.Equal(2, store.TryGet(first)!.DistinctOpenCount);
            Assert.Equal(1, store.TryGet(second)!.DistinctOpenCount);
            Assert.Equal(1.2, store.TryGet(second)!.CalibrationScale, 8);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScaleRecoveryResetRemovesOnlyTheExactFloorContext()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-scale-");
        try
        {
            var store = Store(directory);
            var first = Key("1f");
            var second = first with { FloorKey = "2f" };
            await store.RecordInitialStreakAsync(Streak(first, 5, 1.141078));
            await store.RecordInitialStreakAsync(Streak(second, 5, 0.82));

            await store.ResetAsync(first);

            Assert.Null(store.TryGet(first));
            Assert.True(AdaptiveScaleStore.IsTrusted(store.TryGet(second)));
            var reloaded = Store(directory);
            await reloaded.InitializeAsync();
            Assert.Null(reloaded.TryGet(first));
            Assert.NotNull(reloaded.TryGet(second));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TrustedCalibrationSeedRequiresExactFullKey()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-scale-");
        try
        {
            var store = Store(directory);
            var key = Key("1f");
            await store.RecordInitialStreakAsync(Streak(key, 5, 1.1));
            var coordinator = new AdaptiveScaleCoordinator(new AdaptiveScaleOptions(), store);

            Assert.True(coordinator.TryGetCalibrationSeed(key, out var seed));
            Assert.Equal(1.1, seed!.Scale, 8);
            Assert.False(coordinator.TryGetCalibrationSeed(
                key with { FloorKey = "2f" }, out _));
            Assert.False(coordinator.TryGetCalibrationSeed(
                key with { ViewportWidth = key.ViewportWidth + 1 }, out _));
            Assert.False(coordinator.TryGetCalibrationSeed(
                key with { MapUpdatedAtTicks = key.MapUpdatedAtTicks + 1 }, out _));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DamagedPrimaryRestoresFromBackup()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-scale-");
        try
        {
            var path = Path.Combine(directory.FullName, "adaptive-scale-cache.json");
            var key = Key("1f");
            var writer = new AdaptiveScaleStore(path);
            await writer.RecordInitialStreakAsync(Streak(key, 1, 1.1));
            await writer.RecordInitialStreakAsync(Streak(key, 2, 1.1));
            Assert.True(File.Exists(path + ".bak"));
            await File.WriteAllTextAsync(path, "{damaged");

            var restored = new AdaptiveScaleStore(path);
            await restored.InitializeAsync();

            Assert.NotNull(restored.TryGet(key));
            Assert.Equal(2, JsonDocument.Parse(await File.ReadAllTextAsync(path))
                .RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FailedWriteDoesNotAdvanceInMemoryEntry()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-scale-");
        try
        {
            var occupied = Directory.CreateDirectory(
                Path.Combine(directory.FullName, "adaptive-scale-cache.json"));
            var store = new AdaptiveScaleStore(occupied.FullName);
            var key = Key("1f");

            await Assert.ThrowsAnyAsync<IOException>(() =>
                store.RecordInitialStreakAsync(Streak(key, 5, 1.1)));

            Assert.Null(store.TryGet(key));
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.tmp.*"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(3, 0.90, 0.001, true)]
    [InlineData(2, 0.90, 0.001, false)]
    public async Task SchemaOneMigratesOnlyPreviouslyTrustedEntries(
        int oldCount,
        double confidence,
        double mad,
        bool expectedTrusted)
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-scale-");
        try
        {
            var path = Path.Combine(directory.FullName, "adaptive-scale-cache.json");
            var key = Key("1f");
            await WriteSchemaOneAsync(path, key, oldCount, confidence, mad);
            var store = new AdaptiveScaleStore(path);

            await store.InitializeAsync();

            var entry = store.TryGet(key);
            Assert.NotNull(entry);
            Assert.Equal(expectedTrusted ? 5 : 0, entry!.DistinctOpenCount);
            Assert.Equal(expectedTrusted, AdaptiveScaleStore.IsTrusted(entry));
            Assert.Equal(2, JsonDocument.Parse(await File.ReadAllTextAsync(path))
                .RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CorruptSidecarStartsEmpty()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-scale-");
        try
        {
            var path = Path.Combine(directory.FullName, "adaptive-scale-cache.json");
            await File.WriteAllTextAsync(path, "{not-json");
            var store = new AdaptiveScaleStore(path);
            await store.InitializeAsync();
            Assert.Null(store.TryGet(Key("1f")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedSchemaIsNeverOverwritten()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-scale-");
        try
        {
            var path = Path.Combine(directory.FullName, "adaptive-scale-cache.json");
            const string futureDocument = "{\"SchemaVersion\":99,\"Entries\":[]}";
            await File.WriteAllTextAsync(path, futureDocument);
            var store = new AdaptiveScaleStore(path);
            await store.InitializeAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.RecordInitialStreakAsync(Streak(Key("1f"), 1, 1.0)));

            Assert.Equal(futureDocument, await File.ReadAllTextAsync(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static AdaptiveScaleStore Store(DirectoryInfo directory) =>
        new(Path.Combine(directory.FullName, "adaptive-scale-cache.json"));

    private static AdaptiveScaleInitialStreakSnapshot Streak(
        AdaptiveScaleKey key,
        int count,
        double scale)
    {
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, count)
            .Select(_ => new AdaptiveScaleInitialSample(scale, 0.90, now))
            .ToArray();
        return new(key, samples, count, scale, count == 0 ? 0d : 0.90, 0d, now);
    }

    private static async Task WriteSchemaOneAsync(
        string path,
        AdaptiveScaleKey key,
        int count,
        double confidence,
        double mad)
    {
        var entry = new AdaptiveScaleStoreEntry
        {
            Key = key,
            CalibrationScale = 1.1,
            Confidence = confidence,
            RelativeMad = mad,
            DistinctOpenCount = count,
            LastValidatedAt = DateTimeOffset.UtcNow,
            Source = "StructureConsensus"
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Entries = new[] { entry }
        }));
    }

    private static AdaptiveScaleKey Key(string floor) =>
        new(Guid.NewGuid(), 10, floor, 1920, 1080, 1314, 1055);
}
