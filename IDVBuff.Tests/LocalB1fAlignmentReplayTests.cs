using System.Security.Cryptography;
using System.Text.Json;
using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;
using IDVBuff.Infrastructure.Configuration;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed partial class LocalB1fAlignmentReplayTests
{
    [B1fReplayFact]
    public void Build6036WarmBasementFramesReachAcceptedFloorVerdict()
    {
        var match = Environment.GetEnvironmentVariable("IDVB_B1F_REPLAY")!;
        var report = Environment.GetEnvironmentVariable("IDVB_B1F_REPORT")!;
        var appRoot = Environment.GetEnvironmentVariable("IDVB_B1F_APP_ROOT")!;
        var mapsRoot = Path.Combine(appRoot, "Maps");
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapsRoot, "maps.json")));
        var mapId = Guid.Parse("fcecdfde-ca03-47ae-91aa-00e88aea62c8");
        var map = catalog.RootElement.GetProperty("Maps").Deserialize<MapRecord[]>()!
            .Single(candidate => candidate.Id == mapId);
        var profile = MapFloorRules.GetFloorProfile(map, "b1f")!;
        var referencePath = Path.Combine(mapsRoot, mapId.ToString("N"), "floor-b1f-recognition.png");
        using var config = new TomlConfigProvider(appRoot);
        config.SetActivePreset("2560x1600");
        var baseTuning = MapAlignmentChannelRegistry.CreateLowStructure(config.Get<LowStructureConfig>("low_structure"));
        Assert.Equal("50cd75440a6fb8fd", baseTuning.CacheFingerprint);
        using var referenceImage = Cv2.ImDecode(File.ReadAllBytes(referencePath), ImreadModes.Unchanged);
        var preprocessor = new MapStructurePreprocessor();
        using var reference = preprocessor.ProcessReference(referenceImage,
            profile.WholeImageIgnoreRegions, baseTuning.Generation,
            MapStructurePreprocessingProfile.EdgesOnly);
        var registrar = new MapStructureRegistrar(preprocessor);
        var viewport = new MapScreenRect(832d, 270d, 1321d, 1055d);
        var seed = new MapOverlayTransform
        {
            ScaleX = 1.57476884614911, ScaleY = 1.57476884614911,
            OffsetX = 959.556276538078, OffsetY = -32.35561846062916,
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            ReferenceWidth = 531, ReferenceHeight = 572
        };
        var rows = new List<object>();
        var failures = new List<string>();
        for (var index = 29; index <= 63; index++)
        {
            var path = Path.Combine(match, "显示区域", $"显示区域 {index}.png");
            using var frame = new CapturedGameFrame(
                Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color),
                new MapScreenRect(0d, 0d, 2560d, 1600d), viewport, IntPtr.Zero);
            var live = frame.GetOrCreateDefaultLiveStructureFeatures(preprocessor,
                MapStructurePreprocessingProfile.EdgesOnly, out _, out _, out _,
                generateVisibleMask: true, generationTuning: baseTuning.Generation);
            var tuning = baseTuning.Clone();
            tuning.ScaleSearchRadius = tuning.TrackingScaleSearchRadius = 0d;
            tuning.TrackingSearchRadiusPixels = tuning.PreviousAlignmentSearchRadiusPixels;
            tuning.EnableFeatureVoting = false;
            tuning.EnforceTimeBudget = false;
            tuning.EnableFastAlignment = true;
            tuning.FastFallbackToLegacy = false;
            tuning.FastCoarseDownsampleFactor = 4;
            tuning.FastCoarseTopK = 3;
            tuning.EnableVisibleMask = true;
            tuning.VisibleAwareCorrelationMode = VisibleAwareCorrelationMode.CoarseMat;
            tuning.VisibleAwareCoarseDownsample = 4;
            tuning.VisibleAwareTopK = 3;
            tuning.EnableVisibleAwareShadow = false;
            tuning.EnableVisibleAwareInjection = true;
            MapStructureRegistrationRequest Request(bool restrict) => new()
            {
                ReferenceImage = referenceImage, LiveRoi = frame.Image,
                PreparedReference = reference, PreparedLive = live,
                ViewportBounds = viewport, LockedTransform = seed,
                Tuning = tuning, Channel = MapAlignmentChannel.LowStructure,
                ScaleSearchPolicy = MapScaleSearchPolicy.Fixed,
                RestrictSearchToLockedTransform = restrict, TrackingMode = restrict,
                FixedRotationDegrees = profile.OrientationDegrees,
                ValidMapBounds = profile.GetEffectiveValidMapBounds()
            };
            var result = registrar.Register(Request(true));
            var globalRecovery = !result.Accepted || result.Transform is null;
            if (globalRecovery)
            {
                MapOpenAlignmentRouteRules.ApplySteadyGlobalTranslationRecoveryPolicy(tuning);
                result = registrar.Register(Request(false));
            }
            var diagnostics = new MapScanDiagnostics { WarmStateHit = true };
            MapCvAlignmentService.PopulateStructureDiagnostics(diagnostics, result);
            diagnostics.LowStructureRoute = nameof(LowStructureAlignmentRoute.CachedFixed);
            var attempt = new MapRecognitionAttempt
            {
                StructureAccepted = result.Accepted, StructureResult = result,
                Diagnostics = diagnostics,
                Recognition = result.Transform is { } transform
                    ? MapCvRecognitionBuilders.BuildFloorStructureRecognition(map, "b1f",
                        referencePath, transform, result, 1d)
                    : null
            };
            var evidence = LowStructureScaleEvidenceRules.ObserveAlignment(attempt);
            var verdict = FloorAlignmentRecoveryRules.ClassifyAttemptResult(mapId, "b1f",
                MapAlignmentChannel.LowStructure, attempt, null, .80d,
                evidence.Accepted, evidence.Pending);
            var decision = FloorAlignmentRecoveryRules.DecideFromAttempts([verdict], false, "b1f");
            // The prior rule marked every fixed-scale success as pending,
            // regardless of the already confirmed same-floor warm seed.
            var previousPending = result.Accepted
                && !LowStructureScaleEvidenceRules.IsIndependentScaleRoute(diagnostics.LowStructureRoute);
            rows.Add(new
            {
                index, file = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                geometryAccepted = result.Accepted, result.Confidence, result.RejectionReason,
                scale = result.Transform?.ScaleX, tx = result.Transform?.OffsetX, ty = result.Transform?.OffsetY,
                globalRecovery, route = diagnostics.LowStructureRoute,
                previousPending, pending = evidence.Pending, scaleVotes = evidence.Count,
                outcome = verdict.Outcome.ToString(), winner = decision.Winner?.FloorKey
            });
            if (!previousPending || evidence.Count != 0 || decision.Winner?.FloorKey != "b1f")
                failures.Add($"{Path.GetFileName(path)}: {result.RejectionReason}, {result.FailureReason}, verdict={verdict.Outcome}");
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            sourceBuild = "b01.6-26.10.03.6036", mapId, floor = "b1f",
            referenceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(referencePath))),
            baseTuning.CacheFingerprint, samples = rows, failures
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    public sealed class B1fReplayFactAttribute : FactAttribute
    {
        public B1fReplayFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("IDVB_B1F_REPLAY") is null)
                Skip = "Requires matching build 6036 B1F frames, catalog and configuration.";
        }
    }
}
