namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private static void MergeScanVerificationCounters(
        MapScanDiagnostics target,
        MapScanDiagnostics source)
    {
        target.ScanFormalStructureAttemptCount += source.ScanFormalStructureAttemptCount;
        target.ScanShadowPairCount += source.ScanShadowPairCount;
        target.ScanShadowTrueFormalFalseCount += source.ScanShadowTrueFormalFalseCount;
        target.ScanShadowFalseFormalTrueCount += source.ScanShadowFalseFormalTrueCount;
        target.ScanShadowTrueFormalTrueCount += source.ScanShadowTrueFormalTrueCount;
        target.ScanShadowFalseFormalFalseCount += source.ScanShadowFalseFormalFalseCount;
    }

    private void LogScanVerificationStage(
        string stage,
        SideEntranceScanCandidate candidate,
        MapRecognitionAttempt? attempt,
        bool adaptiveQualified,
        bool shortCircuited)
    {
        var rawChamfer = SideEntranceCandidateEvidence.ResolveRawChamferPixels(
            attempt?.StructureResult);
        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            stage,
            details: new()
            {
                ["map"] = candidate.Map.DisplayName,
                ["mapId"] = candidate.Map.Id,
                ["floor"] = candidate.FloorKey,
                ["structureAttempted"] = attempt?.StructureAttempted ?? false,
                ["structureAccepted"] = attempt?.StructureAccepted ?? false,
                ["hasRecognition"] = attempt?.Recognition is not null,
                ["confidence"] = attempt?.Recognition?.Result.Confidence,
                ["chamfer"] = double.IsFinite(rawChamfer) ? rawChamfer : null,
                ["candidateMargin"] = attempt?.Recognition?.Result.StructureCandidateMargin
                    ?? attempt?.StructureResult?.CandidateMargin,
                ["adaptiveQualified"] = adaptiveQualified,
                ["shortCircuited"] = shortCircuited,
                ["failureReason"] = attempt?.FailureReason
            });
    }

    private static void PopulateScanAttemptTiming(
        MapRecognitionAttempt attempt,
        double templateMilliseconds,
        double vpsgMilliseconds,
        bool vpsgAttempted)
    {
        var diagnostics = attempt.Diagnostics;
        diagnostics.ScanTemplateValidationMilliseconds = templateMilliseconds;
        diagnostics.ScanVpsgMilliseconds = vpsgMilliseconds;
        diagnostics.ScanVpsgAttempted = vpsgAttempted;
        diagnostics.ScanStructureMilliseconds = diagnostics.StructurePreprocessMilliseconds
            + diagnostics.StructureSearchMilliseconds
            + diagnostics.StructureRefineMilliseconds;
    }
}
