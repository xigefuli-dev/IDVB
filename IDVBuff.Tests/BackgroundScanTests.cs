using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class BackgroundScanTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void CandidateInputHandoffOnlyAppliesToInteractiveWindow(
        bool isHeadless,
        bool hasCandidateSelector,
        bool expected)
    {
        Assert.Equal(
            expected,
            BackgroundScanRules.ShouldWaitForCandidateInputHandoff(
                isHeadless,
                hasCandidateSelector));
        Assert.InRange(
            BackgroundScanRules.CandidatePresentationInputHandoffMilliseconds,
            1,
            200);
    }

    private static MapRecord CreateMap(string? title = null) => new()
    {
        Id = Guid.NewGuid(),
        UpdatedAt = DateTimeOffset.UtcNow,
        Title = title ?? "Test Map",
        Floors =
        [
            new FloorDefinition
            {
                Key = "1f", DisplayName = "1F", SortOrder = 1,
                ImageWidth = 200, ImageHeight = 150
            }
        ],
        Recognition = new MapRecognitionProfile
        {
            FirstFloor = new FloorRecognitionProfile
            {
                FloorKey = "1f",
                RecognitionPixelWidth = 200,
                RecognitionPixelHeight = 150
            }
        }
    };

    private static RuntimeMapRecognition CreateIdentity(MapRecord map) =>
        BackgroundScanRules.BuildIdentityOnlyRecognition(
            map, "1f", 0.8d, (_, _) => "overlay.png");

    // ── ClassifyBackgroundScan ──

    [Fact]
    public void ClassifyBackgroundScanWithIdentityIsIdentified()
    {
        var identity = CreateIdentity(CreateMap());

        var outcome = BackgroundScanRules.ClassifyBackgroundScan(
            identity, null, "应被忽略");

        Assert.Equal(BackgroundScanStatus.CompletedIdentified, outcome.Status);
        Assert.Same(identity, outcome.Identity);
        Assert.Null(outcome.Choices);
        Assert.Null(outcome.FailureReason);
    }

    [Fact]
    public void ClassifyBackgroundScanWithChoicesIsAmbiguous()
    {
        var choice = new MapRecognitionChoice
        {
            Recognition = CreateIdentity(CreateMap())
        };

        var outcome = BackgroundScanRules.ClassifyBackgroundScan(
            null, [choice], "候选待确认");

        Assert.Equal(BackgroundScanStatus.CompletedAmbiguous, outcome.Status);
        Assert.Null(outcome.Identity);
        Assert.Same(choice, Assert.Single(outcome.Choices!));
        Assert.Equal("候选待确认", outcome.FailureReason);
    }

    [Fact]
    public void ClassifyBackgroundScanWithFailureReasonIsFailed()
    {
        var outcome = BackgroundScanRules.ClassifyBackgroundScan(
            null, null, "识别失败：无匹配地图");

        Assert.Equal(BackgroundScanStatus.CompletedFailed, outcome.Status);
        Assert.Null(outcome.Identity);
        Assert.Null(outcome.Choices);
        Assert.Equal("识别失败：无匹配地图", outcome.FailureReason);
    }

    [Fact]
    public void ClassifyBackgroundScanWithAllNullsIsFailed()
    {
        var outcome = BackgroundScanRules.ClassifyBackgroundScan(
            null, null, null);

        Assert.Equal(BackgroundScanStatus.CompletedFailed, outcome.Status);
        Assert.Null(outcome.Identity);
        Assert.Null(outcome.Choices);
        Assert.Null(outcome.FailureReason);
    }

    // ── BuildIdentityOnlyRecognition ──

    [Fact]
    public void IdentityOnlyRecognitionCarriesNoTransformOrLocalization()
    {
        var map = CreateMap();

        var identity = BackgroundScanRules.BuildIdentityOnlyRecognition(
            map, "1f", 0.7d, (m, key) => $"overlay-{key}.png");

        Assert.Equal(map.Id, identity.Result.MapId);
        Assert.Equal("1f", identity.Result.Floor);
        Assert.Equal(0.7d, identity.Result.Confidence);
        Assert.Equal(0.7d, identity.Result.IdentityConfidence);
        Assert.Equal(0d, identity.Result.LocalizationConfidence);
        Assert.Equal(MapRecognitionSource.Automatic, identity.Result.Source);
        Assert.Null(identity.Result.OverlayTransform);
        Assert.Equal("overlay-1f.png", identity.FloorImagePath);
    }

    [Fact]
    public void IdentityOnlyRecognitionFallsBackToPrimaryFloorForUnknownFloor()
    {
        var map = CreateMap();

        var identity = BackgroundScanRules.BuildIdentityOnlyRecognition(
            map, "999f", 0.5d, (_, key) => key);

        Assert.Equal("1f", identity.Result.Floor);
        Assert.Equal("1f", identity.FloorImagePath);
    }

    // ── BuildBackgroundCandidateChoices ──

    [Fact]
    public void BackgroundChoicesPreserveCandidateOrderScoreAndIdentityShape()
    {
        var mapA = CreateMap("A");
        var mapB = CreateMap("B");
        var mapC = CreateMap("C");
        var candidates = new List<MapCandidate>
        {
            new() { MapId = mapA.Id.ToString(), MapDisplayName = "A", FloorKey = "1f", Score = 0.9d },
            new() { MapId = mapB.Id.ToString(), MapDisplayName = "B", FloorKey = "1f", Score = 0.8d },
            new() { MapId = mapC.Id.ToString(), MapDisplayName = "C", FloorKey = "1f", Score = 0.7d }
        };

        var choices = BackgroundScanRules.BuildBackgroundCandidateChoices(
            candidates,
            maxCandidates: 5,
            mapId => mapId == mapA.Id ? mapA : mapId == mapB.Id ? mapB : mapC,
            (map, floorKey, score) =>
                BackgroundScanRules.BuildIdentityOnlyRecognition(
                    map, floorKey, score, (_, _) => "overlay.png"),
            out var failureReason);

        Assert.Null(failureReason);
        var result = choices!;
        Assert.Equal(3, result.Count);
        for (var i = 0; i < result.Count; i++)
        {
            var choice = result[i];
            Assert.Equal(i, choice.PreferredOrder);
            Assert.False(choice.IsReferenceOnly);
            Assert.Equal(candidates[i].Score, choice.EvidenceScore);
            Assert.Null(choice.Recognition.Result.OverlayTransform);
            Assert.Equal(0d, choice.Recognition.Result.LocalizationConfidence);
        }
    }

    [Fact]
    public void BackgroundChoicesSkipInvalidMapIdsAndUnresolvableMaps()
    {
        var mapA = CreateMap("A");
        var candidates = new List<MapCandidate>
        {
            new() { MapId = "not-a-guid", MapDisplayName = "bad", Score = 0.9d },
            new() { MapId = mapA.Id.ToString(), MapDisplayName = "A", Score = 0.8d },
            new() { MapId = Guid.NewGuid().ToString(), MapDisplayName = "missing", Score = 0.7d }
        };

        var choices = BackgroundScanRules.BuildBackgroundCandidateChoices(
            candidates,
            maxCandidates: 5,
            mapId => mapId == mapA.Id ? mapA : null,
            (map, floorKey, score) =>
                BackgroundScanRules.BuildIdentityOnlyRecognition(
                    map, floorKey, score, (_, _) => "overlay.png"),
            out var failureReason);

        Assert.Null(failureReason);
        var only = Assert.Single(choices!);
        Assert.Equal(0, only.PreferredOrder);
        Assert.Equal(mapA.Id, only.Recognition.Result.MapId);
        Assert.Equal(0.8d, only.EvidenceScore);
    }

    [Fact]
    public void BackgroundChoicesReturnNullWhenNoCandidates()
    {
        var choices = BackgroundScanRules.BuildBackgroundCandidateChoices(
            [],
            maxCandidates: 5,
            _ => null,
            (_, _, _) => throw new InvalidOperationException("空候选不应构造身份"),
            out var failureReason);

        Assert.Null(choices);
        Assert.NotNull(failureReason);
    }

    [Fact]
    public void BackgroundChoicesRespectMaxCandidatesLimit()
    {
        var map = CreateMap("A");
        var candidates = Enumerable.Range(0, 8)
            .Select(i => new MapCandidate
            {
                MapId = map.Id.ToString(),
                MapDisplayName = $"M{i}",
                Score = 0.9d - i * 0.05d
            })
            .ToList();

        var choices = BackgroundScanRules.BuildBackgroundCandidateChoices(
            candidates,
            maxCandidates: 5,
            _ => map,
            (m, floorKey, score) =>
                BackgroundScanRules.BuildIdentityOnlyRecognition(
                    m, floorKey, score, (_, _) => "overlay.png"),
            out var failureReason);

        Assert.Null(failureReason);
        Assert.Equal(5, choices!.Count);
        Assert.Equal(0, choices[0].PreferredOrder);
        Assert.Equal(4, choices[4].PreferredOrder);
    }

    [Fact]
    public void AllTemplateCandidatesReceiveMandatoryFormalStructureRegistration()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(),
            "Features",
            "Maps",
            "SessionOrchestrator.Pipeline.InitialRecognition.SideEntrance.cs");
        var source = File.ReadAllText(sourcePath);
        var verificationIndex = source.IndexOf(
            "var verificationCandidates = candidates;",
            StringComparison.Ordinal);
        var backgroundCompletionIndex = source.LastIndexOf(
            "if (recognizeOnly)",
            StringComparison.Ordinal);

        Assert.True(verificationIndex >= 0);
        Assert.True(backgroundCompletionIndex > verificationIndex);
        Assert.Contains("RunMandatoryCandidateStructureRegistration(", source);
        Assert.Contains("CreateIndependentCandidateStructureSeed(", source);
        Assert.DoesNotContain("ScanVerificationMinimumCandidateBudgetMilliseconds", source);
        Assert.DoesNotContain("SelectVerificationCandidates", source);
        Assert.DoesNotContain(
            "BuildSideEntranceChoices",
            source,
            StringComparison.Ordinal);

        var mandatorySource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Features", "Maps", "AdaptiveScaleAlignment",
            "SessionOrchestrator.AdaptiveScaleSideEntrance.cs"));
        Assert.Contains("AlignLockedFloorFeature(", mandatorySource);
        Assert.Contains("vpsgAttempted: true", mandatorySource);

        var structureSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Features", "Maps",
            "MapCvAlignmentService.AlignSelected.Structure.cs"));
        Assert.Contains(
            "ScaleSearchPolicy = MapScaleSearchPolicy.Search",
            structureSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ForceBestCandidate = true",
            structureSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "&& !isScanVerification\n            && MapOpenAlignmentRouteRules.ShouldAttemptSideEntranceGlobalRecovery",
            structureSource,
            StringComparison.Ordinal);
    }

    // ── PickSideEntranceSeed ──

    private static MapAlignmentSession CreateSideEntranceSeed(
        MapRecord map,
        string floorKey = "1f",
        double prior = 0.8d) => new()
    {
        MapId = map.Id,
        MapUpdatedAt = map.UpdatedAt,
        FloorKey = floorKey,
        SideEntranceScanPriorConfidence = prior,
        HasGatePairLock = false,
        LockedTransform = new MapOverlayTransform
        {
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        }
    };

    [Fact]
    public void PickSideEntranceSeedReturnsNullWhenSeedIsNull()
    {
        var map = CreateMap();
        var identity = CreateIdentity(map);

        var picked = BackgroundScanRules.PickSideEntranceSeed(
            null, identity, "1f");

        Assert.Null(picked);
    }

    [Fact]
    public void PickSideEntranceSeedReturnsSeedWhenEverythingMatches()
    {
        var map = CreateMap();
        var identity = CreateIdentity(map);
        var seed = CreateSideEntranceSeed(map);

        var picked = BackgroundScanRules.PickSideEntranceSeed(
            seed, identity, "1f");

        Assert.Same(seed, picked);
    }

    [Fact]
    public void PickSideEntranceSeedReturnsNullWhenMapIdMismatches()
    {
        var map = CreateMap();
        var other = CreateMap("Other");
        var identity = CreateIdentity(other);
        var seed = CreateSideEntranceSeed(map);

        var picked = BackgroundScanRules.PickSideEntranceSeed(
            seed, identity, "1f");

        Assert.Null(picked);
    }

    [Fact]
    public void PickSideEntranceSeedReturnsNullWhenUpdatedAtMismatches()
    {
        var map = CreateMap();
        var identity = CreateIdentity(map);
        var seed = new MapAlignmentSession
        {
            MapId = map.Id,
            MapUpdatedAt = map.UpdatedAt.AddDays(-1),
            FloorKey = "1f",
            SideEntranceScanPriorConfidence = 0.8d,
            HasGatePairLock = false,
            LockedTransform = new MapOverlayTransform
            {
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            }
        };

        var picked = BackgroundScanRules.PickSideEntranceSeed(
            seed, identity, "1f");

        Assert.Null(picked);
    }

    [Fact]
    public void PickSideEntranceSeedReturnsNullWhenFloorKeyMismatches()
    {
        var map = CreateMap();
        var identity = CreateIdentity(map);
        var seed = CreateSideEntranceSeed(map, floorKey: "2f");

        var picked = BackgroundScanRules.PickSideEntranceSeed(
            seed, identity, "1f");

        Assert.Null(picked);
    }

    [Fact]
    public void PickSideEntranceSeedReturnsNullWhenPriorConfidenceIsZeroOrBelow()
    {
        var map = CreateMap();
        var identity = CreateIdentity(map);
        // KEEP-1.0 兜底种子（SideEntranceScanPriorConfidence == 0）不是侧门种子。
        var keepOne = CreateSideEntranceSeed(map, prior: 0d);

        var picked = BackgroundScanRules.PickSideEntranceSeed(
            keepOne, identity, "1f");

        Assert.Null(picked);
    }

    [Fact]
    public void VerifiedBackgroundStructurePreservesContentScaleAndSidePrior()
    {
        var map = CreateMap();
        var sideSeed = CreateSideEntranceSeed(map, prior: 0.93d);
        var verified = new RuntimeMapRecognition
        {
            Map = map,
            FloorImagePath = "overlay.png",
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = "1f",
                Confidence = 0.9185d,
                IdentityConfidence = 0.9075d,
                LocalizationConfidence = 0.9185d,
                EvidenceKind = MapAlignmentEvidenceKind.Structure,
                StructureDisposition =
                    MapStructureEvidenceDisposition.Supportive,
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 0.73602406768d,
                    ScaleY = 0.73602406768d,
                    OffsetX = 917d,
                    OffsetY = 44d,
                    ReferenceWidth = 200,
                    ReferenceHeight = 150,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                }
            }
        };

        var result = BackgroundScanRules.BuildValidatedStructureScaleSeed(
            verified,
            sideSeed,
            "1f");

        Assert.NotNull(result);
        Assert.Equal(0.73602406768d, result!.LockedTransform.ScaleX, 10);
        Assert.Equal(0.93d, result.SideEntranceScanPriorConfidence);
        Assert.Equal("1f", result.FloorKey);
        Assert.Equal(map.UpdatedAt, result.MapUpdatedAt);
    }

    [Theory]
    [InlineData(MapAlignmentEvidenceKind.None,
        MapStructureEvidenceDisposition.Supportive, "1f")]
    [InlineData(MapAlignmentEvidenceKind.Structure,
        MapStructureEvidenceDisposition.Inconclusive, "1f")]
    [InlineData(MapAlignmentEvidenceKind.Structure,
        MapStructureEvidenceDisposition.Supportive, "b1f")]
    public void UnverifiedOrOtherFloorBackgroundResultCannotBecomeScaleSeed(
        MapAlignmentEvidenceKind evidence,
        MapStructureEvidenceDisposition disposition,
        string floor)
    {
        var map = CreateMap();
        var recognition = new RuntimeMapRecognition
        {
            Map = map,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = floor,
                Confidence = 0.9d,
                LocalizationConfidence = 0.9d,
                EvidenceKind = evidence,
                StructureDisposition = disposition,
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 0.736d,
                    ScaleY = 0.736d,
                    ReferenceWidth = 200,
                    ReferenceHeight = 150,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                }
            }
        };

        Assert.Null(BackgroundScanRules.BuildValidatedStructureScaleSeed(
            recognition,
            CreateSideEntranceSeed(map),
            "1f"));
    }

}
