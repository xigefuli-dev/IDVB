using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapDiagnosticModeCapture
{
    private static readonly object Gate = new();
    private static int _currentMapOpenId;
    private static readonly AsyncLocal<int> SuppressionDepth = new();
    private static string? _matchDirectory;
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
        get { lock (Gate) return _currentMapOpenId; }
    }

    internal static int BeginMapOpen(Mat viewport)
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
        lock (Gate)
        {
            matchDirectory = _matchDirectory;
            id = attemptId ?? _currentMapOpenId;
        }
        if (matchDirectory is null || id <= 0)
            return;
        var suffix = string.IsNullOrWhiteSpace(tag) ? string.Empty : $"_{tag}";
        TryWrite(Path.Combine(matchDirectory, "结构配准", $"结构配准 {id}{suffix}.png"), structure);
    }

    internal static void WriteFitness(Mat image, int? attemptId = null, string? tag = null)
    {
        if (SuppressionDepth.Value > 0)
            return;
        string? matchDirectory;
        int id;
        lock (Gate)
        {
            matchDirectory = _matchDirectory;
            id = attemptId ?? _currentMapOpenId;
        }
        if (matchDirectory is not null && id > 0)
        {
            var suffix = string.IsNullOrWhiteSpace(tag) ? string.Empty : $"_{tag}";
            TryWrite(Path.Combine(matchDirectory, "贴合度", $"贴合度 {id}{suffix}.png"), image);
        }
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
        // Explicit scan evidence is separate from suppressed per-candidate alignment
        // captures. The caller has already made the automatic identity decision.
        lock (Gate)
        {
            if (_matchDirectory is null) return null;
            try
            {
                var directory = Path.Combine(_matchDirectory, "扫描",
                    DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff"));
                Directory.CreateDirectory(directory);
                WritePng(Path.Combine(directory, "viewport.png"), frame.Image);
                if (evidence is not null)
                {
                    WritePng(Path.Combine(directory, "observed-edges.png"), evidence.Observation.ObservedEdges);
                    WritePng(Path.Combine(directory, "valid-mask.png"), evidence.Observation.ValidMask);
                }
                var data = new
                {
                    mode = mode.ToString(), frame.ClientBounds, frame.ViewportBounds,
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
                File.WriteAllText(Path.Combine(directory, "scan.json"),
                    System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    }));
                return directory;
            }
            catch { return null; /* Diagnostics must not change the scan result. */ }
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
        try { Cv2.ImWrite(path, image); }
        catch { /* Diagnostics must never change alignment behavior. */ }
    }

    private sealed class SuppressionScope : IDisposable
    {
        public void Dispose() => SuppressionDepth.Value = Math.Max(0, SuppressionDepth.Value - 1);
    }
}
