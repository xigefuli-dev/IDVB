// IDVB Remaster — Session Orchestrator 识别管线

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private sealed class InitialRecognitionPipelineState
    {
        public RuntimeMapRecognition? Recognition;
        public string? FailureReason;
        public IReadOnlyList<MapRecognitionChoice>? PendingChoices;
        public string PendingChoicesReason = string.Empty;
        public MapAlignmentSession? PendingSideEntranceSeed;
        public RuntimeMapRecognition? PendingSideEntranceIdentity;
        public SideEntranceScanResult? PendingSideEntranceScan;
        public Dictionary<Guid, MapFeatureCacheKey> RepairCacheKeys = new();
        public bool ScanSucceeded;
    }

    private void RunInitialRecognition(
        CapturedGameFrame frame,
        InitialRecognitionPipelineState result)
    {
        if (_settings!.FirstScanStrategy == FirstScanStrategy.SideEntrance)
        {
            RunInitialSideEntranceRecognition(frame, result);
            return;
        }

        RunInitialDefaultRecognition(frame, result);
    }

}
