using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapRequestDiagnosticsTests
{
    [Fact]
    public void CapturedCallbackDoesNotAcquireAnotherRequestsIdentityOrTiming()
    {
        using var collector = new MapLogCollector(new MapLogRepository(
            Path.Combine(Path.GetTempPath(), "IDVB-RequestDiagnostics-" + Guid.NewGuid().ToString("N"))))
            { DisablePersistence = true };
        collector.IsEnabled = true;
        using var input = new MapInputOperationContext();
        using var request = new ScanRequestDiagnostics();
        using var execution = ScanExecutionContext.Enter(ScanPerformanceMode.Balanced);
        collector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info, "late-callback",
            details: new() { ["scanId"] = "old-scan", ["inputOperationId"] = "old-input" });
        var details = collector.GetEntries().Single(e => e.Message == "late-callback").Details!;
        Assert.Equal("old-scan", details["scanId"]);
        Assert.Equal("old-input", details["inputOperationId"]);
        Assert.False(details.ContainsKey("scanElapsedMs"));
        Assert.False(details.ContainsKey("requestStage"));
    }

    [Theory]
    [InlineData(0.7231875)]
    [InlineData(0.73625)]
    [InlineData(0.705125)]
    public void ReportedFramesIdentifyColorRejectionInsteadOfCaptureFailure(double fraction)
    {
        var frame = new MapViewportColorSignature([1d], fraction, 50);
        var result = MapViewportPresenceDetector.EvaluateReady(frame, previousFrame: frame);
        Assert.False(result.IsPresent);
        var checks = Assert.IsType<MapViewportPresenceChecks>(result.Checks);
        Assert.False(checks.HasUsableReference);
        Assert.False(checks.ColorPassed);
        Assert.Equal(0.85, checks.ColorThreshold);
        Assert.True(checks.BrightnessPassed);
        Assert.True(checks.StructurePassed);
    }

    [Fact]
    public void MissingPreviousFrameReportsBrightnessGateEvenWhenColorPasses()
    {
        var result = MapViewportPresenceDetector.EvaluateReady(
            new MapViewportColorSignature([1d], 0.878375, 50));
        Assert.False(result.IsPresent);
        Assert.True(result.Checks!.ColorPassed);
        Assert.False(result.Checks.HasPreviousFrame);
        Assert.False(result.Checks.BrightnessPassed);
        Assert.Null(result.Checks.BrightnessDelta);
    }

    [Fact]
    public async Task InterleavedRequestsKeepCorrelationAndLateTerminalVerdictsSeparate()
    {
        {
            using var collector = new MapLogCollector(new MapLogRepository(
                Path.Combine(Path.GetTempPath(), "IDVB-RequestDiagnostics-" + Guid.NewGuid().ToString("N"))))
                { DisablePersistence = true };
            collector.IsEnabled = true;
            var firstReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Emit(bool first)
            {
                using var input = new MapInputOperationContext();
                using var request = new ScanRequestDiagnostics();
                input.ScanId = request.ScanId;
                using var execution = ScanExecutionContext.Enter(ScanPerformanceMode.Balanced);
                Assert.Equal(request.ScanId, execution.ScanId);
                if (first) { firstReady.SetResult(true); await secondDone.Task; }
                else { await firstReady.Task; }
                request.Stage = "guard";
                request.Complete(first ? "superseded" : "rejected", first ? "new-scan-request" : "match-not-started");
                collector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning, first ? "old-end" : "new-end",
                    details: new() { ["outcome"] = request.Outcome, ["reason"] = request.Reason });
                if (!first) secondDone.SetResult(true);
            }
            await Task.WhenAll(Emit(true), Emit(false));
            var entries = collector.GetEntries().Where(e => e.Message.EndsWith("-end")).ToArray();
            Assert.Equal(2, entries.Length);
            Assert.Equal(2, entries.Select(e => e.Details!["scanId"]).Distinct().Count());
            Assert.Equal(2, entries.Select(e => e.Details!["inputOperationId"]).Distinct().Count());
            Assert.Equal("match-not-started", entries.Single(e => e.Message == "new-end").Details!["reason"]);
            Assert.Equal("new-scan-request", entries.Single(e => e.Message == "old-end").Details!["reason"]);
            Assert.Null(ScanRequestDiagnostics.Current);
            Assert.Null(MapInputOperationContext.Current);
        }
    }
}
