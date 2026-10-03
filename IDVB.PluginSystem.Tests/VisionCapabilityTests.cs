using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.PluginSdk;
using IdentityVisionBridge.Vision;
using Xunit;

namespace IDVB.PluginSystem.Tests;

public sealed class VisionCapabilityTests
{
    [Fact]
    public async Task GrantsRemainSeparateAndHostCallsReachTheirActualAdapter()
    {
        var host = new Host();
        var engine = new Engine();
        var provider = new VisionCapabilityProvider(host, () => engine);
        var capabilities = new Dictionary<Type, IPluginCapability>();
        provider.AddCapabilities(capabilities, new HashSet<string> { PluginCapabilityIds.HostScanRun }, default);
        var scan = Assert.IsAssignableFrom<IHostScanCapability>(capabilities[typeof(IHostScanCapability)]);
        Assert.False(scan is IVisionAlignmentCapability);
        Assert.False(scan is IHostStateCapability);
        var request = new HostScanRequest { SelectedMapId = Guid.NewGuid() };
        var result = await scan.ScanAsync(request);
        Assert.Same(request, host.LastRequest);
        Assert.Equal("host-result", result.OperationId);
        Assert.False(engine.Used);
        await ((IRevocablePluginCapability)scan).RevokeAsync(default);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scan.ScanAsync(request));
    }

    [Fact]
    public async Task RevocationCancelsProcessingAndRejectsRetainedIdvpHandles()
    {
        var engine = new Engine { Block = true };
        var provider = new VisionCapabilityProvider(new Host(), () => engine);
        var capabilities = new Dictionary<Type, IPluginCapability>();
        provider.AddCapabilities(capabilities, new HashSet<string> { PluginCapabilityIds.VisionScan }, default);
        var scan = Assert.IsAssignableFrom<IVisionScanCapability>(capabilities[typeof(IVisionScanCapability)]);
        var request = new VisionScanRequest { Frame = new() { EncodedImage = [1] } };
        var running = scan.ScanAsync(request);
        await engine.Started.Task;
        await ((IRevocablePluginCapability)scan).RevokeAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(engine.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scan.ScanAsync(request));
    }

    [Fact]
    public async Task IdvpPackagingUsesTheHostsSharedVisionContracts()
    {
        using var fixture = new PluginPackageTestFixture();
        var contracts = typeof(IIdvbVisionEngine).Assembly.Location;
        File.Copy(contracts, Path.Combine(fixture.Source, Path.GetFileName(contracts)));
        var manifest = fixture.CreateManifest(capabilities: [PluginCapabilityIds.VisionScan, PluginCapabilityIds.VisionAlign,
            PluginCapabilityIds.HostStateRead, PluginCapabilityIds.HostScanRun, PluginCapabilityIds.HostAlignmentRun]);
        var path = await fixture.PackAsync(manifest);
        var package = await new IdentityVisionBridge.PluginPackaging.IdvpPackageReader().ValidateAsync(path,
            options: new() { AllowUnsigned = true, ExtractFiles = false });
        Assert.DoesNotContain(package.Manifest.Files, file => file.Path == Path.GetFileName(contracts));
        Assert.Equal(manifest.Capabilities, package.Manifest.Capabilities);
    }

    private sealed class Host : IPluginVisionHost
    {
        public HostScanRequest? LastRequest;
        public Task<HostVisionSnapshot> GetSnapshotAsync(CancellationToken token) => Task.FromResult(new HostVisionSnapshot());
        public Task<VisionScanResult> ScanAsync(HostScanRequest request, CancellationToken token)
        {
            LastRequest = request;
            return Task.FromResult(new VisionScanResult { OperationId = "host-result", Outcome = VisionOutcome.NeedsSelection });
        }
        public Task<VisionAlignmentResult> AlignAsync(CancellationToken token) => Task.FromResult(new VisionAlignmentResult
            { OperationId = "host-alignment", Outcome = VisionOutcome.Rejected });
    }
    private sealed class Engine : IIdvbVisionEngine
    {
        public bool Used, Disposed, Block;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VisionMap>> GetMapsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<VisionMap>>([]);
        public async Task<VisionScanResult> ScanAsync(VisionScanRequest request, CancellationToken token = default)
        {
            Used = true;
            Started.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, token);
            return new() { OperationId = "engine-result", Outcome = VisionOutcome.Unrecognized };
        }
        public Task<VisionAlignmentResult> AlignAsync(VisionAlignmentRequest request, CancellationToken token = default) =>
            Task.FromResult(new VisionAlignmentResult { OperationId = "engine-alignment", Outcome = VisionOutcome.Rejected });
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
