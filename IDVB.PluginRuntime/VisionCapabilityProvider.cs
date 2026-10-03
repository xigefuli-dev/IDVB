using IdentityVisionBridge.PluginSdk;
using IdentityVisionBridge.Vision;

namespace IdentityVisionBridge.PluginRuntime;

public interface IPluginVisionHost
{
    Task<HostVisionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<VisionScanResult> ScanAsync(HostScanRequest request, CancellationToken cancellationToken);
    Task<VisionAlignmentResult> AlignAsync(CancellationToken cancellationToken);
}

/// <summary>Creates separately granted capabilities and revokes both retained handles and running work.</summary>
public sealed partial class VisionCapabilityProvider(IPluginVisionHost host, Func<IIdvbVisionEngine> engineFactory)
{
    public void AddCapabilities(IDictionary<Type, IPluginCapability> capabilities,
        IReadOnlySet<string> granted, CancellationToken pluginLifetime)
    {
        if (!granted.Overlaps(new[] { PluginCapabilityIds.HostStateRead, PluginCapabilityIds.HostScanRun,
            PluginCapabilityIds.HostAlignmentRun, PluginCapabilityIds.VisionMapsRead, PluginCapabilityIds.VisionScan,
            PluginCapabilityIds.VisionAlign, PluginCapabilityIds.HostMatchControl, PluginCapabilityIds.HostMapControl,
            PluginCapabilityIds.HostOverlayControl, PluginCapabilityIds.HostSettingsControl })) return;
        var lease = new Lease(engineFactory, pluginLifetime);
        if (granted.Contains(PluginCapabilityIds.HostStateRead))
            capabilities[typeof(IHostStateCapability)] = new HostState(host, lease);
        if (granted.Contains(PluginCapabilityIds.HostScanRun))
            capabilities[typeof(IHostScanCapability)] = new HostScan(host, lease);
        if (granted.Contains(PluginCapabilityIds.HostAlignmentRun))
            capabilities[typeof(IHostAlignmentCapability)] = new HostAlignment(host, lease);
        if (granted.Contains(PluginCapabilityIds.VisionMapsRead))
            capabilities[typeof(IVisionMapsCapability)] = new Maps(lease);
        if (granted.Contains(PluginCapabilityIds.VisionScan))
            capabilities[typeof(IVisionScanCapability)] = new Scan(lease);
        if (granted.Contains(PluginCapabilityIds.VisionAlign))
            capabilities[typeof(IVisionAlignmentCapability)] = new Alignment(lease);
        if (host is IPluginControlHost control) AddControlCapabilities(capabilities, granted, control, lease);
    }

    private sealed class Lease(Func<IIdvbVisionEngine> factory, CancellationToken pluginLifetime)
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(pluginLifetime);
        private IIdvbVisionEngine? _engine;
        private Task? _revocation;
        private bool _revoked;

        public IIdvbVisionEngine Engine
        {
            get
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_revoked, this);
                    _lifetime.Token.ThrowIfCancellationRequested();
                    return _engine ??= factory();
                }
            }
        }

        public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
        {
            CancellationTokenSource linked;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_revoked, this);
                linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
            }
            using (linked)
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await action(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                return result;
            }
        }

        public ValueTask RevokeAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_revocation is not null) return new(_revocation);
                _revoked = true;
                _lifetime.Cancel();
                _revocation = DisposeEngineAsync();
                return new(_revocation);
            }
        }

        private async Task DisposeEngineAsync()
        {
            try { if (_engine is not null) await _engine.DisposeAsync().ConfigureAwait(false); }
            finally { _lifetime.Dispose(); }
        }
    }

    private abstract class Capability(Lease lease) : IRevocablePluginCapability
    {
        protected Lease Lifetime { get; } = lease;
        public ValueTask RevokeAsync(CancellationToken token) => Lifetime.RevokeAsync(token);
    }
    private sealed class HostState(IPluginVisionHost host, Lease lease) : Capability(lease), IHostStateCapability
    {
        public ValueTask<HostVisionSnapshot> GetSnapshotAsync(CancellationToken token = default) => new(Lifetime.RunAsync(host.GetSnapshotAsync, token));
    }
    private sealed class HostScan(IPluginVisionHost host, Lease lease) : Capability(lease), IHostScanCapability
    {
        public Task<VisionScanResult> ScanAsync(HostScanRequest request, CancellationToken token = default) =>
            Lifetime.RunAsync(ct => host.ScanAsync(request, ct), token);
    }
    private sealed class HostAlignment(IPluginVisionHost host, Lease lease) : Capability(lease), IHostAlignmentCapability
    {
        public Task<VisionAlignmentResult> AlignAsync(CancellationToken token = default) => Lifetime.RunAsync(host.AlignAsync, token);
    }
    private sealed class Maps(Lease lease) : Capability(lease), IVisionMapsCapability
    {
        public Task<IReadOnlyList<VisionMap>> GetMapsAsync(CancellationToken token = default) =>
            Lifetime.RunAsync(ct => Lifetime.Engine.GetMapsAsync(ct), token);
    }
    private sealed class Scan(Lease lease) : Capability(lease), IVisionScanCapability
    {
        public Task<VisionScanResult> ScanAsync(VisionScanRequest request, CancellationToken token = default) =>
            Lifetime.RunAsync(ct => Lifetime.Engine.ScanAsync(request, ct), token);
    }
    private sealed class Alignment(Lease lease) : Capability(lease), IVisionAlignmentCapability
    {
        public Task<VisionAlignmentResult> AlignAsync(VisionAlignmentRequest request, CancellationToken token = default) =>
            Lifetime.RunAsync(ct => Lifetime.Engine.AlignAsync(request, ct), token);
    }
}
