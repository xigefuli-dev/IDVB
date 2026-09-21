using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class SilentScanCandidateVerificationTests
{
    [Fact]
    public void MapScanDiagnosticsTracksVerificationCompletenessFlags()
    {
        var diagnostics = new MapScanDiagnostics
        {
            ScanCandidateCount = 9,
            ScanVerificationCandidateCount = 9,
            ScanVerifiedCandidateCount = 9,
            ScanEarlyExited = false,
            ScanVerificationTimedOut = false
        };

        Assert.Equal(9, diagnostics.ScanCandidateCount);
        Assert.Equal(9, diagnostics.ScanVerificationCandidateCount);
        Assert.Equal(9, diagnostics.ScanVerifiedCandidateCount);
        Assert.False(diagnostics.ScanEarlyExited);
        Assert.False(diagnostics.ScanVerificationTimedOut);
    }

    [Fact]
    public void ClassifyBackgroundScanWithAmbiguousChoicesYieldsAmbiguousStatus()
    {
        // When multiple candidates pass structure registration, PendingChoices is set.
        var map1 = new MapRecord { Id = Guid.NewGuid(), Title = "地图 1" };
        var map13 = new MapRecord { Id = Guid.NewGuid(), Title = "地图 13" };

        var choices = new List<MapRecognitionChoice>
        {
            new()
            {
                Recognition = new RuntimeMapRecognition { Map = map1 },
                EvidenceScore = 0.77d
            },
            new()
            {
                Recognition = new RuntimeMapRecognition { Map = map13 },
                EvidenceScore = 0.75d
            }
        };

        var outcome = BackgroundScanRules.ClassifyBackgroundScan(
            identity: null,
            choices: choices,
            failureReason: null);

        Assert.Equal(BackgroundScanStatus.CompletedAmbiguous, outcome.Status);
        Assert.Null(outcome.Identity);
        Assert.NotNull(outcome.Choices);
        Assert.Equal(2, outcome.Choices.Count);
    }

    [Fact]
    public void ClassifyBackgroundScanWithSingleIdentityYieldsIdentifiedStatus()
    {
        var map = new MapRecord { Id = Guid.NewGuid(), Title = "确定地图" };
        var recognition = new RuntimeMapRecognition
        {
            Map = map,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = "1f",
                Confidence = 0.85d,
                IdentityConfidence = 0.85d,
                EvidenceKind = MapAlignmentEvidenceKind.Structure
            }
        };

        var outcome = BackgroundScanRules.ClassifyBackgroundScan(
            identity: recognition,
            choices: null,
            failureReason: null);

        Assert.Equal(BackgroundScanStatus.CompletedIdentified, outcome.Status);
        Assert.NotNull(outcome.Identity);
        Assert.Equal(map.Id, outcome.Identity.Map.Id);
        Assert.Null(outcome.Choices);
    }

    [Fact]
    public void PickSideEntranceSeedRequiresPositivePriorConfidence()
    {
        var map = new MapRecord { Id = Guid.NewGuid(), Title = "地图 1" };
        var identity = new RuntimeMapRecognition
        {
            Map = map,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = "1f",
                Confidence = 0.8d
            }
        };

        var zeroPriorSeed = new MapAlignmentSession
        {
            MapId = map.Id,
            FloorKey = "1f",
            SideEntranceScanPriorConfidence = 0d
        };

        var validPriorSeed = new MapAlignmentSession
        {
            MapId = map.Id,
            FloorKey = "1f",
            SideEntranceScanPriorConfidence = 0.75d
        };

        Assert.Null(BackgroundScanRules.PickSideEntranceSeed(zeroPriorSeed, identity, "1f"));
        Assert.NotNull(BackgroundScanRules.PickSideEntranceSeed(validPriorSeed, identity, "1f"));
        Assert.Null(BackgroundScanRules.PickSideEntranceSeed(validPriorSeed, identity, "2f"));
    }
}
