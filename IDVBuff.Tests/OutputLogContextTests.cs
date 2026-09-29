using IDVBuff.Diagnostics;
using IDVBuff.Features.Maps;
using System.Runtime.InteropServices;

namespace IDVBuff.Tests;

[CollectionDefinition("Output log context", DisableParallelization = true)]
public sealed class OutputLogContextCollection;

[Collection("Output log context")]
public sealed class OutputLogContextTests
{
    [Theory]
    [InlineData(false, "awaiting-game-window")]
    [InlineData(true, "disabled-by-safe-mode")]
    public void HeaderRecordsApplicationVersionDeviceAndEffectiveSafeMode(bool safeMode, string regionState)
    {
        using var scope = new LogScope(safeMode);
        var text = ReadActiveLog(OutputLog.CurrentLogPath!);
        Assert.Contains("productVersion=9.8.7", text);
        Assert.Contains("buildVersion=b09.8-26.09.29.1234", text);
        Assert.Contains($"applicationSafeMode={safeMode.ToString().ToLowerInvariant()}", text);
        Assert.Matches(@"deviceResolution=\d+x\d+", text);
        Assert.Contains("deviceResolutionSource=primary-monitor", text);
        Assert.Contains($"recognitionRegionState={regionState}", text);
        Assert.Contains("recognitionRegionWidth=unknown", text);
        Assert.Contains("scanLogFile=none", text);
    }

    [Fact]
    public async Task ScanSessionRecordsBothFileNamesAndLateOldStopPreservesNewAssociation()
    {
        using var scope = new LogScope();
        var outputPath = OutputLog.CurrentLogPath!;
        await using var first = new MapLogCollector(new MapLogRepository(scope.Directory));
        await using var second = new MapLogCollector(new MapLogRepository(scope.Directory));
        first.IsEnabled = true;
        var firstPath = first.CurrentSessionPath!;
        second.IsEnabled = true;
        var secondPath = second.CurrentSessionPath!;
        first.IsEnabled = false;
        Assert.Equal(secondPath, OutputLog.GetContextDetails()["scanLogPath"]);
        var start = (await second.GetCompleteEntriesAsync()).Single(e => e.Message == "Log collection started");
        Assert.Equal(secondPath, start.Details!["sessionPath"]!.ToString());
        Assert.Equal(outputPath, start.Details["outputLogPath"]!.ToString());
        Assert.Equal("9.8.7", start.Details["productVersion"]!.ToString());
        second.IsEnabled = false;
        Assert.Null(OutputLog.GetContextDetails()["scanLogPath"]);
        var text = ReadActiveLog(outputPath);
        Assert.Contains($"scanLogFile={Path.GetFileName(firstPath)}", text);
        Assert.Contains($"scanLogFile={Path.GetFileName(secondPath)}", text);
        Assert.Contains("Scan log association ended", text);
    }

    [Fact]
    public void RecognitionDimensionsUpdateOnlyOnChangeAndClearWhenWindowDisappears()
    {
        using var scope = new LogScope();
        var path = OutputLog.CurrentLogPath!;
        OutputLog.UpdateCaptureContext(1920, 1080, 1035, 864, IntPtr.Zero);
        var firstLength = new FileInfo(path).Length;
        OutputLog.UpdateCaptureContext(1920, 1080, 1035, 864, IntPtr.Zero);
        Assert.Equal(firstLength, new FileInfo(path).Length);
        OutputLog.UpdateCaptureContext(3840, 2160, 2070, 1728, IntPtr.Zero);
        var details = OutputLog.GetContextDetails();
        Assert.Equal(3840, details["clientWidth"]);
        Assert.Equal(2070, details["recognitionRegionWidth"]);
        Assert.Equal(1728, details["recognitionRegionHeight"]);
        OutputLog.ClearCaptureContext();
        Assert.Null(OutputLog.GetContextDetails()["recognitionRegionWidth"]);
        var text = ReadActiveLog(path);
        Assert.Contains("clientWidth=1920 | clientHeight=1080 | recognitionRegionWidth=1035 | recognitionRegionHeight=864", text);
        Assert.Contains("recognitionRegionWidth=2070 | recognitionRegionHeight=1728", text);
        Assert.Contains("Recognition region unavailable", text);
    }

    [Fact]
    public void WindowMonitorResolutionIsRecordedSeparatelyFromGameClientDimensions()
    {
        using var scope = new LogScope();
        var desktopWindow = GetDesktopWindow();
        Assert.NotEqual(IntPtr.Zero, desktopWindow);
        OutputLog.UpdateCaptureContext(1600, 900, 860, 720, desktopWindow);
        var details = OutputLog.GetContextDetails();
        Assert.Equal("game-window-monitor", details["deviceResolutionSource"]);
        Assert.Matches(@"^\d+x\d+$", (string)details["deviceResolution"]!);
        Assert.Equal(1600, details["clientWidth"]);
        Assert.Equal(860, details["recognitionRegionWidth"]);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [Fact]
    public async Task RestartedOutputLogKeepsActiveScanAssociationAndDropsStaleCaptureGeometry()
    {
        using var scope = new LogScope();
        await using var collector = new MapLogCollector(new MapLogRepository(scope.Directory));
        collector.IsEnabled = true;
        var scanPath = collector.CurrentSessionPath!;
        var oldOutput = OutputLog.CurrentLogPath;
        OutputLog.UpdateCaptureContext(1920, 1080, 1035, 864, IntPtr.Zero);
        OutputLog.Shutdown();
        OutputLog.Initialize(scope.Directory, captureFirstChanceExceptions: false);
        Assert.NotEqual(oldOutput, OutputLog.CurrentLogPath);
        var text = ReadActiveLog(OutputLog.CurrentLogPath!);
        Assert.Contains($"scanLogFile={Path.GetFileName(scanPath)}", text);
        Assert.Contains("productVersion=9.8.7", text);
        Assert.Contains("recognitionRegionWidth=unknown", text);
        Assert.DoesNotContain("recognitionRegionWidth=1035", text);
    }

    private static string ReadActiveLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class LogScope : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "IDVB-OutputLogTests-" + Guid.NewGuid().ToString("N"));

        public LogScope(bool safeMode = false)
        {
            Assert.Null(OutputLog.CurrentLogPath);
            OutputLog.ConfigureApplication("9.8.7", "b09.8-26.09.29.1234", safeMode);
            OutputLog.Initialize(Directory, captureFirstChanceExceptions: false);
            Assert.NotNull(OutputLog.CurrentLogPath);
        }

        public void Dispose()
        {
            OutputLog.Shutdown();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
