using System.Diagnostics.CodeAnalysis;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    /// <summary>
    /// 尝试使用 VPSG 3.0 进行极速结构对齐（向后兼容重载）。
    /// </summary>
    public bool TryAlignWithVpsg3(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        double identityPriorConfidence,
        [NotNullWhen(true)] out MapRecognitionAttempt? attempt,
        double? knownScaleSeed = null) =>
        TryAlignWithVpsg3(
            frame,
            map,
            floorKey,
            identityPriorConfidence,
            out attempt,
            out _,
            knownScaleSeed);

    /// <summary>
    /// 尝试使用 VPSG 3.0 进行极速结构对齐（尺度估计 + 平移搜索 + 亚像素精修 + 空间验证）。
    /// 当目标楼层具备有效的 PrebuiltStructureLine 且预构建索引就绪时，优先执行 VPSG 3.0。
    /// 成功时直接返回通过结构验证的对齐结果；失败或未就绪时返回 false，由调用方回退至传统流程，并通过 status 完整携带死因因果链。
    /// </summary>
    public bool TryAlignWithVpsg3(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        double identityPriorConfidence,
        [NotNullWhen(true)] out MapRecognitionAttempt? attempt,
        out IdvbStatus status,
        double? knownScaleSeed = null)
    {
        attempt = null;
        if (_disposed)
        {
            status = IdvbStatus.ClientError(
                IdvbHttpCode.ServiceUnavailable,
                IdvbSubCode.StructureCvException,
                "ServiceDisposed",
                "识别服务已释放",
                stage: "Vpsg3.Precheck");
            return false;
        }

        if (MapAlignmentChannelRegistry.Resolve(map, floorKey).Channel == MapAlignmentChannel.LowStructure)
        {
            status = IdvbStatus.ClientError(
                IdvbHttpCode.FeatureDisabled,
                IdvbSubCode.Vpsg3FallbackDegraded,
                "Vpsg3LowStructureUnsupported",
                "目标楼层为低结构通道，VPSG 3.0 不适用",
                stage: "Vpsg3.ChannelCheck");
            return false;
        }

        if (!TryGetVpsg3IndexKey(map, floorKey, out var key))
        {
            status = IdvbStatus.ClientError(
                IdvbHttpCode.MapNotFound,
                IdvbSubCode.FloorKeyNotMatched,
                "Vpsg3KeyNotResolved",
                $"无法解析 VPSG 3.0 缓存键 · map={map.SequenceNumber}#{floorKey}",
                stage: "Vpsg3.KeyResolution");
            return false;
        }

        if (!_vpsg3Registry.TryGet(key, out var lease))
        {
            _vpsg3Registry.TryGetDetailedStatus(
                key,
                out var indexStatus,
                out var statusAge,
                out var failureReason,
                out var actualKey);

            var keyMismatch = actualKey.HasValue && actualKey.Value != key;
            var detailReason = indexStatus switch
            {
                Vpsg3IndexStatus.Missing => "索引未构建(未进入预构建队列或prebuilt文件不合规)",
                Vpsg3IndexStatus.Building => $"索引正在后台构建中 (已耗时 {statusAge.TotalMilliseconds:F0}ms)",
                Vpsg3IndexStatus.Failed => $"索引后台构建失败: {failureReason ?? "未知错误"}",
                Vpsg3IndexStatus.Stale => "索引已失效(版本/时间戳变更)",
                _ when keyMismatch => "缓存键不匹配",
                _ => "索引未就绪"
            };

            status = IdvbStatus.Fallback(
                IdvbHttpCode.Vpsg3FallbackToLegacy,
                IdvbSubCode.Vpsg3FallbackIndexNotReady,
                "Vpsg3IndexNotReady",
                $"VPSG 3.0 快速对齐跳过 · 索引未就绪({indexStatus}: {detailReason}) · map={map.SequenceNumber}#{floorKey}",
                stage: "Vpsg3.RegistryCheck");
            MapLogCollector.Instance.AppendStatus(
                status,
                MapLogCategory.StructureRegistration,
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floorKey"] = floorKey,
                    ["indexStatus"] = indexStatus.ToString(),
                    ["statusAgeMs"] = statusAge.TotalMilliseconds,
                    ["failureReason"] = failureReason ?? string.Empty,
                    ["detailReason"] = detailReason,
                    ["expectedKey"] = key.ToString(),
                    ["actualKey"] = actualKey?.ToString() ?? string.Empty,
                    ["keyMatch"] = !keyMismatch
                });
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
                var subCode = result.FallbackReason?.Contains("ApertureMargin", StringComparison.OrdinalIgnoreCase) == true
                    ? IdvbSubCode.Vpsg3FallbackApertureMarginLow
                    : IdvbSubCode.Vpsg3FallbackNotConverged;
                status = IdvbStatus.Fallback(
                    IdvbHttpCode.Vpsg3FallbackToLegacy,
                    subCode,
                    "Vpsg3SolverRejected",
                    $"VPSG 3.0 快速对齐未接受 · {result.FallbackReason}",
                    technicalDetail: $"map={map.SequenceNumber}#{floorKey}, scale={result.Scale:F5}, margin={result.ApertureMargin:F4}, conf={result.Confidence:F3}",
                    stage: "Vpsg3.FastBootstrapSolver");

                MapLogCollector.Instance.AppendStatus(
                    status,
                    MapLogCategory.StructureRegistration,
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

            var finalScale = result.Scale;
            var finalX = result.OffsetX;
            var finalY = result.OffsetY;
            var refineTime = result.Timing.RefineMs;

            if (lease.Floor.PrecisionDistance.IsEmpty)
            {
                var floorDef = map.Floors.FirstOrDefault(f => string.Equals(f.Key, floorKey, StringComparison.OrdinalIgnoreCase));
                if (floorDef is not null && TryGetEligiblePrebuiltPath(map, floorDef, out var prebuiltPath) && prebuiltPath is not null)
                {
                    lease.Floor.EnsurePrecisionDistance(prebuiltPath);
                }
            }

            Vpsg3PrecisionResult? precision = null;
            if (!lease.Floor.PrecisionDistance.IsEmpty)
            {
                TrackActivePrecisionFloor(lease.Floor);
                var precisionBudget = Vpsg3PrecisionBudget.Start();
                var lockScale = knownScaleSeed is { } s && s > 0;
                precision = Vpsg3PrecisionRefiner.Refine(
                    observation, lease.Floor, result.Scale, result.OffsetX, result.OffsetY, precisionBudget, lockScale: lockScale);
                refineTime += precision.Milliseconds;
                if (precision.Calibrated)
                {
                    finalScale = precision.Scale;
                    finalX = precision.OffsetX;
                    finalY = precision.OffsetY;
                }
            }

            var transform = MapCanonicalTransformMath.BuildOverlayTransform(
                finalScale,
                finalScale,
                finalX,
                finalY,
                lease.Floor.ReferenceWidth,
                lease.Floor.ReferenceHeight,
                residualPixels: 0d,
                orientationDegrees: MapFloorRules.GetFloorProfile(map, floorKey)?.OrientationDegrees ?? 0,
                alignmentMode: MapOverlayAlignmentMode.Uniform);

            var chamferEstimate = precision?.After?.Loss
                ?? precision?.Before?.Loss
                ?? (2.0d * (1.0d - Math.Clamp(result.BestCandidate.K3Score, 0d, 1d)) + 1.0d);

            var coverage = (double)result.BestCandidate.Spatial.HitPoints / Math.Max(1, result.BestCandidate.Spatial.TotalValidPoints);
            var breakdown = new MapStructureConfidenceBreakdown
            {
                ChamferPixels = chamferEstimate,
                ChamferQuality = Math.Clamp(1.0d - (chamferEstimate / 3.0d), 0d, 1d),
                EdgeCoverage = coverage,
                OccupancyCoverage = result.BestCandidate.K5Score,
                ReferenceCoverage = result.Confidence,
                ProjectionCorrelation = result.BestCandidate.WeightedScore,
                ConsistentPartitions = result.PassedPartitions,
                PartitionQuality = Math.Clamp(result.PassedPartitions / 4.0d, 0d, 1d),
                StructureQuality = result.Confidence,
                CandidateSeparation = result.ApertureMargin,
                GeometricFitQuality = result.Confidence,
                EvidenceConfidence = result.Confidence,
                GeometricLockConfidence = result.Confidence,
                LockConfidence = result.Confidence,
                EffectiveWeight = 1.0d,
                FinalScore = result.BestCandidate.WeightedScore
            };

            var mainCandidate = new MapStructureCandidate
            {
                Scale = finalScale,
                OffsetX = finalX,
                OffsetY = finalY,
                ChamferPixels = chamferEstimate,
                EdgeCoverage = coverage,
                OccupancyCoverage = result.BestCandidate.K5Score,
                ConsistentPartitions = result.PassedPartitions,
                CompositeCost = result.BestCandidate.WeightedScore,
                AppearanceCorrelation = result.Confidence
            };

            var structureResult = new MapStructureRegistrationResult
            {
                Accepted = true,
                Transform = transform,
                Confidence = result.Confidence,
                ConfidenceBreakdown = breakdown,
                Candidates = [mainCandidate],
                BestScore = result.BestCandidate.WeightedScore,
                SecondScore = result.RunnerUpCandidate?.WeightedScore ?? double.PositiveInfinity,
                CandidateMargin = result.ApertureMargin,
                RejectionReason = MapStructureRejectionReason.None,
                PreprocessMilliseconds = result.Timing.ExtractionMs,
                SearchMilliseconds = result.Timing.TranslationMs,
                RefineMilliseconds = refineTime,
                UsedFastStrategy = true,
                LockedScale = finalScale,
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

            var totalTime = result.Timing.TotalMs + (refineTime - result.Timing.RefineMs);
            var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(ReadyMapCount, TotalMapCount);
            diagnostics.ScaleBootstrapAttempted = true;
            diagnostics.ScaleBootstrapSucceeded = true;
            diagnostics.ScaleBootstrapValidated = true;
            diagnostics.ScaleBootstrapScale = finalScale;
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
            diagnostics.StructureSearchMilliseconds = result.Timing.TranslationMs + refineTime;
            diagnostics.StructureRefineMilliseconds = refineTime;
            diagnostics.StructureBestScore = result.BestCandidate.WeightedScore;
            diagnostics.StructureSecondScore = result.RunnerUpCandidate?.WeightedScore ?? double.PositiveInfinity;
            diagnostics.StructureCandidateMargin = result.ApertureMargin;
            diagnostics.StructureCandidateCount = result.HasDistinctRunnerUp ? 2 : 1;
            diagnostics.AlignmentEvidence = MapAlignmentEvidenceKind.Structure;
            diagnostics.StructureAccepted = true;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureRejectionReason = MapStructureRejectionReason.None;
            diagnostics.TotalMilliseconds = totalTime;
            diagnostics.TrackingMode = MapAlignmentTrackingMode.StructureMatched;

            status = IdvbStatus.Ok(
                $"VPSG 3.0 快速对齐通过 · floor={floorKey} · scale={finalScale:F5}",
                subCode: IdvbSubCode.StructureVpsg3Ok,
                stage: "Vpsg3.Aligned");

            attempt = new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structureResult,
                Recognition = recognition,
                StructureAttempted = true,
                StructureAccepted = true,
                Status = status,
                SearchStage = AlignmentSearchStage.StructureFallback
            };

            MapLogCollector.Instance.AppendStatus(
                status,
                MapLogCategory.StructureRegistration,
                elapsedMs: totalTime,
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["scale"] = finalScale,
                    ["tx"] = finalX,
                    ["ty"] = finalY,
                    ["confidence"] = result.Confidence,
                    ["margin"] = result.ApertureMargin,
                    ["partitions"] = result.PassedPartitions,
                    ["extractionMs"] = result.Timing.ExtractionMs,
                    ["scaleMs"] = result.Timing.ScaleMs,
                    ["translationMs"] = result.Timing.TranslationMs,
                    ["refineMs"] = refineTime,
                    ["verificationMs"] = result.Timing.VerificationMs,
                    ["totalMs"] = totalTime
                });

            return true;
        }
    }
}
