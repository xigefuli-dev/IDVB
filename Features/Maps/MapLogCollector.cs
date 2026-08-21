using System.Diagnostics;
using System.Globalization;
using IDVBuff.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Thread-safe, opt-in collector for structured map diagnostics.
/// A session is flushed in small batches and finalized when collection stops.
/// </summary>
public sealed partial class MapLogCollector : IDisposable, IAsyncDisposable
{
    private const int FlushBatchSize = 50;
    private const int MaxBufferedEntries = 500;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FinalFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly object _stateGate = new();
    private readonly MapLogRepository _repository;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private Session? _session;
    private Timer? _flushTimer;
    private bool _isEnabled;
    private bool _disposed;
    private readonly List<Task> _finalizationTasks = [];

    /// <summary>
    /// Process-wide fallback used by recognition components before the runtime host is ready.
    /// </summary>
    public static MapLogCollector Instance { get; set; } = new();

    public MapLogCollector(MapLogRepository? repository = null)
    {
        _repository = repository ?? new MapLogRepository();
    }

    public bool IsEnabled
    {
        get => Volatile.Read(ref _isEnabled);
        set
        {
            Task? finalizationTask = null;
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (value == _isEnabled)
                    return;

                if (value)
                {
                    var session = new Session(_repository.CreateSessionPath());
                    _session = session;
                    _isEnabled = true;
                    _flushTimer = new Timer(
                        static state =>
                        {
                            var context = ((MapLogCollector Collector, Session Session))state!;
                            context.Collector.OnFlushTimer(context.Session);
                        },
                        (this, session),
                        FlushInterval,
                        FlushInterval);
                    _repository.CleanupOldSessions();
                    AppendInternal(
                        session,
                        MapLogCategory.System,
                        MapLogLevel.Info,
                        "Log collection started",
                        details: new() { ["sessionPath"] = session.Path });
                }
                else
                {
                    finalizationTask = StopCurrentSessionLocked("Log collection stopped");
                }
            }

            _ = finalizationTask;
        }
    }

    public int EntryCount
    {
        get
        {
            lock (_stateGate)
                return _session?.TotalEntryCount ?? 0;
        }
    }

    public int BufferedEntryCount
    {
        get
        {
            var session = Volatile.Read(ref _session);
            if (session is null)
                return 0;
            lock (session.Gate)
                return session.Entries.Count;
        }
    }

    public string? CurrentSessionPath => Volatile.Read(ref _session)?.Path;

    public string LogDirectory => _repository.LogDirectory;

    public event EventHandler? EntryAdded;

    public void Append(
        MapLogCategory category,
        MapLogLevel level,
        string message,
        double? elapsedMs = null,
        Dictionary<string, object?>? details = null)
    {
        WritePlainTextOutput(category, level, message, elapsedMs, details);
        lock (_stateGate)
        {
            if (!_isEnabled || _session is null)
                return;
            AppendInternal(
                _session,
                category,
                level,
                message,
                elapsedMs,
                details,
                writePlainTextOutput: false);
        }
    }

    public void Clear()
    {
        var session = Volatile.Read(ref _session);
        if (session is null)
            return;
        lock (session.Gate)
        {
            session.Entries.Clear();
            session.TotalEntryCount = 0;
            session.LastFlushedSequence = session.Sequence;
        }
    }

    /// <summary>
    /// Returns only entries still waiting to be written. Persisted entries are intentionally
    /// removed from memory so normal memory usage follows the pending write backlog.
    /// </summary>
    public IReadOnlyList<MapLogEntry> GetEntries()
    {
        var session = Volatile.Read(ref _session);
        if (session is null)
            return [];
        lock (session.Gate)
            return session.Entries.ToArray();
    }

    public async Task<IReadOnlyList<MapLogEntry>> GetCompleteEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        var session = Volatile.Read(ref _session);
        if (session is null)
            return [];

        await FlushAsync(session).ConfigureAwait(false);

        var persisted = await _repository.ReadSessionAsync(
            session.Path,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MapLogEntry> pending;
        lock (session.Gate)
            pending = session.Entries.ToArray();

        return persisted
            .Concat(pending)
            .GroupBy(entry => entry.Sequence)
            .Select(group => group.First())
            .OrderBy(entry => entry.Sequence)
            .ToArray();
    }

    private void AppendInternal(
        Session session,
        MapLogCategory category,
        MapLogLevel level,
        string message,
        double? elapsedMs = null,
        Dictionary<string, object?>? details = null,
        bool writePlainTextOutput = true)
    {
        var entry = new MapLogEntry
        {
            Sequence = Interlocked.Increment(ref session.Sequence),
            Timestamp = DateTimeOffset.UtcNow,
            Category = category,
            Level = level,
            Message = message,
            ElapsedMs = elapsedMs,
            Details = details
        };

        if (writePlainTextOutput)
            WritePlainTextOutput(category, level, message, elapsedMs, details);

        var shouldFlush = false;
        lock (session.Gate)
        {
            if (session.PersistenceDisabled
                && session.Entries.Count >= MaxBufferedEntries)
            {
                session.Entries.RemoveAt(0);
                session.DroppedEntryCount++;
            }
            session.Entries.Add(entry);
            session.TotalEntryCount++;
            if (!session.PersistenceDisabled
                && (session.Entries.Count == 1
                    || session.Entries.Count % FlushBatchSize == 0))
                shouldFlush = true;
        }

        if (shouldFlush)
            RequestFlush(session);
    }

    private static void WritePlainTextOutput(
        MapLogCategory category,
        MapLogLevel level,
        string message,
        double? elapsedMs,
        Dictionary<string, object?>? details)
    {
        try
        {
            var outputMessage = message;
            if (elapsedMs is not null)
            {
                outputMessage += $" | elapsedMs="
                    + elapsedMs.Value.ToString("0.###", CultureInfo.InvariantCulture);
            }
            if (details is not null)
            {
                foreach (var detail in details)
                {
                    outputMessage += $" | {detail.Key}="
                        + (Convert.ToString(detail.Value, CultureInfo.InvariantCulture) ?? "null");
                }
            }
            OutputLog.Write(level.ToString(), $"MAP/{category}", outputMessage);
        }
        catch
        {
            // The plain-text logging side channel must never affect map recognition.
        }
    }

    private void OnFlushTimer(Session session)
    {
        RequestFlush(session);
    }

    private Task FlushAsync(Session session) => RequestFlush(session);

    private async Task FlushCoreAsync(Session session)
    {
        var gateEntered = false;
        try
        {
            await _flushGate.WaitAsync().ConfigureAwait(false);
            gateEntered = true;
            IReadOnlyList<MapLogEntry> batch;
            lock (session.Gate)
            {
                if (session.Entries.Count == 0)
                    return;
                batch = session.Entries
                    .Where(entry => entry.Sequence > session.LastFlushedSequence)
                    .ToArray();
                if (batch.Count == 0)
                    return;
            }

            await _repository.FlushAsync(session.Path, batch).ConfigureAwait(false);
            lock (session.Gate)
            {
                session.LastFlushedSequence = Math.Max(
                    session.LastFlushedSequence,
                    batch[^1].Sequence);
                session.Entries.RemoveAll(
                    entry => entry.Sequence <= session.LastFlushedSequence);
            }
            EntryAdded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            lock (session.FlushTaskGate)
            {
                session.PersistenceDisabled = true;
                session.FlushRequested = false;
            }
            lock (session.Gate)
            {
                if (session.Entries.Count > MaxBufferedEntries)
                {
                    var removeCount = session.Entries.Count - MaxBufferedEntries;
                    session.Entries.RemoveRange(0, removeCount);
                    session.DroppedEntryCount += removeCount;
                }
            }
            Debug.WriteLine($"[MapLogCollector] flush failed: {exception}");
            WriteErrorToFile("flush", exception);
        }
        finally
        {
            if (gateEntered)
            {
                try
                {
                    _flushGate.Release();
                }
                catch (SemaphoreFullException)
                {
                    // The semaphore was already released during shutdown.
                }
            }
        }
    }

    private Task? StopCurrentSessionLocked(string message)
    {
        if (!_isEnabled || _session is null)
            return null;

        _isEnabled = false;
        DisposeTimerLocked();
        var session = _session;
        AppendInternal(session, MapLogCategory.System, MapLogLevel.Info, message);
        _session = null;
        var finalizationTask = FinalizeSessionAsync(session);
        _finalizationTasks.Add(finalizationTask);
        return finalizationTask;
    }

    private async Task FinalizeSessionAsync(Session session)
    {
        try
        {
            if (session.PersistenceDisabled)
                return;

            // Force the final marker through the same coalesced worker so a
            // racing timer callback cannot write the session file in parallel.
            var pending = RequestFlush(session);

            if (!await WaitForCompletionAsync(pending).ConfigureAwait(false))
            {
                WriteErrorToFile(
                    "finalize",
                    new TimeoutException("Timed out waiting for pending log flushes."));
                return;
            }

            if (session.PersistenceDisabled)
                return;

            IReadOnlyList<MapLogEntry> remaining;
            lock (session.Gate)
                remaining = session.Entries.ToArray();
            if (remaining.Count == 0)
                return;

            var finalizeTask = _repository.FinalizeAsync(session.Path, remaining);
            if (!await WaitForCompletionAsync(finalizeTask).ConfigureAwait(false))
            {
                WriteErrorToFile(
                    "finalize",
                    new TimeoutException("Timed out writing the final log batch."));
                return;
            }

            lock (session.Gate)
            {
                session.LastFlushedSequence = Math.Max(
                    session.LastFlushedSequence,
                    remaining[^1].Sequence);
                session.Entries.RemoveAll(
                    entry => entry.Sequence <= session.LastFlushedSequence);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[MapLogCollector] finalize failed: {exception}");
            WriteErrorToFile("finalize", exception);
        }
    }

    private static async Task<bool> WaitForCompletionAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(FinalFlushTimeout))
            .ConfigureAwait(false);
        if (completed != task)
            return false;
        await task.ConfigureAwait(false);
        return true;
    }

    public void Dispose()
    {
        Task[] finalizations;
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopCurrentSessionLocked("Log collector disposed");
            finalizations = _finalizationTasks.ToArray();
        }

        try
        {
            Task.WhenAll(finalizations).Wait(FinalFlushTimeout);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[MapLogCollector] dispose failed: {exception}");
        }
        EntryAdded = null;
        // A timed-out file write may still be unwinding; do not dispose the gate underneath it.
    }

    public async ValueTask DisposeAsync()
    {
        Task[] finalizations;
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopCurrentSessionLocked("Log collector disposed");
            finalizations = _finalizationTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(finalizations).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[MapLogCollector] DisposeAsync failed: {exception}");
        }
        EntryAdded = null;
        // A timed-out file write may still be unwinding; do not dispose the gate underneath it.
    }

    private void WriteErrorToFile(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(_repository.LogDirectory);
            var errorPath = Path.Combine(_repository.LogDirectory, "flush-errors.log");
            var line = $"[{DateTimeOffset.UtcNow:O}] [{context}] "
                + $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}"
                + $"{exception.StackTrace}{Environment.NewLine}";
            File.AppendAllText(errorPath, line);
        }
        catch
        {
            // Logging must never take down the application.
        }
    }

    private void DisposeTimerLocked()
    {
        var timer = Interlocked.Exchange(ref _flushTimer, null);
        if (timer is null)
            return;
        timer.Change(Timeout.Infinite, Timeout.Infinite);
        timer.Dispose();
    }

    private sealed class Session(string path)
    {
        public readonly object Gate = new();
        public readonly object FlushTaskGate = new();
        public readonly string Path = path;
        public readonly List<MapLogEntry> Entries = [];
        public Task PendingFlush = Task.CompletedTask;
        public bool FlushRequested;
        public bool FlushLoopActive;
        public volatile bool PersistenceDisabled;
        public int DroppedEntryCount;
        public int Sequence;
        public int LastFlushedSequence;
        public int TotalEntryCount;
    }
}
/*
 * 文件职责：MapLogCollector。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
