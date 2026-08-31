using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapCvRecognitionHelpers
{
    internal static MapRecognitionTuning NormalizedCopy(MapRecognitionTuning tuning)
    {
        var copy = tuning?.Clone() ?? new MapRecognitionTuning();
        copy.Normalize();
        return copy;
    }

    internal static double GeometryMargin(IReadOnlyList<MapGeometryCandidate> ranked) =>
        ranked.Count > 1
            ? ranked[1].VectorError - ranked[0].VectorError
            : double.PositiveInfinity;

    internal static double ConfirmCandidate(
        MapGeometryCandidate candidate,
        Mat liveEdges,
        MapScreenRect viewportBounds)
    {
        using var reference = Cv2.ImRead(
            candidate.Fingerprint.RecognitionImagePath,
            ImreadModes.Unchanged);
        if (reference.Empty())
            return 0d;

        using var referenceEdges = GateTemplateDetector.CreateEdges(reference);
        var fingerprint = candidate.Fingerprint;
        var referenceMain = new Point2d(
            fingerprint.MainPoint.X * referenceEdges.Width,
            fingerprint.MainPoint.Y * referenceEdges.Height);
        var referenceSide = new Point2d(
            fingerprint.SidePoint.X * referenceEdges.Width,
            fingerprint.SidePoint.Y * referenceEdges.Height);
        var liveMain = new Point2d(
            candidate.MainGate.ScreenBounds.CenterX - viewportBounds.X,
            candidate.MainGate.ScreenBounds.CenterY - viewportBounds.Y);
        var liveSide = new Point2d(
            candidate.SideGate.ScreenBounds.CenterX - viewportBounds.X,
            candidate.SideGate.ScreenBounds.CenterY - viewportBounds.Y);
        var referenceDistance = Distance(referenceMain, referenceSide);
        var liveDistance = Distance(liveMain, liveSide);
        if (referenceDistance <= 1d || liveDistance <= 1d)
            return 0d;

        var scale = liveDistance / referenceDistance;
        var patchSize = (int)Math.Clamp(
            ((candidate.MainGate.ScreenBounds.Width + candidate.SideGate.ScreenBounds.Width) / 2d) * 3d,
            96d,
            240d);
        var referencePatchSize = Math.Max(16, (int)Math.Round(patchSize / scale));
        var referenceCenter = new Point2d(
            (referenceMain.X + referenceSide.X) / 2d,
            (referenceMain.Y + referenceSide.Y) / 2d);
        var liveCenter = new Point2d(
            (liveMain.X + liveSide.X) / 2d,
            (liveMain.Y + liveSide.Y) / 2d);
        var referenceCenters = new List<Point2d>
        {
            referenceMain,
            referenceSide,
            referenceCenter
        };
        var liveCenters = new List<Point2d>
        {
            liveMain,
            liveSide,
            liveCenter
        };
        var scaleX = AxisScale(
            referenceSide.X - referenceMain.X,
            liveSide.X - liveMain.X,
            scale);
        var scaleY = AxisScale(
            referenceSide.Y - referenceMain.Y,
            liveSide.Y - liveMain.Y,
            scale);
        foreach (var anchor in (MapFloorRules.GetFloorProfile(
                     candidate.Fingerprint.Map,
                     candidate.Fingerprint.FloorKey)
                 ?? candidate.Fingerprint.Map.Recognition.FirstFloor).Anchors
                     .Where(anchor =>
                         anchor.Role == RecognitionAnchorRole.Optional
                         && anchor.Bounds?.IsValid is true)
                     .Take(3))
        {
            var bounds = anchor.Bounds!;
            var anchorReferenceCenter = new Point2d(
                (bounds.X + (bounds.Width / 2d)) * referenceEdges.Width,
                (bounds.Y + (bounds.Height / 2d)) * referenceEdges.Height);
            referenceCenters.Add(anchorReferenceCenter);
            liveCenters.Add(new Point2d(
                liveCenter.X + ((anchorReferenceCenter.X - referenceCenter.X) * scaleX),
                liveCenter.Y + ((anchorReferenceCenter.Y - referenceCenter.Y) * scaleY)));
        }

        var scores = new List<double>();
        for (var index = 0; index < referenceCenters.Count; index++)
        {
            if (!TryExtractCenteredPatch(
                    referenceEdges,
                    referenceCenters[index],
                    referencePatchSize,
                    out var referencePatch)
                || !TryExtractCenteredPatch(
                    liveEdges,
                    liveCenters[index],
                    patchSize,
                    out var livePatch))
            {
                continue;
            }

            using (referencePatch)
            using (livePatch)
            using (var resized = new Mat())
            {
                Cv2.Resize(
                    referencePatch,
                    resized,
                    livePatch.Size(),
                    0d,
                    0d,
                    InterpolationFlags.Area);
                scores.Add(CosineSimilarity(resized, livePatch));
            }
        }

        return scores.Count == 0 ? 0d : scores.Average();
    }

    internal static CvAnchorEvidence CreateEvidence(
        RecognitionAnchor anchor,
        GateDetection gate,
        MapGeometryFingerprint fingerprint)
    {
        var bounds = anchor.Bounds!;
        return new CvAnchorEvidence
        {
            AnchorId = anchor.Id,
            Score = gate.Score,
            TemplateScale = gate.Scale,
            ReferenceBounds = new MapScreenRect(
                bounds.X * fingerprint.ReferenceWidth,
                bounds.Y * fingerprint.ReferenceHeight,
                bounds.Width * fingerprint.ReferenceWidth,
                bounds.Height * fingerprint.ReferenceHeight),
            ScreenBounds = gate.ScreenBounds
        };
    }

    internal static MapNormalizedPoint Center(NormalizedRectangle bounds) =>
        new(bounds.X + (bounds.Width / 2d), bounds.Y + (bounds.Height / 2d));

    internal static MapScreenRect ToPixelBounds(
        NormalizedRectangle bounds,
        int width,
        int height) =>
        new(
            bounds.X * width,
            bounds.Y * height,
            bounds.Width * width,
            bounds.Height * height);

    internal static bool TryExtractCenteredPatch(
        Mat image,
        Point2d center,
        int size,
        out Mat patch)
    {
        patch = new Mat();
        var half = size / 2;
        var left = Math.Max(0, (int)Math.Round(center.X) - half);
        var top = Math.Max(0, (int)Math.Round(center.Y) - half);
        var right = Math.Min(image.Width, left + size);
        var bottom = Math.Min(image.Height, top + size);
        left = Math.Max(0, right - size);
        top = Math.Max(0, bottom - size);
        if (right - left < 12 || bottom - top < 12)
            return false;
        patch = new Mat(image, new Rect(left, top, right - left, bottom - top)).Clone();
        return true;
    }

    internal static double CosineSimilarity(Mat left, Mat right)
    {
        using var leftFloat = new Mat();
        using var rightFloat = new Mat();
        left.ConvertTo(leftFloat, MatType.CV_32FC1);
        right.ConvertTo(rightFloat, MatType.CV_32FC1);
        var denominator = Cv2.Norm(leftFloat) * Cv2.Norm(rightFloat);
        return denominator <= 0.000001d
            ? 0d
            : Math.Clamp(leftFloat.Dot(rightFloat) / denominator, 0d, 1d);
    }

    internal static double Distance(Point2d left, Point2d right)
    {
        var deltaX = right.X - left.X;
        var deltaY = right.Y - left.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    internal static double AxisScale(
        double referenceDelta,
        double liveDelta,
        double fallbackScale)
    {
        if (Math.Abs(referenceDelta) > 4d)
        {
            var solved = liveDelta / referenceDelta;
            if (double.IsFinite(solved) && solved > 0d)
                return solved;
        }
        return fallbackScale;
    }

    /// <summary>
    /// Resolves the <c>Gate.png</c> path: deployed &gt; workspace &gt; current-directory fallback.
    /// </summary>
    internal static string ResolveGatePath()
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        if (File.Exists(deployed))
            return deployed;
        var workspace = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Gate.png"));
        if (File.Exists(workspace))
            return workspace;
        var current = Path.Combine(Environment.CurrentDirectory, "Assets", "Gate.png");
        return File.Exists(current) ? current : deployed;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="left"/> and
    /// <paramref name="right"/> share the same inputs that feed
    /// <see cref="MapGeometryFingerprint"/> construction.
    /// </summary>
    internal static bool HaveSameFingerprintInputs(MapRecord left, MapRecord right)
    {
        if (left.Id != right.Id
            || left.UpdatedAt != right.UpdatedAt
            || left.Recognition.SchemaVersion != right.Recognition.SchemaVersion
            || !string.Equals(
                MapScanFloorRules.NormalizeFloorIdentity(left.ClassProperties?.ScanFloorKey),
                MapScanFloorRules.NormalizeFloorIdentity(right.ClassProperties?.ScanFloorKey),
                StringComparison.Ordinal))
            return false;

        var leftFloors = MapFloorRules.GetOrderedFloors(left);
        var rightFloors = MapFloorRules.GetOrderedFloors(right);
        if (leftFloors.Count != rightFloors.Count)
            return false;

        for (var index = 0; index < leftFloors.Count; index++)
        {
            var a = leftFloors[index];
            var b = rightFloors[index];
            if (!string.Equals(a.Key, b.Key, StringComparison.Ordinal)
                || !string.Equals(a.ImageSha256, b.ImageSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.RecognitionSha256, b.RecognitionSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.OverlaySha256, b.OverlaySha256, StringComparison.OrdinalIgnoreCase)
                || a.ImageFileLength != b.ImageFileLength
                || a.ImageLastWriteUtcTicks != b.ImageLastWriteUtcTicks
                || a.RecognitionFileLength != b.RecognitionFileLength
                || a.RecognitionLastWriteUtcTicks != b.RecognitionLastWriteUtcTicks
                || a.OverlayFileLength != b.OverlayFileLength
                || a.OverlayLastWriteUtcTicks != b.OverlayLastWriteUtcTicks)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Populates gate-detection diagnostics and computes the dynamic ignore
    /// regions from detected gate screen bounds.
    /// </summary>
    internal static void PopulateGateDiagnosticsAndIgnoreRegions(
        MapScanDiagnostics diagnostics,
        GateDetectionResult gateResult,
        IReadOnlyList<GateDetection> gates,
        CapturedGameFrame frame,
        out List<Rect> dynamicIgnoreRegions)
    {
        diagnostics.GateCandidateCount = gates.Count;
        diagnostics.GateSearchMode = gateResult.SearchModeUsed;
        diagnostics.GateSearchStopReason = gateResult.StopReason;
        diagnostics.GateScalesEvaluated = gateResult.ScalesEvaluated;
        diagnostics.GateMatchTemplateCalls = gateResult.MatchTemplateCalls;
        diagnostics.GateBudgetExceeded = gateResult.BudgetExceeded;
        MapLogCollector.Instance.Append(MapLogCategory.GateDetection, MapLogLevel.Info,
            $"门检测完成 · {gates.Count} 个候选 · 模式 {gateResult.SearchModeUsed} · 原因 {gateResult.StopReason}",
            elapsedMs: diagnostics.GateDetectionMilliseconds,
            details: new()
            {
                ["gateCount"] = gates.Count,
                ["mode"] = gateResult.SearchModeUsed.ToString(),
                ["stopReason"] = gateResult.StopReason.ToString(),
                ["scalesEvaluated"] = gateResult.ScalesEvaluated,
                ["matchTemplateCalls"] = gateResult.MatchTemplateCalls,
            });

        dynamicIgnoreRegions = gates
            .Select(gate => MapCvRecognitionBuilders.ToLocalRect(
                gate.ScreenBounds,
                frame.ViewportBounds,
                frame.Image.Size()))
            .Where(region => region.Width > 0 && region.Height > 0)
            .ToList();
    }

    /// <summary>
    /// Builds the side-entrance feature cache: loads valid feature images for
    /// every registered map floor as grayscale <see cref="Mat"/> objects.
    /// </summary>
    internal static Dictionary<(Guid, string), Mat> BuildSideEntranceFeatureCache(
        MapRepository repository,
        IReadOnlyList<MapRecord> maps)
    {
        var cache = new Dictionary<(Guid, string), Mat>();
        foreach (var map in maps)
        {
            foreach (var floorDef in MapFloorRules.GetOrderedFloors(map))
            {
                var profile = MapFloorRules.GetFloorProfile(map, floorDef.Key);
                if (profile is null
                    || !repository.TryGetValidSideEntranceFeaturePath(
                        map,
                        floorDef.Key,
                        out var path,
                        out _))
                    continue;

                try
                {
                    var mat = Cv2.ImRead(path, ImreadModes.Grayscale);
                    if (mat.Empty())
                    {
                        mat.Dispose();
                        continue;
                    }
                    cache[(map.Id, floorDef.Key)] = mat;
                }
                catch
                {
                    // 单张地图加载失败不影响其他地图
                }
            }
        }
        return cache;
    }
}
/*
 * 文件职责：MapCvRecognitionHelpers。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
