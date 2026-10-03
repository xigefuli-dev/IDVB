using IdentityVisionBridge.Vision;

namespace IdentityVisionBridge.PluginSdk;

public enum HostOperationState { Accepted, Applied, Rejected, Failed, Busy, NotReady }
public sealed record HostOperationResult(string OperationId, HostOperationState State, string Message)
{
    public HostVisionSnapshot? Snapshot { get; init; }
}

public sealed record HostMatchRequest
{
    public required string MapClass { get; init; }
    public bool EndCurrentMatch { get; init; } = true;
}

/// <summary>Granted controllers remain active between matches. Accepted transitions belong to the host.</summary>
public interface IHostMatchCapability : IPluginCapability
{
    Task<IReadOnlyList<string>> GetMapClassesAsync(CancellationToken cancellationToken = default);
    Task<HostOperationResult> SwitchAndBeginAsync(HostMatchRequest request, CancellationToken cancellationToken = default);
    Task<HostOperationResult> EndAsync(CancellationToken cancellationToken = default);
    Task<HostOperationResult?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default);
}

public interface IHostMapControlCapability : IPluginCapability
{
    Task<HostOperationResult> SelectMapAsync(Guid mapId, string floorKey, CancellationToken cancellationToken = default);
    Task<HostOperationResult> SelectFloorAsync(string floorKey, CancellationToken cancellationToken = default);
}

public interface IHostOverlayCapability : IPluginCapability
{
    Task<HostOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);
}

public sealed record HostSettingsSnapshot(bool IsEnabled, VisionScanMode ScanMode, bool DisableAutoFloor,
    double MapOpacity, double MiniMapScale);
public sealed record HostSettingsPatch
{
    public bool? IsEnabled { get; init; }
    public VisionScanMode? ScanMode { get; init; }
    public bool? DisableAutoFloor { get; init; }
    public double? MapOpacity { get; init; }
    public double? MiniMapScale { get; init; }
}
public interface IHostSettingsCapability : IPluginCapability
{
    Task<HostSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default);
    Task<HostOperationResult> UpdateAsync(HostSettingsPatch patch, CancellationToken cancellationToken = default);
}
