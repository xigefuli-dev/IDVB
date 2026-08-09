namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void ShowTransientOverlayStatus(
        MapOverlayStatusLevel level,
        string title,
        string message,
        string? detail,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle)
    {
        _overlayStatus.Show(
            new MapOverlayStatus(level, title, message, detail ?? string.Empty),
            gameBounds,
            gameWindowHandle,
            _settings?.ShowOverlayStatus ?? true,
            transient: true);
    }

    private void ShowTransientAlignmentSuccess(
        RuntimeMapRecognition recognition,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle,
        MapScanDiagnostics? diagnostics = null)
    {
        var route = MapAlignmentStatusText.Describe(recognition, diagnostics);
        ShowTransientOverlayStatus(
            MapOverlayStatusLevel.Success,
            route,
            $"{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()}",
            $"置信度 {recognition.Result.Confidence:P0}",
            gameBounds,
            gameWindowHandle);
    }
}
