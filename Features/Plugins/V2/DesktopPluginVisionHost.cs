using IDVBuff.Features.Maps;
using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.Vision;
using IdentityVisionBridge.PluginSdk;

namespace IDVBuff.Features.Plugins.V2;

public sealed class DesktopPluginVisionHost(SessionOrchestrator session) : IPluginVisionHost, IPluginControlHost
{
    public Task<HostVisionSnapshot> GetSnapshotAsync(CancellationToken token) =>
        session.InvokePluginOperationAsync(() => Task.FromResult(session.GetPluginSnapshot()), token);
    public Task<VisionScanResult> ScanAsync(HostScanRequest request, CancellationToken token) =>
        session.InvokePluginOperationAsync(() => session.RunPluginScanAsync(request, token), token);
    public Task<VisionAlignmentResult> AlignAsync(CancellationToken token) =>
        session.InvokePluginOperationAsync(() => session.RunPluginAlignmentAsync(token), token);
    public Task<IReadOnlyList<string>> GetMapClassesAsync(CancellationToken token) =>
        session.InvokePluginOperationAsync(() => session.GetPluginMapClassesAsync(token), token);
    public Task<HostOperationResult> QueueMatchAsync(HostMatchRequest? request, CancellationToken token) =>
        session.InvokePluginOperationAsync(() => session.QueuePluginMatchAsync(request, token), token);
    public Task<HostOperationResult?> GetMatchOperationAsync(string id, CancellationToken token) =>
        session.InvokePluginOperationAsync(() => Task.FromResult(session.GetPluginMatchOperation(id)), token);
    public Task<HostOperationResult> SelectMapAsync(Guid id, string floor, CancellationToken token) =>
        session.InvokePluginOperationAsync(() => session.SelectPluginMapAsync(id, floor, token), token);
    public Task<HostOperationResult> SelectFloorAsync(string floor, CancellationToken token) =>
        session.InvokePluginOperationAsync(() => Task.FromResult(session.SelectPluginFloor(floor, token)), token);
    public Task<HostOperationResult> SetOverlayVisibleAsync(bool visible, CancellationToken token) =>
        session.InvokePluginOperationAsync(() => Task.FromResult(session.SetPluginOverlayVisible(visible, token)), token);
    public Task<HostSettingsSnapshot> GetSettingsAsync(CancellationToken token) =>
        session.InvokePluginOperationAsync(() => Task.FromResult(session.GetPluginSettings()), token);
    public Task<HostOperationResult> UpdateSettingsAsync(HostSettingsPatch patch, CancellationToken token) =>
        session.InvokePluginOperationAsync(() => session.UpdatePluginSettingsAsync(patch, token), token);
}
