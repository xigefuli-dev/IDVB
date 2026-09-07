using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.Features.Maps;

namespace IDVBuff.Diagnostics;

/// <summary>
/// 实时性能与内存资源追踪器。提供运行态物理内存、托管堆、增量追踪及关键资源调用探针。
/// 内存口径严格对齐 Windows 任务管理器“进程”列表默认的“活动专用工作集（Working Set - Private）”。
/// </summary>
public static class RealtimePerformanceTracker
{
    private static readonly Process CurrentProcess = Process.GetCurrentProcess();
    private static readonly object Gate = new();
    private static readonly AsyncLocal<string?> ActiveFunctionSlot = new();
    private static volatile string _latestActiveFunction = "Idle";
    private static double _lastDeltaWorkingSetMb;
    private static double _lastActiveElapsedMs;
    private static long _lastSampleTick;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public nint PeakWorkingSetSize;
        public nint WorkingSetSize;
        public nint QuotaPeakPagedPoolUsage;
        public nint QuotaPagedPoolUsage;
        public nint QuotaPeakNonPagedPoolUsage;
        public nint QuotaNonPagedPoolUsage;
        public nint PagefileUsage;
        public nint PeakPagefileUsage;
        public nint PrivateUsage;
        public nint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(
        IntPtr hProcess,
        out PROCESS_MEMORY_COUNTERS_EX2 counters,
        uint cb);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public readonly record struct PerformanceSnapshot(
        double WorkingSetMb,
        double PrivateWorkingSetMb,
        double TotalWorkingSetMb,
        double GcHeapMb,
        double PagedMemoryMb,
        double PeakWorkingSetMb,
        string ActiveFunction,
        double ActiveElapsedMs,
        double LastDeltaWorkingSetMb,
        DateTimeOffset Timestamp);

    public static event Action<PerformanceSnapshot>? SnapshotUpdated;

    public static string LatestActiveFunction => _latestActiveFunction;
    public static double LastDeltaWorkingSetMb => _lastDeltaWorkingSetMb;
    public static double LastActiveElapsedMs => _lastActiveElapsedMs;

    /// <summary>
    /// 获取当前物理内存专用工作集（MB，严格对齐任务管理器“进程”选项卡中的“内存”列）。
    /// </summary>
    public static double CurrentWorkingSetMb => CurrentPrivateWorkingSetMb;

    /// <summary>获取当前活动专用工作集（MB，独占物理内存，对标任务管理器）。</summary>
    public static double CurrentPrivateWorkingSetMb => GetMemoryMetrics().PrivateWorkingSetMb;

    /// <summary>获取当前总物理工作集（MB，包含共享系统库等所有物理页面）。</summary>
    public static double CurrentTotalWorkingSetMb => GetMemoryMetrics().TotalWorkingSetMb;

    /// <summary>获取当前 .NET GC 托管堆大小（MB）。</summary>
    public static double CurrentGcHeapMb => GC.GetTotalMemory(false) / (1024.0 * 1024.0);

    /// <summary>获取当前峰值物理工作集（MB）。</summary>
    public static double CurrentPeakWorkingSetMb => GetMemoryMetrics().PeakWorkingSetMb;

    /// <summary>高效、精准获取底层进程物理内存指标（优先调用原生 Win32 Psapi）。</summary>
    public static (double PrivateWorkingSetMb, double TotalWorkingSetMb, double PeakWorkingSetMb) GetMemoryMetrics()
    {
        try
        {
            var hProc = GetCurrentProcess();
            var counters = new PROCESS_MEMORY_COUNTERS_EX2
            {
                cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>()
            };

            if (K32GetProcessMemoryInfo(hProc, out counters, counters.cb) && counters.WorkingSetSize > 0)
            {
                var totalWsMb = counters.WorkingSetSize / (1024.0 * 1024.0);
                var peakWsMb = counters.PeakWorkingSetSize / (1024.0 * 1024.0);
                var privWsMb = counters.PrivateWorkingSetSize > 0
                    ? counters.PrivateWorkingSetSize / (1024.0 * 1024.0)
                    : totalWsMb;

                return (privWsMb, totalWsMb, peakWsMb);
            }
        }
        catch
        {
            // 忽略原生调用异常，平滑回退
        }

        CurrentProcess.Refresh();
        var fallbackWs = CurrentProcess.WorkingSet64 / (1024.0 * 1024.0);
        var fallbackPeak = CurrentProcess.PeakWorkingSet64 / (1024.0 * 1024.0);
        return (fallbackWs, fallbackWs, fallbackPeak);
    }

    /// <summary>捕获一份最新的性能与资源快照。</summary>
    public static PerformanceSnapshot CaptureSnapshot()
    {
        var (privWs, totalWs, peakWs) = GetMemoryMetrics();
        var gc = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        CurrentProcess.Refresh();
        var paged = CurrentProcess.PagedMemorySize64 / (1024.0 * 1024.0);

        var snapshot = new PerformanceSnapshot(
            WorkingSetMb: privWs,
            PrivateWorkingSetMb: privWs,
            TotalWorkingSetMb: totalWs,
            GcHeapMb: gc,
            PagedMemoryMb: paged,
            PeakWorkingSetMb: peakWs,
            ActiveFunction: _latestActiveFunction,
            ActiveElapsedMs: _lastActiveElapsedMs,
            LastDeltaWorkingSetMb: _lastDeltaWorkingSetMb,
            Timestamp: DateTimeOffset.UtcNow);

        SnapshotUpdated?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// 将进程中未使用的物理内存页剥离出物理工作集，返还给操作系统。
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            // 修剪工作集属于辅助优化，失败时安全忽略
        }
    }

    /// <summary>
    /// 开始对能够调用大资源的关键函数进行性能与内存增量打点。
    /// 退出 using 时自动计算增量、耗时并输出日志。
    /// </summary>
    public static IDisposable TrackScope(string functionName, string? context = null, bool forceLog = false)
    {
        return new ResourceScope(functionName, context, forceLog);
    }

    /// <summary>
    /// 在关键生命周期节点（进对局、出对局、重置）执行完整的内存审计。
    /// </summary>
    public static void AuditLifecycle(string lifecyclePhase, string? detail = null, bool triggerGcAudit = false)
    {
        var (privBefore, totalBefore, peak) = GetMemoryMetrics();
        var gcBefore = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        if (!triggerGcAudit)
        {
            var msg = $"[LIFECYCLE] {lifecyclePhase} | privWs={privBefore:F1}MB (totalWs={totalBefore:F1}MB) | gcHeap={gcBefore:F1}MB | peakWs={peak:F1}MB"
                + (string.IsNullOrEmpty(detail) ? "" : $" | {detail}");
            OutputLog.Write("INFO", "PERF/LIFECYCLE", msg);
            MapLogCollector.Instance.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                msg,
                details: new()
                {
                    ["phase"] = lifecyclePhase,
                    ["privWorkingSetMb"] = privBefore,
                    ["totalWorkingSetMb"] = totalBefore,
                    ["gcHeapMb"] = gcBefore,
                    ["peakWorkingSetMb"] = peak
                });
            return;
        }

        // 显式触发一次 GC 垃圾回收与堆压缩探测，查明滞留内存是托管堆还是原生堆
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // 原生堆（C++ CRT/OpenCV）与托管堆释放后，Windows 物理工作集默认保留页面不归还给系统。
        // 在对局重置等非高频生命周期节点剥离空闲物理页，退还给操作系统。
        TrimWorkingSet();

        var (privAfter, totalAfter, _) = GetMemoryMetrics();
        var gcAfter = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        var deltaPriv = privAfter - privBefore;
        var deltaTotal = totalAfter - totalBefore;
        var deltaGc = gcAfter - gcBefore;

        var auditMsg = $"[LIFECYCLE/GC-AUDIT] {lifecyclePhase} | "
            + $"privWs={privBefore:F1}MB -> {privAfter:F1}MB (delta={(deltaPriv >= 0 ? "+" : "")}{deltaPriv:F1}MB) | "
            + $"totalWs={totalBefore:F1}MB -> {totalAfter:F1}MB (delta={(deltaTotal >= 0 ? "+" : "")}{deltaTotal:F1}MB) | "
            + $"gcHeap={gcBefore:F1}MB -> {gcAfter:F1}MB (delta={(deltaGc >= 0 ? "+" : "")}{deltaGc:F1}MB) | "
            + $"peakWs={peak:F1}MB"
            + (string.IsNullOrEmpty(detail) ? "" : $" | {detail}");

        OutputLog.Write("INFO", "PERF/LIFECYCLE", auditMsg);
        MapLogCollector.Instance.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            auditMsg,
            details: new()
            {
                ["phase"] = lifecyclePhase,
                ["privBeforeMb"] = privBefore,
                ["privAfterMb"] = privAfter,
                ["deltaPrivMb"] = deltaPriv,
                ["totalBeforeMb"] = totalBefore,
                ["totalAfterMb"] = totalAfter,
                ["deltaTotalMb"] = deltaTotal,
                ["gcBeforeMb"] = gcBefore,
                ["gcAfterMb"] = gcAfter,
                ["deltaGcMb"] = deltaGc,
                ["peakWorkingSetMb"] = peak
            });

        lock (Gate)
        {
            _lastDeltaWorkingSetMb = deltaPriv;
            _latestActiveFunction = "GC.Collect";
            _lastActiveElapsedMs = 0;
        }
        CaptureSnapshot();
    }

    private sealed class ResourceScope : IDisposable
    {
        private readonly string _functionName;
        private readonly string? _context;
        private readonly bool _forceLog;
        private readonly string? _previousFunction;
        private readonly double _startPrivWs;
        private readonly double _startTotalWs;
        private readonly double _startGc;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        public ResourceScope(string functionName, string? context, bool forceLog)
        {
            _functionName = functionName;
            _context = context;
            _forceLog = forceLog;
            _previousFunction = ActiveFunctionSlot.Value;
            ActiveFunctionSlot.Value = functionName;
            _latestActiveFunction = functionName;

            var (privWs, totalWs, _) = GetMemoryMetrics();
            _startPrivWs = privWs;
            _startTotalWs = totalWs;
            _startGc = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopwatch.Stop();
            var elapsedMs = _stopwatch.Elapsed.TotalMilliseconds;

            var (endPrivWs, endTotalWs, _) = GetMemoryMetrics();
            var endGc = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            var deltaPrivWs = endPrivWs - _startPrivWs;

            ActiveFunctionSlot.Value = _previousFunction;
            _latestActiveFunction = _previousFunction ?? "Idle";

            lock (Gate)
            {
                _lastDeltaWorkingSetMb = deltaPrivWs;
                _lastActiveElapsedMs = elapsedMs;
            }

            // 增量超过 0.5MB、耗时超 25ms 或显式要求日志时输出记录
            if (_forceLog || Math.Abs(deltaPrivWs) >= 0.5 || elapsedMs >= 25.0)
            {
                var sign = deltaPrivWs >= 0 ? "+" : "";
                var logMsg = $"[{_functionName}] elapsed={elapsedMs:F1}ms | privWs={endPrivWs:F1}MB (totalWs={endTotalWs:F1}MB) | deltaPriv={sign}{deltaPrivWs:F1}MB | gcHeap={endGc:F1}MB"
                    + (string.IsNullOrEmpty(_context) ? "" : $" | {_context}");

                OutputLog.Write("INFO", "PERF/RESOURCE", logMsg);
                MapLogCollector.Instance.Append(
                    MapLogCategory.System,
                    MapLogLevel.Info,
                    $"[PERF/RESOURCE] {_functionName}",
                    elapsedMs: elapsedMs,
                    details: new()
                    {
                        ["function"] = _functionName,
                        ["elapsedMs"] = elapsedMs,
                        ["privWorkingSetMb"] = endPrivWs,
                        ["totalWorkingSetMb"] = endTotalWs,
                        ["deltaWorkingSetMb"] = deltaPrivWs,
                        ["gcHeapMb"] = endGc,
                        ["context"] = _context
                    });
            }

            // 节流触发快照通知
            var nowTick = Environment.TickCount64;
            if (nowTick - _lastSampleTick >= 200)
            {
                _lastSampleTick = nowTick;
                CaptureSnapshot();
            }
        }
    }
}
