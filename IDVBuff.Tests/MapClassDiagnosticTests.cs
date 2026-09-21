using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapClassDiagnosticTests
{
    [Theory]
    [InlineData("1f", "1f", true)]
    [InlineData("1F", "1f", true)]
    [InlineData("1f", "2f", false)]
    [InlineData("1f", "b1f", false)]
    public void RequiresScanAssets_OnlyMatchesConfiguredScanFloor(
        string scanFloorKey,
        string floorKey,
        bool expected)
    {
        Assert.Equal(expected,
            MapRepository.RequiresScanAssets(scanFloorKey, floorKey));
    }

    [Fact]
    public async Task DiagnoseMapClasses_ReportsConcreteEmptyClassFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"idvb-map-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var repository = new MapRepository(root);
            var mapClass = await repository.CreateClassAsync("诊断测试");

            var result = Assert.Single(
                await repository.DiagnoseMapClassesAsync(),
                item => item.MapClass == mapClass);

            Assert.Equal(mapClass, result.MapClass);
            Assert.False(result.IsHealthy);
            Assert.Equal("没有任何地图。", Assert.Single(result.Problems));
            Assert.Equal("没有任何地图。", result.Summary);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
