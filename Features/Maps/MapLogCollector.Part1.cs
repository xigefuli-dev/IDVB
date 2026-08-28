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
