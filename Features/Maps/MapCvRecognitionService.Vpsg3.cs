using System.Diagnostics.CodeAnalysis;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService : IDisposable
{
    private const int Vpsg3RebuildWorkerCount = 3;

    private readonly Vpsg3PreparedIndexRegistry _vpsg3Registry = new();
    private readonly CancellationTokenSource _vpsg3RebuildCts = new();
    private int _vpsg3ShadowRunning;

    /// <summary>
    /// Application-lifetime registry holding prepared reference floor indices for VPSG 3.0.
    /// </summary>
    public IVpsg3PreparedIndexRegistry Vpsg3Registry => _vpsg3Registry;

    /// <summary>
    /// 尝试使用 VPSG 3.0 进行极速结构对齐（尺度估计 + 平移搜索 + 亚像素精修 + 空间验证）。
    /// 当目标楼层具备有效的 PrebuiltStructureLine 且预构建索引就绪时，优先执行 VPSG 3.0。
    /// 成功时直接返回通过结构验证的对齐结果；失败或未就绪时返回 false，由调用方回退至传统流程。
    /// </summary>
    public bool TryAlignWithVpsg3(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        double identityPriorConfidence,
        [NotNullWhen(true)] out MapRecognitionAttempt? attempt,
        double? knownScaleSeed = null)
    {
        attempt = null;
        if (_disposed || MapAlignmentChannelRegistry.Resolve(map, floorKey).Channel == MapAlignmentChannel.LowStructure)
            return false;

        var floor = map.Floors.FirstOrDefault(f => string.Equals(f.Key, floorKey, StringComparison.OrdinalIgnoreCase));
        if (floor?.PrebuiltStructureLine is not { IsComplete: true } prebuilt
            || !string.Equals(prebuilt.SourceSha256, floor.RecognitionSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var structureGen = Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(prebuilt, schemaVersion: 1);
        var key = new Vpsg3IndexCacheKey(
            map.Id,
            floor.Key,
            MapFeatureCacheRules.ComputeContentFingerprint(map),
            map.UpdatedAt,
            structureGen,
            SchemaVersion: 1);

        if (!_vpsg3Registry.TryGet(key, out var lease))
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"VPSG 3.0 快速对齐跳过 · 索引未就绪 · map={map.SequenceNumber}#{floorKey}",
                details: new() { ["cacheKey"] = key.ToString() });
            return false;
        }

        using (lease)
        {
            using var observation = Vpsg3FastLiveExtractor.Extract(frame.Image, frame.ViewportBounds);
            var result = Vpsg3FastBootstrapSolver.TrySolve(observation, lease.Floor, knownScaleSeed: knownScaleSeed);

            if (MapDiagnosticModeCapture.IsActive)
            {
                try
                {
                    var refPath = _repository.GetPrebuiltStructureLinePath(map, floorKey);
                    Vpsg3DiagnosticCapture.CaptureIfActive(
                        MapDiagnosticModeCapture.CurrentMapOpenId,
                        observation,
                        refPath,
                        result,
                        tag: "vpsg3-live");
                }
                catch (Exception diagEx)
                {
                    MapLogCollector.Instance.Append(
                        MapLogCategory.StructureRegistration,
                        MapLogLevel.Warning,
                        "VPSG3 diagnostic capture failed",
                        details: new() { ["error"] = diagEx.Message });
                }
            }

            if (!result.IsAccepted)
            {
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    $"VPSG 3.0 快速对齐未接受 · map={map.SequenceNumber}#{floorKey} · reason={result.FallbackReason}",
                    elapsedMs: result.Timing.TotalMs,
                    details: new()
                    {
                        ["mapId"] = map.Id,
                        ["floor"] = floorKey,
                        ["reason"] = result.FallbackReason,
                        ["scale"] = result.Scale,
                        ["confidence"] = result.Confidence,
                        ["margin"] = result.ApertureMargin,
                        ["totalMs"] = result.Timing.TotalMs
                    });
                return false;
            }

            var transform = MapCanonicalTransformMath.BuildOverlayTransform(
                result.Scale,
                result.Scale,
                result.OffsetX,
                result.OffsetY,
                lease.Floor.ReferenceWidth,
                lease.Floor.ReferenceHeight,
                residualPixels: 0d,
                orientationDegrees: MapFloorRules.GetFloorProfile(map, floorKey)?.OrientationDegrees ?? 0,
                alignmentMode: MapOverlayAlignmentMode.Uniform);

            var structureResult = new MapStructureRegistrationResult
            {
                Accepted = true,
                Transform = transform,
                Confidence = result.Confidence,
                BestScore = result.BestCandidate.WeightedScore,
                SecondScore = result.RunnerUpCandidate?.WeightedScore ?? double.PositiveInfinity,
                CandidateMargin = result.ApertureMargin,
                RejectionReason = MapStructureRejectionReason.None,
                PreprocessMilliseconds = result.Timing.ExtractionMs,
                SearchMilliseconds = result.Timing.TranslationMs,
                RefineMilliseconds = result.Timing.RefineMs,
                UsedFastStrategy = true,
                LockedScale = result.Scale,
                ReferenceWidth = lease.Floor.ReferenceWidth,
                ReferenceHeight = lease.Floor.ReferenceHeight
            };

            var recognition = MapCvRecognitionBuilders.BuildFloorStructureRecognition(
                map,
                floorKey,
                _repository.GetFloorOverlayPath(map, floorKey),
                transform,
                structureResult,
                identityPriorConfidence);

            var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(ReadyMapCount, TotalMapCount);
            diagnostics.ScaleBootstrapAttempted = true;
            diagnostics.ScaleBootstrapSucceeded = true;
            diagnostics.ScaleBootstrapValidated = true;
            diagnostics.ScaleBootstrapScale = result.Scale;
            diagnostics.ScaleBootstrapConfidence = result.Confidence;
            diagnostics.ScaleBootstrapMode = "Vpsg3";
            diagnostics.ScaleBootstrapMethod = "vpsg3";
            diagnostics.ScaleBootstrapCost = result.BestCandidate.WeightedScore;
            diagnostics.ScaleBootstrapMargin = result.ApertureMargin;
            diagnostics.ScaleBootstrapCandidateCount = result.HasDistinctRunnerUp ? 2 : 1;
            diagnostics.ScaleBootstrapSelectedCandidateIndex = 0;
            diagnostics.ScaleBootstrapTestedScaleCount = 1;
            diagnostics.ScaleBootstrapStructureMilliseconds = result.Timing.ScaleMs;
            diagnostics.LiveStructurePreprocessMilliseconds = result.Timing.ExtractionMs;
            diagnostics.StructurePreprocessMilliseconds = result.Timing.ExtractionMs;
            diagnostics.StructureSearchMilliseconds = result.Timing.TranslationMs + result.Timing.RefineMs;
            diagnostics.StructureRefineMilliseconds = result.Timing.RefineMs;
            diagnostics.StructureBestScore = result.BestCandidate.WeightedScore;
            diagnostics.StructureSecondScore = result.RunnerUpCandidate?.WeightedScore ?? double.PositiveInfinity;
            diagnostics.StructureCandidateMargin = result.ApertureMargin;
            diagnostics.StructureCandidateCount = result.HasDistinctRunnerUp ? 2 : 1;
            diagnostics.AlignmentEvidence = MapAlignmentEvidenceKind.Structure;
            diagnostics.StructureAccepted = true;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureRejectionReason = MapStructureRejectionReason.None;
            diagnostics.TotalMilliseconds = result.Timing.TotalMs;
            diagnostics.TrackingMode = MapAlignmentTrackingMode.StructureMatched;

            attempt = new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structureResult,
                Recognition = recognition,
                StructureAttempted = true,
                StructureAccepted = true,
                SearchStage = AlignmentSearchStage.StructureFallback
            };

            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"VPSG 3.0 快速对齐通过 · floor={floorKey} · scale={result.Scale:F5} · elapsedMs={result.Timing.TotalMs:F1}",
                elapsedMs: result.Timing.TotalMs,
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["scale"] = result.Scale,
                    ["tx"] = result.OffsetX,
                    ["ty"] = result.OffsetY,
                    ["confidence"] = result.Confidence,
                    ["margin"] = result.ApertureMargin,
                    ["partitions"] = result.PassedPartitions,
                    ["extractionMs"] = result.Timing.ExtractionMs,
                    ["scaleMs"] = result.Timing.ScaleMs,
                    ["translationMs"] = result.Timing.TranslationMs,
                    ["refineMs"] = result.Timing.RefineMs,
                    ["verificationMs"] = result.Timing.VerificationMs,
                    ["totalMs"] = result.Timing.TotalMs
                });

            return true;
        }
    }

    // ponytail: one in-flight shadow, no queue; add a bounded queue only if dropped
    // observations prevent certification. Cloned pixels and a lease outlive the caller.
    internal Task QueueVpsg3Shadow(CapturedGameFrame frame, MapRecord map, string floorKey,
        MapOverlayTransform? baseline, MapLogCollector log, int? diagnosticAttemptId = null)
    {
        if (_disposed || MapAlignmentChannelRegistry.Resolve(map, floorKey).Channel == MapAlignmentChannel.LowStructure)
            return Task.CompletedTask;
        if (Interlocked.CompareExchange(ref _vpsg3ShadowRunning, 1, 0) != 0)
        {
            log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                "VPSG3 shadow skipped: busy", details: new() { ["mapId"] = map.Id, ["floorKey"] = floorKey });
            return Task.CompletedTask;
        }

        Vpsg3FloorIndexLease? lease = null;
        Mat? pixels = null;
        try
        {
            var floor = map.Floors.FirstOrDefault(f => string.Equals(f.Key, floorKey, StringComparison.OrdinalIgnoreCase));
            if (floor?.PrebuiltStructureLine is not { IsComplete: true } prebuilt
                || !string.Equals(prebuilt.SourceSha256, floor.RecognitionSha256, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref _vpsg3ShadowRunning, 0);
                log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                    "VPSG3 shadow skipped: prebuilt unavailable", details: new() { ["mapId"] = map.Id, ["floorKey"] = floorKey });
                return Task.CompletedTask;
            }
            var key = new Vpsg3IndexCacheKey(map.Id, floor.Key,
                MapFeatureCacheRules.ComputeContentFingerprint(map), map.UpdatedAt,
                Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(prebuilt));
            if (!_vpsg3Registry.TryGet(key, out lease))
            {
                Interlocked.Exchange(ref _vpsg3ShadowRunning, 0);
                log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                    "VPSG3 shadow skipped: index not ready", details: new() { ["cacheKey"] = key.ToString() });
                return Task.CompletedTask;
            }
            pixels = frame.Image.Clone();
            var ownedPixels = pixels;
            var ownedLease = lease;
            var bounds = frame.ViewportBounds;
            var capturedAt = DateTimeOffset.UtcNow;
            var referenceSha256 = prebuilt.Sha256;
            return Task.Run(() =>
            {
                using (ownedPixels)
                using (ownedLease)
                {
                    try
                    {
                        if (_disposed) return;
                        using var observation = Vpsg3FastLiveExtractor.Extract(ownedPixels, bounds);
                        var result = Vpsg3FastBootstrapSolver.TrySolve(observation, ownedLease.Floor);
                        if (MapDiagnosticModeCapture.IsActive)
                        {
                            try
                            {
                                var refPath = _repository.GetPrebuiltStructureLinePath(map, floorKey);
                                Vpsg3DiagnosticCapture.CaptureIfActive(
                                    diagnosticAttemptId,
                                    observation,
                                    refPath,
                                    result,
                                    tag: "vpsg3");
                            }
                            catch (Exception diagEx)
                            {
                                log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                                    "VPSG3 diagnostic capture failed", details: new() { ["error"] = diagEx.Message });
                            }
                        }
                        string? evidencePath = null;
                        string? evidenceError = null;
                        if (File.Exists(Path.Combine(log.LogDirectory, Vpsg3CertificationCapture.EnableFileName)))
                        {
                            try
                            {
                                evidencePath = Vpsg3CertificationCapture.Save(log.LogDirectory, ownedPixels,
                                    _repository.GetPrebuiltStructureLinePath(map, floorKey), referenceSha256,
                                    key, bounds, capturedAt, result, baseline);
                            }
                            catch (Exception ex)
                            {
                                evidenceError = ex.Message;
                                log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                                    "VPSG3 certification capture failed", details: new() { ["cacheKey"] = key.ToString(), ["error"] = evidenceError });
                            }
                        }
                        log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                            "VPSG3 shadow result", elapsedMs: result.Timing.TotalMs, details: new()
                            {
                                ["shadowOnly"] = true, ["capturedAt"] = capturedAt,
                                ["fastAcceptLocked"] = true, ["certificationEvidencePath"] = evidencePath,
                                ["certificationEvidenceError"] = evidenceError,
                                ["cacheKey"] = key.ToString(), ["mapId"] = key.MapId, ["floorKey"] = key.FloorKey,
                                ["viewportX"] = bounds.X, ["viewportY"] = bounds.Y,
                                ["viewportWidth"] = bounds.Width, ["viewportHeight"] = bounds.Height,
                                ["accepted"] = result.IsAccepted, ["reason"] = result.FallbackReason,
                                ["scale"] = result.Scale, ["tx"] = result.OffsetX, ["ty"] = result.OffsetY,
                                ["confidence"] = result.Confidence, ["margin"] = result.ApertureMargin,
                                ["hasDistinctRunnerUp"] = result.HasDistinctRunnerUp,
                                ["partitions"] = result.PassedPartitions,
                                ["baselineScale"] = baseline?.ScaleX, ["baselineTx"] = baseline?.OffsetX,
                                ["baselineTy"] = baseline?.OffsetY, ["baselineIsGroundTruth"] = false,
                                ["extractionMs"] = result.Timing.ExtractionMs, ["scaleMs"] = result.Timing.ScaleMs,
                                ["translationMs"] = result.Timing.TranslationMs, ["refineMs"] = result.Timing.RefineMs,
                                ["verificationMs"] = result.Timing.VerificationMs, ["gateMs"] = result.Timing.GateMs
                            });
                    }
                    catch (Exception ex)
                    {
                        log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                            "VPSG3 shadow failed", details: new() { ["cacheKey"] = key.ToString(), ["error"] = ex.Message });
                    }
                    finally { Interlocked.Exchange(ref _vpsg3ShadowRunning, 0); }
                }
            });
        }
        catch (Exception ex)
        {
            pixels?.Dispose();
            lease?.Dispose();
            Interlocked.Exchange(ref _vpsg3ShadowRunning, 0);
            log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                "VPSG3 shadow scheduling failed", details: new() { ["mapId"] = map.Id, ["floorKey"] = floorKey, ["error"] = ex.Message });
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Synchronously invalidates changed maps in the VPSG3 registry, then triggers
    /// background asynchronous rebuilding for the invalidated floors using bounded parallelism (3 workers).
    /// Alignment never waits for rebuild to complete.
    /// Only floors with complete, valid PrebuiltStructureLine are eligible for VPSG3 index building.
    /// </summary>
    internal void InvalidateAndTriggerVpsg3Rebuild(
        IReadOnlyList<MapRecord> maps,
        IReadOnlySet<Guid> changedMapIds)
    {
        if (changedMapIds is null || changedMapIds.Count == 0)
            return;

        // 1. Synchronous invalidation in the registry
        _vpsg3Registry.InvalidateMaps(changedMapIds);

        // 2. Asynchronous background rebuild without blocking caller or alignment
        if (_disposed || _vpsg3RebuildCts.IsCancellationRequested)
            return;

        var token = _vpsg3RebuildCts.Token;
        var targets = maps.Where(m => changedMapIds.Contains(m.Id)).ToArray();
        if (targets.Length == 0)
            return;

        // Collect all eligible prebuilt tasks
        var buildTasks = new List<(MapRecord Map, FloorDefinition Floor, string LinePath, Vpsg3IndexCacheKey CacheKey)>();
        foreach (var map in targets)
        {
            var fingerprint = MapFeatureCacheRules.ComputeContentFingerprint(map);

            foreach (var floor in map.Floors)
            {
                // Strict PrebuiltStructureLine contract: ineligible floors never get a VPSG3 index
                if (!TryGetEligiblePrebuiltPath(map, floor, out var linePath) || linePath is null)
                    continue;

                var structureGen = Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(
                    floor.PrebuiltStructureLine!,
                    schemaVersion: 1);

                var cacheKey = new Vpsg3IndexCacheKey(
                    map.Id,
                    floor.Key,
                    fingerprint,
                    map.UpdatedAt,
                    structureGen,
                    SchemaVersion: 1);

                buildTasks.Add((map, floor, linePath, cacheKey));
            }
        }

        if (buildTasks.Count == 0)
            return;

        // Dispatch background bounded parallel execution (3 workers)
        _ = Task.Run(async () =>
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Vpsg3RebuildWorkerCount,
                CancellationToken = token
            };

            try
            {
                await Parallel.ForEachAsync(buildTasks, options, (taskItem, ct) =>
                {
                    if (ct.IsCancellationRequested)
                        return ValueTask.CompletedTask;

                    // Prevent duplicate parallel builds for the same key
                    if (!_vpsg3Registry.TryBeginBuild(taskItem.CacheKey))
                        return ValueTask.CompletedTask;

                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        using var image = Cv2.ImRead(taskItem.LinePath, ImreadModes.Grayscale);
                        if (image.Empty())
                        {
                            _vpsg3Registry.RecordBuildFailure(taskItem.CacheKey, "Decoded prebuilt line image is empty.");
                            return ValueTask.CompletedTask;
                        }

                        var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(image, taskItem.CacheKey);
                        _vpsg3Registry.TryPublishFloor(taskItem.CacheKey, preparedFloor);
                    }
                    catch (OperationCanceledException)
                    {
                        // Background task cancelled
                    }
                    catch (Exception ex)
                    {
                        _vpsg3Registry.RecordBuildFailure(taskItem.CacheKey, ex.Message);
                    }

                    return ValueTask.CompletedTask;
                });
            }
            catch (OperationCanceledException)
            {
                // Service being disposed or rebuild cancelled
            }
        }, token);
    }

    private bool TryGetEligiblePrebuiltPath(
        MapRecord map,
        FloorDefinition floor,
        out string? prebuiltPath)
    {
        prebuiltPath = null;
        if (floor.PrebuiltStructureLine?.IsComplete is not true)
            return false;

        if (!string.Equals(
                floor.PrebuiltStructureLine.SourceSha256,
                floor.RecognitionSha256,
                StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var path = _repository.GetPrebuiltStructureLinePath(map, floor.Key);
            if (File.Exists(path))
            {
                prebuiltPath = path;
                return true;
            }
        }
        catch
        {
            // Corrupted or missing prebuilt line file
        }

        return false;
    }

    private void DisposeVpsg3()
    {
        try
        {
            _vpsg3RebuildCts.Cancel();
            _vpsg3RebuildCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _vpsg3Registry.Dispose();
    }
}
