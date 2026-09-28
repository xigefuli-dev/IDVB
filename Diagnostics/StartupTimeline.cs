using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Runtime.InteropServices;

namespace IDVBuff.Diagnostics;

/// <summary>Early startup evidence, independent of XAML and the ordinary output logger.</summary>
internal static class StartupTimeline
{
    // Process I/O counts logical read/write traffic, not physical disk traffic.
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    public static IDisposable Measure(string name) => new Measurement(name);

    private sealed class Measurement : IDisposable
    {
        private readonly string _name;
        private readonly long _start;
        private readonly double? _cpu;
        private bool _disposed;

        public Measurement(string name)
        {
            _name = name;
            _start = Stopwatch.GetTimestamp();
            _cpu = ReadCpuMilliseconds();
            Write($"BEGIN {name}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var cpu = ReadCpuMilliseconds();
            var cpuDelta = cpu.HasValue && _cpu.HasValue
                ? (cpu.Value - _cpu.Value).ToString("F1", CultureInfo.InvariantCulture) : "unavailable";
            // END means scope exit, including exception unwinding, not successful completion.
            Write(FormattableString.Invariant($"END {_name}; wallMs={Stopwatch.GetElapsedTime(_start).TotalMilliseconds:F1}; processCpuDeltaMs={cpuDelta}"));
        }
    }

    private static double? ReadCpuMilliseconds() =>
        GetProcessTimes(new IntPtr(-1), out _, out _, out var kernel, out var user)
            ? (kernel + user) / 10000d : null;

    private static readonly object Gate = new();
    private static long origin = Stopwatch.GetTimestamp();
    private static long previous = origin;
    private static StreamWriter? writer;
    private static readonly ManualResetEventSlim SamplingStopped = new(false);
    private static int _samplingStarted;

    public static void StartSampling(Func<Action, bool> enqueueUi)
    {
        if (Interlocked.Exchange(ref _samplingStarted, 1) != 0) return;
        var lastUiAck = Stopwatch.GetTimestamp();
        var pending = 0;
        new Thread(() =>
        {
            try
            {
                // Independent of the thread pool being investigated, bounded even if startup stalls.
                for (var sample = 0; sample < 120 && !SamplingStopped.Wait(1000); sample++)
                {
                    if (Interlocked.CompareExchange(ref pending, 1, 0) == 0)
                    {
                        if (!enqueueUi(() =>
                        {
                            Volatile.Write(ref lastUiAck, Stopwatch.GetTimestamp());
                            Volatile.Write(ref pending, 0);
                        }))
                        {
                            Write("Startup sampler: UI dispatcher rejected probe.");
                            return;
                        }
                    }
                    Write(FormattableString.Invariant($"Startup sample: uiAckAgeMs={Stopwatch.GetElapsedTime(Volatile.Read(ref lastUiAck)).TotalMilliseconds:F1}; probePending={Volatile.Read(ref pending)}"));
                }
            }
            catch (Exception exception) { Write("Startup sampling stopped after diagnostic failure.", exception); }
        }) { IsBackground = true, Name = "IDVB startup sampler" }.Start();
    }

    public static void StopSampling() => SamplingStopped.Set();

    public static void Shutdown()
    {
        StopSampling();
        lock (Gate)
        {
            writer?.Dispose();
            writer = null;
        }
    }

    public static void Initialize(long mainEntered, DateTimeOffset mainUtc, long lifecycleCompleted)
    {
        lock (Gate)
        {
            origin = previous = mainEntered;
            try
            {
                // Avoid triggering legacy-data migration before it can be timed.
                var directory = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), AppDataPaths.ProductDirectoryName, "Logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory,
                    $"startup-{mainUtc:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
                writer = new StreamWriter(new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    lock (Gate) { writer?.Dispose(); writer = null; }
                };
                Write(FormattableString.Invariant($"Managed Main entered at {mainUtc:O}; mainToVelopackCompleteMs={Stopwatch.GetElapsedTime(mainEntered, lifecycleCompleted).TotalMilliseconds:F1}; loggingSetupMs={Stopwatch.GetElapsedTime(lifecycleCompleted).TotalMilliseconds:F1}. Main timestamp captured before Velopack; logging starts after lifecycle hooks."));
                using var process = Process.GetCurrentProcess();
                var start = new DateTimeOffset(process.StartTime);
                Write(FormattableString.Invariant($"Process metadata: processStart={start:O}; processToMainMs={(mainUtc - start).TotalMilliseconds:F1}; systemUptimeMs={Environment.TickCount64}; executable={Environment.ProcessPath}; version={BuildVersionInfo.BuildVersion}."));
            }
            catch
            {
                // Diagnostics must not prevent launch, including unavailable process metadata.
            }
        }
    }

    public static string Write(string message, Exception? exception = null)
    {
        lock (Gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(origin, now).TotalMilliseconds;
            var delta = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
            previous = now;
            var detail = string.Create(CultureInfo.InvariantCulture,
                $"[pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId} nativeTid={GetCurrentThreadId()} elapsedMs={elapsed:F1} deltaMs={delta:F1}] {message}");
            var cpu = ReadCpuMilliseconds()?.ToString("F1", CultureInfo.InvariantCulture) ?? "unavailable";
            var io = GetProcessIoCounters(new IntPtr(-1), out var counters)
                ? $"logicalReadBytes={counters.ReadBytes} logicalWriteBytes={counters.WriteBytes}" : "processIo=unavailable";
            detail += $" | processCpuMs={cpu} {io} poolThreads={ThreadPool.ThreadCount} poolPending={ThreadPool.PendingWorkItemCount} gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}";
            try
            {
                writer?.WriteLine($"{DateTimeOffset.Now:O} {detail}");
                if (exception is not null) writer?.WriteLine(exception);
            }
            catch { /* A failed diagnostic write must not break startup. */ }
            OutputLog.Write(exception is null ? "INFO" : "ERROR", "STARTUP", detail, exception);
            return detail;
        }
    }
}
