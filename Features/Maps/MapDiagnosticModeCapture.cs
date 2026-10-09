using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapDiagnosticModeCapture
{
    private static readonly object Gate = new();
    private static int _currentMapOpenId;
    private static readonly AsyncLocal<int> SuppressionDepth = new();
    private static string? _matchDirectory;
    private static object? _matchIdentity;
    private static IDisposable? _cacheProtection;
    private static int _attemptId;

    internal static string RootDirectory => Path.Combine(
#if IDVB_UNIT_TEST
        Path.GetTempPath(), $"IDVB-UnitTests-{Environment.ProcessId}",
#else
        global::IDVBuff.AppDataPaths.RootDirectory,
#endif
        "诊断模式");

    internal static bool IsActive
    {
        get { lock (Gate) return _matchDirectory is not null; }
    }

    internal static string? ActiveMatchDirectory
    {
        get { lock (Gate) return _matchDirectory; }
    }

    internal static void BeginMatch()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(RootDirectory);
            var matchId = Directory.EnumerateDirectories(RootDirectory, "对局 *")
                .Select(path => Path.GetFileName(path)["对局 ".Length..])
                .Select(value => int.TryParse(value, out var id) ? id : 0)
                .DefaultIfEmpty()
                .Max() + 1;
            _matchDirectory = Path.Combine(RootDirectory, $"对局 {matchId}");
            _matchIdentity = new object();
            _cacheProtection?.Dispose();
            _cacheProtection = AppDataPaths.ProtectCachePath(_matchDirectory);
            foreach (var category in new[] { "结构配准", "显示区域", "贴合度" })
                Directory.CreateDirectory(Path.Combine(_matchDirectory, category));
            _attemptId = 0;
            _currentMapOpenId = 0;
        }
    }

    internal static void EndMatch()
    {
        lock (Gate)
        {
            _matchDirectory = null;
            _matchIdentity = null;
            _cacheProtection?.Dispose();
            _cacheProtection = null;
            _currentMapOpenId = 0;
        }
    }

    internal static void Clear()
    {
        EndMatch();
        if (Directory.Exists(RootDirectory))
            Directory.Delete(RootDirectory, recursive: true);
    }

    internal static int CurrentMapOpenId
    {
        get
        {
            if (DeferredFitness.Value is { } capture)
                return capture.MapOpenId;
            lock (Gate) return _currentMapOpenId;
        }
    }

    internal static int BeginMapOpen(Mat viewport) => BeginMapOpenCore(viewport, null);

    internal static int BeginMapOpen(CapturedGameFrame frame) => BeginMapOpenCore(frame.Image, frame);

    private static int BeginMapOpenCore(Mat viewport, CapturedGameFrame? frame)
    {
        string? matchDirectory;
        int id;
        lock (Gate)
        {
            matchDirectory = _matchDirectory;
            id = matchDirectory is null ? 0 : ++_attemptId;
            _currentMapOpenId = id;
        }
        if (matchDirectory is not null)
            TryWrite(Path.Combine(matchDirectory, "显示区域", $"显示区域 {id}.png"), viewport);
        if (id > 0 && matchDirectory is not null && frame?.DiagnosticSourceImage is { } source)
        {
            try
            {
                var directory = Path.Combine(matchDirectory, "捕获原帧");
                Directory.CreateDirectory(directory);
                TryWrite(Path.Combine(directory, $"捕获原帧 {id}.png"), source);
                var metadata = new
                {
                    frame.ClientBounds,
                    SourceBounds = frame.DiagnosticSourceBounds,
                    frame.ViewportBounds,
                    frame.CaptureSystemRelativeTicks,
                    frame.CaptureBackend,
                    frame.DetectedFloorKey
                };
                File.WriteAllText(Path.Combine(directory, $"捕获原帧 {id}.json"),
                    System.Text.Json.JsonSerializer.Serialize(metadata));
            }
            catch { /* Diagnostics must never change identity or alignment behavior. */ }
        }
        return id;
    }

    internal static IDisposable Suppress()
    {
        SuppressionDepth.Value++;
        return new SuppressionScope();
    }


    internal static void WriteInputs(Mat viewport, Mat structure, int? attemptId = null, string? tag = null)
    {
        if (SuppressionDepth.Value > 0)
            return;
        string? matchDirectory;
        int id;
        if (DeferredFitness.Value is { } capture)
        {
            matchDirectory = capture.MatchDirectory;
            id = attemptId ?? capture.MapOpenId;
        }
        else lock (Gate)
        {
            matchDirectory = _matchDirectory;
            id = attemptId ?? _currentMapOpenId;
        }
        if (matchDirectory is null || id <= 0)
            return;
        var suffix = string.IsNullOrWhiteSpace(tag) ? string.Empty : $"_{tag}";
        WriteDiagnosticImage(Path.Combine(matchDirectory, "结构配准", $"结构配准 {id}{suffix}.png"), structure);
    }

    internal static void WriteFitness(Mat image, int? attemptId = null, string? tag = null)
    {
        if (SuppressionDepth.Value > 0)
            return;
        string? matchDirectory;
        int id;
        if (DeferredFitness.Value is { } capture)
        {
            matchDirectory = capture.MatchDirectory;
            id = attemptId ?? capture.MapOpenId;
        }
        else lock (Gate)
        {
            matchDirectory = _matchDirectory;
            id = attemptId ?? _currentMapOpenId;
        }
        if (matchDirectory is not null && id > 0)
        {
            var suffix = string.IsNullOrWhiteSpace(tag) ? string.Empty : $"_{tag}";
            var path = Path.Combine(matchDirectory, "贴合度", $"贴合度 {id}{suffix}.png");
            // An immediate writer is later than any pending drawing for this
            // path. It must not be overwritten when the job finishes.
            DeferredFitness.Value?.Discard(path);
            WriteDiagnosticImage(path, image);
        }
    }

    private static void WriteDiagnosticImage(string path, Mat image)
    {
        if (DeferredFitness.Value is { } capture)
            capture.WriteIfCurrent(path, image);
        else
            TryWrite(path, image);
    }

    internal static string? WriteNativeMiniMap(Mat image, string suffix = "")
    {
        // Serialize with EndMatch/Clear so an in-flight write cannot recreate a closed session.
        lock (Gate)
        {
            if (_matchDirectory is null) return null;
            var directory = Path.Combine(_matchDirectory, "原生小地图");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"原生小地图_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}{suffix}.png");
            // Byte encoding also supports Unicode paths. Report failures to the sampling loop.
            if (!Cv2.ImEncode(".png", image, out var bytes))
                throw new IOException("原生小地图 PNG 编码失败。");
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }

    internal static string? WriteUnresolvedScan(CapturedGameFrame frame,
        ScanFrameEvidence? evidence, IReadOnlyList<SideEntranceScanCandidate> candidates,
        ScanPerformanceMode mode)
    {
        var snapshot = CaptureUnresolvedScan(frame, evidence, candidates, mode);
        return snapshot is null ? null : WriteUnresolvedScanSnapshot(snapshot);
    }

    internal static Task<string?> WriteUnresolvedScanAsync(CapturedGameFrame frame,
        ScanFrameEvidence? evidence, IReadOnlyList<SideEntranceScanCandidate> candidates,
        ScanPerformanceMode mode)
    {
        // Freeze pixels and metadata before returning to the scan. Neither a
        // disposed frame nor a later match/candidate may change this evidence.
        var snapshot = CaptureUnresolvedScan(frame, evidence, candidates, mode);
        if (snapshot is null) return Task.FromResult<string?>(null);
        try { return Task.Run(() => WriteUnresolvedScanSnapshot(snapshot)); }
        catch
        {
            snapshot.Dispose();
            return Task.FromResult<string?>(null);
        }
    }

    private sealed record UnresolvedScanSnapshot(string Directory, string Json,
        Mat Image, Mat? Edges, Mat? Mask, IDisposable Protection) : IDisposable
    {
        public void Dispose()
        {
            Image.Dispose();
            Edges?.Dispose();
            Mask?.Dispose();
            Protection.Dispose();
        }
    }

    private static UnresolvedScanSnapshot? CaptureUnresolvedScan(CapturedGameFrame frame,
        ScanFrameEvidence? evidence, IReadOnlyList<SideEntranceScanCandidate> candidates,
        ScanPerformanceMode mode)
    {
        // Explicit scan evidence is separate from suppressed per-candidate alignment
        // captures. The caller has already made the automatic identity decision.
        lock (Gate)
        {
            if (_matchDirectory is null) return null;
            Mat? image = null, edges = null, mask = null;
            try
            {
                var directory = Path.Combine(_matchDirectory, "扫描",
                    DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff"));
                var execution = ScanExecutionContext.Current;
                var data = new
                {
                    mode = mode.ToString(), frame.ClientBounds, frame.ViewportBounds,
                    scanId = execution?.ScanId, scanElapsedMs = execution?.ElapsedMilliseconds,
                    retrievalComplete = execution?.RetrievalCompleted,
                    computeStopReason = execution?.ComputeStopReason,
                    candidates = candidates.Select((candidate, rank) => new
                    {
                        retrievalRank = rank + 1, candidate.Map.Id, candidate.Map.Class,
                        candidate.Map.SequenceNumber, candidate.FloorKey,
                        candidate.MatchScore, candidate.MatchScale, candidate.MatchLocation,
                        candidate.IdentityEvidence, candidate.VerifiedTransform,
                        hypotheses = (candidate.SearchHypotheses.Count > 0
                            ? candidate.SearchHypotheses : new[] { candidate }).Select(h => new
                            {
                                h.MatchScore, h.MatchScale, h.MatchLocation,
                                h.GateSpatialResidualPixels, h.AssociatedGate, h.IdentityEvidence, h.VerifiedTransform
                            })
                    })
                };
                var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    });
                image = frame.Image.Clone();
                edges = evidence?.Observation.ObservedEdges.Clone();
                mask = evidence?.Observation.ValidMask.Clone();
                return new(directory, json, image, edges, mask, AppDataPaths.ProtectCachePath(directory));
            }
            catch
            {
                image?.Dispose(); edges?.Dispose(); mask?.Dispose();
                return null; /* Diagnostics must not change the scan result. */
            }
        }
    }

    private static string? WriteUnresolvedScanSnapshot(UnresolvedScanSnapshot snapshot)
    {
        using (snapshot)
        {
            try
            {
                Directory.CreateDirectory(snapshot.Directory);
                WritePng(Path.Combine(snapshot.Directory, "viewport.png"), snapshot.Image);
                if (snapshot.Edges is not null) WritePng(Path.Combine(snapshot.Directory, "observed-edges.png"), snapshot.Edges);
                if (snapshot.Mask is not null) WritePng(Path.Combine(snapshot.Directory, "valid-mask.png"), snapshot.Mask);
                File.WriteAllText(Path.Combine(snapshot.Directory, "scan.json"), snapshot.Json);
                return snapshot.Directory;
            }
            catch { return null; /* Preserve the automatic decision on write failure. */ }
        }
    }

    private static void WritePng(string path, Mat image)
    {
        if (!Cv2.ImEncode(".png", image, out var bytes))
            throw new IOException("扫描诊断 PNG 编码失败。");
        File.WriteAllBytes(path, bytes);
    }

    internal static void TryWrite(string path, Mat image)
    {
        using var protection = AppDataPaths.ProtectCachePath(path);
        try { Cv2.ImWrite(path, image); }
        catch { /* Diagnostics must never change alignment behavior. */ }
    }

    private sealed class SuppressionScope : IDisposable
    {
        public void Dispose() => SuppressionDepth.Value = Math.Max(0, SuppressionDepth.Value - 1);
    }
}
