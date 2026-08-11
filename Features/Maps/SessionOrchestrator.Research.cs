namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// Converts one high-level alignment result into the stable research
    /// schema.  Callers invoke this only after the route has produced its
    /// final attempt, so OpenCV retries and intermediate candidates do not
    /// become separate research samples.
    /// </summary>
    private void RecordResearchAttempt(
        MapRecord map,
        string floorKey,
        CapturedGameFrame frame,
        MapRecognitionAttempt attempt,
        string floorSource,
        MapOverlayTransform? scaleSeed = null,
        IReadOnlyList<double>? searchRadii = null,
        int stableConfirmationFrames = 0,
        int stableConfirmationRequiredFrames = 0,
        bool calibrationUpdated = false,
        string? calibrationRejectionReason = null,
        RuntimeMapRecognition? recognitionOverride = null)
    {
        if (!_researchCollector.IsEnabled || _settings is not { } settings)
            return;

        _researchCollector.RecordAttempt(
            MapAlignmentResearchAttemptFactory.Create(
                map,
                floorKey,
                attempt,
                settings,
                SessionSnapshot,
                CreateWindowSignature(frame),
                floorSource,
                scaleSeed,
                searchRadii,
                stableConfirmationFrames,
                stableConfirmationRequiredFrames,
                calibrationUpdated,
                calibrationRejectionReason,
                recognitionOverride),
            map,
            floorKey,
            frame.Image);
    }

    private void RecordResearchAttemptForMap(
        MapRecord? map,
        string? floorKey,
        CapturedGameFrame frame,
        MapRecognitionAttempt attempt,
        string floorSource)
    {
        if (map is null)
            return;
        RecordResearchAttempt(
            map,
            floorKey
                ?? attempt.Recognition?.Result.Floor
                ?? MapFloorRules.GetPrimaryFloorKey(map),
            frame,
            attempt,
            floorSource);
    }

    private static MapWindowSignature CreateWindowSignature(CapturedGameFrame frame) =>
        new()
        {
            WindowHandle = frame.WindowHandle.ToInt64(),
            ClientX = (int)Math.Round(frame.ClientBounds.X),
            ClientY = (int)Math.Round(frame.ClientBounds.Y),
            ClientWidth = Math.Max(0, (int)Math.Round(frame.ClientBounds.Width)),
            ClientHeight = Math.Max(0, (int)Math.Round(frame.ClientBounds.Height)),
            ViewportX = (int)Math.Round(frame.ViewportBounds.X),
            ViewportY = (int)Math.Round(frame.ViewportBounds.Y),
            ViewportWidth = Math.Max(0, (int)Math.Round(frame.ViewportBounds.Width)),
            ViewportHeight = Math.Max(0, (int)Math.Round(frame.ViewportBounds.Height)),
            Dpi = DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle)
        };
}
