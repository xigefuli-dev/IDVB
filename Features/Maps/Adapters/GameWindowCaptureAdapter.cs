using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IGameWindowCapture 适配器 — 委托给 DwrGameWindowCaptureService。</summary>
public sealed class GameWindowCaptureAdapter : IGameWindowCapture
{
    private readonly DwrGameWindowCaptureService _capture = new();

    public bool TryGetForegroundClientBounds(out object clientBounds, out IntPtr windowHandle, out string failureReason)
    {
        var result = _capture.TryGetForegroundClientBounds(out var bounds, out windowHandle, out failureReason);
        clientBounds = bounds;
        return result;
    }

    public bool TryCaptureClient(out object? frame, out string failureReason)
    {
        var result = _capture.TryCaptureClient(out var f, out failureReason);
        frame = f;
        return result;
    }

    public bool TryCaptureViewport(object viewport, out object? frame, out string failureReason)
    {
        var result = _capture.TryCaptureViewport((NormalizedRectangle)viewport, out var f, out failureReason);
        frame = f;
        return result;
    }
}
