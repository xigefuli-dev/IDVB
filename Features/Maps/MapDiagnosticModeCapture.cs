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
        global::IDVBuff.AppDataPaths.RootDirectory,
        "诊断模式");

    internal static bool IsActive
    {
        get { lock (Gate) return _matchDirectory is not null; }
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
