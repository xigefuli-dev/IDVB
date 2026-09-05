using OpenCvSharp;

namespace IDVBuff.Features.Maps;
public sealed partial class MapScanDiagnostics
{

    /// <summary>Pure Task.Delay inside HandleGameMapToggleAsync.</summary>
    public double OpeningAnimationWaitMilliseconds { get; set; }

    /// <summary>Wall-clock inside WaitForStableViewportAsync (includes capture + Delay).</summary>
    public double StableViewportWaitMilliseconds { get; set; }

    /// <summary>Cumulative TryCaptureViewport time inside stability loop.</summary>
    public double StableViewportCaptureMilliseconds { get; set; }

    /// <summary>Stability loop attempt / capture counts.</summary>
    public int StableViewportAttempts { get; set; }
    public int StableViewportSuccessfulCaptures { get; set; }
    public string StableViewportMode { get; set; } = "readiness";
    public bool StableViewportFallback { get; set; }

    /// <summary>Floor: request enqueued → worker thread picks up.</summary>
    public double FloorQueueMilliseconds { get; set; }

    /// <summary>Floor: worker pickup → result produced.</summary>
    public double FloorWorkerMilliseconds { get; set; }

    /// <summary>Floor: enqueued → result produced (Queue + Worker).</summary>
    public double FloorRequestMilliseconds { get; set; }

    /// <summary>Floor: input → result (includes animation + stability overhead).</summary>
    public double FloorInputToResultMilliseconds { get; set; }

    /// <summary>Sum of Thread.Sleep / Task.Delay inside floor retry loop.</summary>
    public double FloorRetryWaitMilliseconds { get; set; }

    /// <summary>Worker wall time minus (capture + analysis) — true overhead.</summary>
    public double FloorWorkerOverheadMilliseconds { get; set; }

    /// <summary>Capture inside RunSelectedMapAlignmentAsync (after stability).</summary>
    public double AlignmentCaptureMilliseconds { get; set; }

    /// <summary>Task.Run dispatch overhead for alignment compute.</summary>
    public double AlignmentDispatchMilliseconds { get; set; }

    /// <summary>Cv2.ImRead for reference image (if not cached).</summary>
    public double ReferenceImageLoadMilliseconds { get; set; }

    /// <summary>_structureCache.GetOrCreate wall time.</summary>
    public double ReferenceCacheMilliseconds { get; set; }

    /// <summary>ProcessLiveRoi wall time inside structure registration.</summary>
    public double LiveStructurePreprocessMilliseconds { get; set; }

    /// <summary>ConfirmAlignmentCandidateAsync: pure Task.Delay inside loop.</summary>
    public double ConfirmationDelayMilliseconds { get; set; }

    /// <summary>ConfirmAlignmentCandidateAsync: TryCaptureViewport cumulative.</summary>
    public double ConfirmationCaptureMilliseconds { get; set; }

    /// <summary>ConfirmAlignmentCandidateAsync: Task.Run compute (excl. Delay + Capture).</summary>
    public double ConfirmationComputeMilliseconds { get; set; }

    /// <summary>Session transition + SaveAlignmentCalibrationAsync.</summary>
    public double SessionCommitMilliseconds { get; set; }

    /// <summary>Three target metrics — mutually exclusive.</summary>
    public double FirstCandidateMilliseconds { get; set; }
    public double AlignmentPipelineMilliseconds { get; set; }
}
