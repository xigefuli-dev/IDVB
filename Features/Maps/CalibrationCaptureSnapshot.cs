namespace IDVBuff.Features.Maps;
public enum CalibrationRegionKind { MapViewport, FloorDisplay, NativeMiniMap }

public sealed record CalibrationWindowIdentity(IntPtr WindowHandle,uint ProcessId,
    int ClientWidth,int ClientHeight,uint Dpi)
{
    public bool Matches(CalibrationWindowIdentity? current) => current is not null
        && WindowHandle==current.WindowHandle && ProcessId==current.ProcessId
        && ClientWidth==current.ClientWidth && ClientHeight==current.ClientHeight && Dpi==current.Dpi;
}
public sealed record CalibrationCaptureSnapshot(CapturedGameFrame Frame,
    CalibrationWindowIdentity Window,DateTimeOffset CapturedAt,long ConfigurationRevision=0,string Preset=""):IDisposable
{
    public void Dispose()=>Frame.Dispose();
}
