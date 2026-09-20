// IDVB Real CLI — SessionResultBuilder 诊断与结果比对构建器
//
// 负责构建 OperationTrace、CodeExecutionPath、KeyEvents、VPSG 3.0 诊断、
// 双门检测、几何候选、IDVB 状态码及自动化对账比对结果。

using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using IDVBuff.RealCLI.Output;
using IDVBuff.RealCLI.Stubs;

namespace IDVBuff.RealCLI.Cli;

internal static partial class SessionResultBuilder
{
    public static RealCliOperationTraceOutput? BuildOperationTrace(SessionOrchestrator orchestrator)
    {
        var trace = orchestrator.LastScanOperationTrace ?? orchestrator.LastAlignmentOperationTrace;
        if (trace is null) return null;
        return new RealCliOperationTraceOutput
        {
            OperationType = trace.OperationType,
            TotalWallMs = trace.WallClockMs,
            Timeline = trace.ToHumanTimeline(),
            Spans = trace.Spans.Select(s => new RealCliTraceSpanOutput
            {
                Name = s.Name,
                DurationMs = s.DurationMs,
                StartOffsetMs = s.StartOffsetMs,
                WaitKind = s.WaitKind.ToString().ToLowerInvariant(),
                Status = s.Status.ToString().ToLowerInvariant(),
                Route = s.Route
            }).ToList()
        };
    }

    public static List<string> BuildCodeExecutionPath(SessionOrchestrator orchestrator)
    {
        var trace = orchestrator.LastScanOperationTrace ?? orchestrator.LastAlignmentOperationTrace;
        if (trace is not null && trace.Spans.Count > 0)
        {
            return trace.Spans
                .OrderBy(s => s.StartOffsetMs)
                .Select(s => $"[{s.Name}] {s.DurationMs:F1}ms ({s.WaitKind.ToString().ToLowerInvariant()}: {s.Status.ToString().ToLowerInvariant()})")
                .ToList();
        }

        var path = new List<string>();
        if (orchestrator.LastScanPhaseTimings is { } timings)
        {
            foreach (var kv in timings)
                path.Add($"[{kv.Key}] {kv.Value:F1}ms");
        }
        return path;
    }

    public static List<RealCliKeyEvent> BuildKeyEvents(SessionOrchestrator orchestrator)
    {
        var entries = orchestrator.LogCollector.GetEntries();
        if (entries is not { Count: > 0 }) return [];

        var list = new List<RealCliKeyEvent>();
        foreach (var e in entries)
        {
            if (e.Category is MapLogCategory.ScanLifecycle or MapLogCategory.StructureRegistration or MapLogCategory.Session or MapLogCategory.Overlay
                || e.Level >= MapLogLevel.Warning
                || e.StatusCode.HasValue)
            {
                list.Add(new RealCliKeyEvent
                {
                    TimestampMs = e.ElapsedMs ?? 0,
                    Category = e.Category.ToString(),
                    Message = e.Message,
                    Details = e.Details
                });
            }
        }
        return list;
    }

    public static RealCliVpsg3DiagnosticsOutput? BuildVpsg3Diagnostics(SessionOrchestrator orchestrator)
    {
        var entries = orchestrator.LogCollector.GetEntries();
        var voteEntry = entries.LastOrDefault(e => e.Message.Contains("VPSG3VoteDiagnostics", StringComparison.OrdinalIgnoreCase));
        var alignEntry = entries.LastOrDefault(e => e.Message.Contains("VPSG 3.0", StringComparison.OrdinalIgnoreCase) || (e.Details != null && e.Details.ContainsKey("stage") && e.Details["stage"]?.ToString()?.Contains("Vpsg3") == true));

        var diag = orchestrator.LastDiagnostics;
        var usedVpsg3 = diag?.ScaleBootstrapMode == "Vpsg3" || voteEntry != null || alignEntry != null;
        if (!usedVpsg3)
            return null;

        var details = voteEntry?.Details;
        var isAccepted = alignEntry?.Message.Contains("快速对齐通过", StringComparison.OrdinalIgnoreCase) == true
            || (diag != null && diag.StructureAccepted && diag.ScaleBootstrapMode == "Vpsg3");

        int sparseCount = 0;
        int hitsK5 = 0;
        int hitsK3 = 0;
        double margin = 0;
        double conf = diag?.StructureBestScore ?? 0;
        string? fallbackReason = null;

        if (details != null)
        {
            if (details.TryGetValue("sparsePointCount", out var sp) && int.TryParse(sp?.ToString(), out var spVal)) sparseCount = spVal;
            if (details.TryGetValue("hitsK5", out var k5) && int.TryParse(k5?.ToString(), out var k5Val)) hitsK5 = k5Val;
            if (details.TryGetValue("hitsK3", out var k3) && int.TryParse(k3?.ToString(), out var k3Val)) hitsK3 = k3Val;
            if (details.TryGetValue("weightedScore", out var ws) && double.TryParse(ws?.ToString(), out var wsVal)) conf = wsVal;
        }

        if (alignEntry != null && !isAccepted)
        {
            fallbackReason = alignEntry.Message;
            if (alignEntry.StatusChain is { } sc) fallbackReason += $" -> {sc}";
        }

        if (diag != null)
        {
            margin = diag.ScaleBootstrapMargin;
        }

        var lockedRec = GetEffectiveRecognition(orchestrator);
        var mapId = lockedRec?.Map.Id ?? Guid.Empty;
        var floorKey = lockedRec?.Result.Floor ?? "1f";
        var indexStatus = mapId != Guid.Empty ? orchestrator.Vpsg3Registry.GetStatus(mapId, floorKey).ToString() : "Missing";

        return new RealCliVpsg3DiagnosticsOutput
        {
            Attempted = true,
            IsAccepted = isAccepted,
            IndexStatus = indexStatus,
            Scale = diag?.ScaleBootstrapScale ?? 0,
            ApertureMargin = margin,
            Confidence = conf,
            SparsePointCount = sparseCount,
            HitsK5 = hitsK5,
            HitsK3 = hitsK3,
            FallbackReason = fallbackReason,
            DurationMs = alignEntry?.ElapsedMs ?? 0
        };
    }

    public static List<RealCliGateDetectionOutput> BuildGateDetections(SessionOrchestrator orchestrator)
    {
        var list = new List<RealCliGateDetectionOutput>();
        if (orchestrator.LastScanPipelineContext?.DetectedGates is { } gates)
        {
            foreach (var g in gates)
            {
                list.Add(new RealCliGateDetectionOutput
                {
                    X = g.ScreenBounds.X,
                    Y = g.ScreenBounds.Y,
                    Width = g.ScreenBounds.Width,
                    Height = g.ScreenBounds.Height,
                    Score = g.Score,
                    Scale = g.TemplateScale
                });
            }
        }
        else if (orchestrator.LastSideEntranceScan?.GateDetection.Gates is { } sideGates)
        {
            foreach (var g in sideGates)
            {
                list.Add(new RealCliGateDetectionOutput
                {
                    X = g.ScreenBounds.X,
                    Y = g.ScreenBounds.Y,
                    Width = g.ScreenBounds.Width,
                    Height = g.ScreenBounds.Height,
                    Score = g.Score,
                    Scale = g.Scale
                });
            }
        }
        return list;
    }

    public static List<RealCliGeometryCandidateOutput> BuildGeometryCandidates(SessionOrchestrator orchestrator)
    {
        var list = new List<RealCliGeometryCandidateOutput>();
        if (orchestrator.LastScanPipelineContext?.Candidates is { } candidates)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var nextScore = i + 1 < candidates.Count ? candidates[i + 1].Score : 0d;
                list.Add(new RealCliGeometryCandidateOutput
                {
                    MapId = c.MapId,
                    DisplayName = c.MapDisplayName,
                    Score = c.Score,
                    MarginToNext = c.Score - nextScore,
                    Selected = orchestrator.LastScanPipelineContext.SelectedCandidate?.MapId == c.MapId
                });
            }
        }
        else if (orchestrator.LastSideEntranceScan?.Candidates is { } sideCandidates)
        {
            for (var i = 0; i < sideCandidates.Count; i++)
            {
                var c = sideCandidates[i];
                var nextScore = i + 1 < sideCandidates.Count ? sideCandidates[i + 1].MatchScore : 0d;
                list.Add(new RealCliGeometryCandidateOutput
                {
                    MapId = c.Map.Id.ToString(),
                    DisplayName = c.Map.DisplayName,
                    Score = c.MatchScore,
                    MarginToNext = c.MatchScore - nextScore,
                    Selected = i == 0
                });
            }
        }
        return list;
    }

    public static RealCliIdvbStatusOutput? BuildIdvbStatus(SessionOrchestrator orchestrator)
    {
        var entries = orchestrator.LogCollector.GetEntries();
        var statusEntry = entries.LastOrDefault(e => e.StatusCode.HasValue);
        if (statusEntry is null || !statusEntry.StatusCode.HasValue)
            return null;

        return new RealCliIdvbStatusOutput
        {
            HttpCode = statusEntry.StatusCode.Value,
            SubCode = statusEntry.SubCode ?? 0,
            CodeName = statusEntry.SubCode.HasValue ? $"[SubCode {statusEntry.SubCode}]" : string.Empty,
            Stage = statusEntry.Details != null && statusEntry.Details.TryGetValue("stage", out var st) ? st?.ToString() ?? string.Empty : string.Empty,
            Message = statusEntry.Message,
            CausedBy = statusEntry.StatusChain
        };
    }

    public static (string Result, bool IsCorrect) VerifyResult(
        RuntimeMapRecognition? recognition,
        RealCliExpectedMap? expected,
        string? failureReason)
    {
        if (expected is null || (string.IsNullOrWhiteSpace(expected.MapId) && string.IsNullOrWhiteSpace(expected.MapDisplayName)))
        {
            return (recognition is not null ? "Matched" : "Unrecognized", recognition is not null);
        }

        if (recognition is null)
        {
            return ("Unrecognized", false);
        }

        var actualMap = recognition.Map;
        var mapMatched = false;

        if (!string.IsNullOrWhiteSpace(expected.MapId)
            && Guid.TryParse(expected.MapId, out var expectedGuid))
        {
            mapMatched = actualMap.Id == expectedGuid;
        }
        else if (!string.IsNullOrWhiteSpace(expected.MapDisplayName))
        {
            mapMatched = string.Equals(actualMap.DisplayName, expected.MapDisplayName, StringComparison.OrdinalIgnoreCase)
                || actualMap.DisplayName.Contains(expected.MapDisplayName, StringComparison.OrdinalIgnoreCase)
                || expected.MapDisplayName.Contains(actualMap.DisplayName, StringComparison.OrdinalIgnoreCase)
                || (int.TryParse(expected.MapDisplayName, out var seq) && actualMap.SequenceNumber == seq)
                || (expected.MapDisplayName.StartsWith("地图") && int.TryParse(expected.MapDisplayName[2..].Trim(), out var seq2) && actualMap.SequenceNumber == seq2)
                || (expected.MapDisplayName.StartsWith("map", StringComparison.OrdinalIgnoreCase) && int.TryParse(expected.MapDisplayName[3..].Trim('_', '-', ' '), out var seq3) && actualMap.SequenceNumber == seq3);
        }

        var floorMatched = true;
        if (!string.IsNullOrWhiteSpace(expected.Floor))
        {
            floorMatched = string.Equals(recognition.Result.Floor, expected.Floor, StringComparison.OrdinalIgnoreCase);
        }

        if (mapMatched && floorMatched)
            return ("Matched", true);

        return ("Mismatched", false);
    }

    public static (string Result, bool IsCorrect, RealCliExpectedMap? Expected) VerifyResult(
        SessionOrchestrator orchestrator,
        string? expectedString)
    {
        var rec = GetEffectiveRecognition(orchestrator);
        if (string.IsNullOrWhiteSpace(expectedString))
            return (rec != null ? "Matched" : "Unrecognized", rec != null, null);

        var exp = new RealCliExpectedMap();
        if (Guid.TryParse(expectedString, out var guid))
        {
            exp.MapId = guid.ToString();
        }
        else
        {
            exp.MapDisplayName = expectedString;
        }

        var (res, isCorrect) = VerifyResult(rec, exp, orchestrator.StatusMessage);
        return (res, isCorrect, exp);
    }

    public static RealCliSessionResult ExtractResult(
        SessionOrchestrator orchestrator,
        RecordingOverlayWindow overlay,
        string imagePath,
        double totalMs,
        string? error,
        string? expected = null)
    {
        var rec = GetEffectiveRecognition(orchestrator);
        var (verification, isCorrect, expObj) = VerifyResult(orchestrator, expected);
        var keyEvents = BuildKeyEvents(orchestrator);
        var codeExecutionPath = BuildCodeExecutionPath(orchestrator);
        var operationTrace = BuildOperationTrace(orchestrator);
        var vpsg3Diagnostics = BuildVpsg3Diagnostics(orchestrator);
        var gateDetections = BuildGateDetections(orchestrator);
        var geometryCandidates = BuildGeometryCandidates(orchestrator);
        var idvbStatus = BuildIdvbStatus(orchestrator);

        // 扫描管线各阶段耗时
        var scanPhaseTimings = orchestrator.LastScanPhaseTimings?
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new RealCliSessionResult
        {
            ImagePath = imagePath,
            Succeeded = rec is not null,
            StatusMessage = orchestrator.StatusMessage,
            Recognition = BuildRecognition(orchestrator),
            FailureReason = rec is null ? (orchestrator.StatusMessage ?? "识别失败：无结果") : null,
            BackgroundScanStatus = orchestrator.BackgroundScanStatus.ToString(),
            IsBackgroundScanCompleted = orchestrator.IsBackgroundScanCompleted,
            OverlayEvents = overlay.Events.ToList(),
            AlignmentSession = BuildAlignmentSession(orchestrator),
            ScanPhaseTimings = scanPhaseTimings,
            Diagnostics = BuildDiagnostics(orchestrator),
            LogEntries = BuildLogEntries(orchestrator),
            TotalWallMs = totalMs,
            FatalError = error,

            ExpectedMap = expObj,
            VerificationResult = verification,
            IsMatchCorrect = isCorrect,
            CodeExecutionPath = codeExecutionPath,
            KeyEvents = keyEvents,
            OperationTrace = operationTrace,
            Vpsg3Diagnostics = vpsg3Diagnostics,
            GateDetections = gateDetections,
            GeometryCandidates = geometryCandidates,
            IdvbStatus = idvbStatus
        };
    }
}
