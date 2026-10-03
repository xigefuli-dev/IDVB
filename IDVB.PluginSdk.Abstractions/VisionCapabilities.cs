using IdentityVisionBridge.Vision;

namespace IdentityVisionBridge.PluginSdk;

public interface IHostStateCapability : IPluginCapability
{
    ValueTask<HostVisionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface IHostScanCapability : IPluginCapability
{
    Task<VisionScanResult> ScanAsync(HostScanRequest request, CancellationToken cancellationToken = default);
}

public interface IHostAlignmentCapability : IPluginCapability
{
    Task<VisionAlignmentResult> AlignAsync(CancellationToken cancellationToken = default);
}

public interface IVisionMapsCapability : IPluginCapability
{
    Task<IReadOnlyList<VisionMap>> GetMapsAsync(CancellationToken cancellationToken = default);
}

public interface IVisionScanCapability : IPluginCapability
{
    Task<VisionScanResult> ScanAsync(VisionScanRequest request, CancellationToken cancellationToken = default);
}

public interface IVisionAlignmentCapability : IPluginCapability
{
    Task<VisionAlignmentResult> AlignAsync(VisionAlignmentRequest request, CancellationToken cancellationToken = default);
}
