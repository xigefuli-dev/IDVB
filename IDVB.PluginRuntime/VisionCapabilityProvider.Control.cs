using IdentityVisionBridge.PluginSdk;

namespace IdentityVisionBridge.PluginRuntime;

public interface IPluginControlHost
{
    Task<IReadOnlyList<string>> GetMapClassesAsync(CancellationToken token);
    Task<HostOperationResult> QueueMatchAsync(HostMatchRequest? request, CancellationToken token);
    Task<HostOperationResult?> GetMatchOperationAsync(string operationId, CancellationToken token);
    Task<HostOperationResult> SelectMapAsync(Guid mapId, string floorKey, CancellationToken token);
    Task<HostOperationResult> SelectFloorAsync(string floorKey, CancellationToken token);
    Task<HostOperationResult> SetOverlayVisibleAsync(bool visible, CancellationToken token);
    Task<HostSettingsSnapshot> GetSettingsAsync(CancellationToken token);
    Task<HostOperationResult> UpdateSettingsAsync(HostSettingsPatch patch, CancellationToken token);
}

public sealed partial class VisionCapabilityProvider
{
    private static void AddControlCapabilities(IDictionary<Type, IPluginCapability> capabilities,
        IReadOnlySet<string> granted, IPluginControlHost host, Lease lease)
    {
        if (granted.Contains(PluginCapabilityIds.HostMatchControl))
            capabilities[typeof(IHostMatchCapability)] = new Match(host, lease);
        if (granted.Contains(PluginCapabilityIds.HostMapControl))
            capabilities[typeof(IHostMapControlCapability)] = new MapControl(host, lease);
        if (granted.Contains(PluginCapabilityIds.HostOverlayControl))
            capabilities[typeof(IHostOverlayCapability)] = new Overlay(host, lease);
        if (granted.Contains(PluginCapabilityIds.HostSettingsControl))
            capabilities[typeof(IHostSettingsCapability)] = new Settings(host, lease);
    }
    private sealed class Match(IPluginControlHost host, Lease lease) : Capability(lease), IHostMatchCapability
    {
        public Task<IReadOnlyList<string>> GetMapClassesAsync(CancellationToken token = default) => Lifetime.RunAsync(host.GetMapClassesAsync, token);
        public Task<HostOperationResult> SwitchAndBeginAsync(HostMatchRequest request, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Lifetime.RunAsync(ct => host.QueueMatchAsync(request, ct), token);
        }
        public Task<HostOperationResult> EndAsync(CancellationToken token = default) => Lifetime.RunAsync(ct => host.QueueMatchAsync(null, ct), token);
        public Task<HostOperationResult?> GetOperationAsync(string id, CancellationToken token = default) =>
            Lifetime.RunAsync(ct => host.GetMatchOperationAsync(id, ct), token);
    }
    private sealed class MapControl(IPluginControlHost host, Lease lease) : Capability(lease), IHostMapControlCapability
    {
        public Task<HostOperationResult> SelectMapAsync(Guid id, string floor, CancellationToken token = default) =>
            Lifetime.RunAsync(ct => host.SelectMapAsync(id, floor, ct), token);
        public Task<HostOperationResult> SelectFloorAsync(string floor, CancellationToken token = default) =>
            Lifetime.RunAsync(ct => host.SelectFloorAsync(floor, ct), token);
    }
    private sealed class Overlay(IPluginControlHost host, Lease lease) : Capability(lease), IHostOverlayCapability
    {
        public Task<HostOperationResult> SetVisibleAsync(bool visible, CancellationToken token = default) =>
            Lifetime.RunAsync(ct => host.SetOverlayVisibleAsync(visible, ct), token);
    }
    private sealed class Settings(IPluginControlHost host, Lease lease) : Capability(lease), IHostSettingsCapability
    {
        public Task<HostSettingsSnapshot> GetAsync(CancellationToken token = default) => Lifetime.RunAsync(host.GetSettingsAsync, token);
        public Task<HostOperationResult> UpdateAsync(HostSettingsPatch patch, CancellationToken token = default) =>
            Lifetime.RunAsync(ct => host.UpdateSettingsAsync(patch, ct), token);
    }
}
