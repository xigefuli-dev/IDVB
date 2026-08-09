// IDVB Remaster Phase 4 — Pipeline Orchestrator unit tests

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Xunit;

namespace IDVBuff.Tests;

public sealed class PipelineOrchestratorTests
{
    [Fact]
    public async Task RunAsync_Executes_All_Stages_In_Order()
    {
        var order = new List<string>();
        var stages = new IPipelineStage[]
        {
            new TestStage("A", order),
            new TestStage("B", order),
            new TestStage("C", order),
        };
        var orchestrator = new PipelineOrchestrator(stages);
        var context = new PipelineContext();

        var result = await orchestrator.RunAsync(context);

        Assert.False(result.IsFailed);
        Assert.Equal(["A", "B", "C"], order);
        Assert.True(result.TotalWallMs >= 0);
    }

    [Fact]
    public async Task RunAsync_Records_Phase_Timings()
    {
        var stages = new IPipelineStage[]
        {
            new TestStage("load", new List<string>()),
            new TestStage("process", new List<string>()),
        };
        var orchestrator = new PipelineOrchestrator(stages);
        var context = new PipelineContext();

        var result = await orchestrator.RunAsync(context);

        Assert.True(result.GetPhaseMs("load") >= 0);
        Assert.True(result.GetPhaseMs("process") >= 0);
    }

    [Fact]
    public async Task RunAsync_Stops_On_Failure()
    {
        var order = new List<string>();
        var stages = new IPipelineStage[]
        {
            new TestStage("A", order),
            new FailingStage("fail"),
            new TestStage("B", order), // should NOT execute
        };
        var orchestrator = new PipelineOrchestrator(stages);
        var context = new PipelineContext();

        var result = await orchestrator.RunAsync(context);

        Assert.True(result.IsFailed);
        Assert.Equal(["A"], order); // B not reached
    }

    [Fact]
    public async Task RunAsync_Fallback_Executes_On_Failure()
    {
        var executed = new List<string>();
        var stages = new IPipelineStage[] { new FailingStage("fail") };
        var fallback = new PipelineFallbackChain()
            .WithFallback("fail", new TestStage("fallback", executed));
        var orchestrator = new PipelineOrchestrator(stages, fallback);

        var result = await orchestrator.RunAsync(new PipelineContext());

        Assert.False(result.IsFailed); // fallback succeeded
        Assert.Contains("fallback", executed);
    }

    [Fact]
    public void PipelineContext_Fail_Sets_IsFailed()
    {
        var ctx = new PipelineContext();
        ctx.Fail("test reason");
        Assert.True(ctx.IsFailed);
        Assert.Equal("test reason", ctx.FailureReason);
    }

    [Fact]
    public void ScanPipelineContext_Initializes_Empty()
    {
        var ctx = new ScanPipelineContext();
        Assert.Empty(ctx.DetectedGates);
        Assert.Empty(ctx.Candidates);
        Assert.Null(ctx.Result);
    }

    [Fact]
    public void AlignmentPipelineContext_Tracks_Attempted_Strategies()
    {
        var ctx = new AlignmentPipelineContext();
        ctx.MarkStrategyAttempted("dual_gate");
        Assert.True(ctx.HasTriedStrategy("dual_gate"));
        Assert.False(ctx.HasTriedStrategy("single_gate"));
    }

    private sealed class TestStage : IPipelineStage
    {
        private readonly List<string> _log;
        public string StageName { get; }
        public TestStage(string name, List<string> log) { StageName = name; _log = log; }
        public Task<PipelineContext> ExecuteAsync(PipelineContext ctx, CancellationToken ct)
        {
            _log.Add(StageName);
            return Task.FromResult(ctx);
        }
    }

    private sealed class FailingStage : IPipelineStage
    {
        public string StageName { get; }
        public FailingStage(string name) { StageName = name; }
        public Task<PipelineContext> ExecuteAsync(PipelineContext ctx, CancellationToken ct)
            => Task.FromResult(ctx.Fail("test failure"));
    }
}
