using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private long _persistedTicks;
    private long _checkpointedSessionTicks;
    private readonly string _mutexName;
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
        _mutexName = "Local\\IDVB.ApplicationUsage." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        _getElapsed = getElapsed ?? (() => _stopwatch.Elapsed);
        _periodicSave = periodicSave;
        try
        {
            _persistedTicks = ReadPersistedTicks();
            _historyReadable = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Preserve unreadable history rather than silently replacing it with zero.
            Debug.WriteLine($"[ApplicationUsage] History unavailable: {exception.Message}");
        }
    }

    private long ReadPersistedTicks()
    {
        if (!File.Exists(_path)) return 0;
        var statistics = JsonSerializer.Deserialize<Statistics>(File.ReadAllText(_path));
        if (statistics is null || statistics.SchemaVersion != 1 || statistics.TotalTicks < 0)
            throw new InvalidDataException("Unsupported application usage statistics.");
        return statistics.TotalTicks;
    }

    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _stopwatch.Start();
            _started = true;
            // The desktop shutdown path uses Environment.Exit, which bypasses Program's finally.
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
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
        var delta = Math.Max(0, session.Ticks - _checkpointedSessionTicks);
        var total = _historyReadable
            ? TimeSpan.FromTicks(_persistedTicks + Math.Min(delta, long.MaxValue - _persistedTicks))
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
                ticks = _historyReadable ? GetSnapshotCore().Session.Ticks : null;
            }
            SaveCore(ticks);
        }
    }

    private void SaveCore(long? sessionTicks)
    {
        if (sessionTicks is null) return;
        using var mutex = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            long delta;
            lock (_gate) delta = Math.Max(0, sessionTicks.Value - _checkpointedSessionTicks);
            // Development and installed GUIs may coexist. Merge only this process's
            // unsaved interval under a cross-process mutex, never overwrite another lifetime.
            var stored = ReadPersistedTicks();
            var totalTicks = stored + Math.Min(delta, long.MaxValue - stored);
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                new Statistics(1, totalTicks)));
            File.Move(temporaryPath, _path, overwrite: true);
            lock (_gate)
            {
                _persistedTicks = totalTicks;
                _checkpointedSessionTicks = sessionTicks.Value;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Debug.WriteLine($"[ApplicationUsage] Checkpoint unavailable: {exception.Message}");
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private void OnProcessExit(object? sender, EventArgs args) => Dispose();

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
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                _saveTimer?.Dispose();
                _saveTimer = null;
                ticks = _started && _historyReadable ? _stoppedElapsed.Ticks : null;
            }
            SaveCore(ticks);
        }
    }

    internal static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours.ToString("F1", CultureInfo.InvariantCulture) + " h";
}
