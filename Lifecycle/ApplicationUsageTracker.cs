using System.Diagnostics;
using System.Text.Json;

namespace IDVBuff.Lifecycle;

internal readonly record struct ApplicationUsageSnapshot(TimeSpan? Total, TimeSpan Session);

/// <summary>Tracks the primary GUI lifetime independently of navigation and diagnostic logging.</summary>
internal sealed class ApplicationUsageTracker : IDisposable
{
    private sealed record Statistics(int SchemaVersion, long TotalTicks);
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly string _path;
    private readonly Stopwatch _stopwatch = new();
    private readonly Func<TimeSpan> _getElapsed;
    private readonly bool _periodicSave;
    private readonly long _previousTicks;
    private readonly bool _historyReadable;
    private Timer? _saveTimer;
    private TimeSpan _stoppedElapsed;
    private bool _started;
    private bool _disposed;

    // Defer reading the previous session until Program owns the primary GUI mutex.
    private static readonly Lazy<ApplicationUsageTracker> Shared = new(() => new(Path.Combine(
        AppDataPaths.RootDirectory, "application-usage.json")));
    internal static ApplicationUsageTracker Current => Shared.Value;

    internal ApplicationUsageTracker(string path, Func<TimeSpan>? getElapsed = null,
        bool periodicSave = true)
    {
        _path = path;
        _getElapsed = getElapsed ?? (() => _stopwatch.Elapsed);
        _periodicSave = periodicSave;
        try
        {
            if (File.Exists(path))
            {
                var statistics = JsonSerializer.Deserialize<Statistics>(File.ReadAllText(path));
                if (statistics is null || statistics.SchemaVersion != 1 || statistics.TotalTicks < 0)
                    throw new InvalidDataException("Unsupported application usage statistics.");
                _previousTicks = statistics.TotalTicks;
            }
            _historyReadable = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Preserve unreadable history rather than silently replacing it with zero.
            Debug.WriteLine($"[ApplicationUsage] History unavailable: {exception.Message}");
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _stopwatch.Start();
            _started = true;
            if (_periodicSave)
                _saveTimer = new Timer(_ => SaveCheckpoint(), null,
                    TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    internal ApplicationUsageSnapshot GetSnapshot()
    {
        lock (_gate) return GetSnapshotCore();
    }

    private ApplicationUsageSnapshot GetSnapshotCore()
    {
        var session = !_started ? TimeSpan.Zero : _disposed ? _stoppedElapsed : _getElapsed();
        var total = _historyReadable
            ? TimeSpan.FromTicks(_previousTicks + Math.Min(session.Ticks, long.MaxValue - _previousTicks))
            : (TimeSpan?)null;
        return new ApplicationUsageSnapshot(total, session);
    }

    internal void SaveCheckpoint()
    {
        lock (_saveGate)
        {
            long? ticks;
            lock (_gate)
            {
                if (!_started || _disposed) return;
                ticks = GetSnapshotCore().Total?.Ticks;
            }
            SaveCore(ticks);
        }
    }

    private void SaveCore(long? totalTicks)
    {
        if (totalTicks is null) return;
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // Each checkpoint stores the same session baseline plus elapsed time; it never adds
            // a previously checkpointed session twice. Replacement prevents partial JSON reads.
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                new Statistics(1, totalTicks.Value)));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[ApplicationUsage] Checkpoint unavailable: {exception.Message}");
        }
    }

    public void Dispose()
    {
        lock (_saveGate)
        {
            long? ticks;
            lock (_gate)
            {
                if (_disposed) return;
                _stoppedElapsed = _started ? _getElapsed() : TimeSpan.Zero;
                _stopwatch.Stop();
                _disposed = true;
                _saveTimer?.Dispose();
                _saveTimer = null;
                ticks = _started ? GetSnapshotCore().Total?.Ticks : null;
            }
            SaveCore(ticks);
        }
    }

    internal static string FormatDuration(TimeSpan duration) => duration.Days > 0
        ? $"{duration.Days} 天 {duration.Hours} 小时"
        : duration.TotalHours >= 1
            ? $"{(long)duration.TotalHours} 小时 {duration.Minutes} 分"
            : $"{duration.Minutes} 分 {duration.Seconds} 秒";
}
