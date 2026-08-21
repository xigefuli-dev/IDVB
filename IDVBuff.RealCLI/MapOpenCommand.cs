// IDVB Real CLI — mapopen 命令：仅对齐 E2E（先锁定 → 关图 → 重开 → 仅对齐）
//
// 模拟真实玩家流程：快捷扫描锁定地图身份与变换 → 关闭游戏地图 → 再次打开 →
// 只跑对齐管线（不再扫描识别）。用于量化仅对齐路径三个优化的收益：
//   P0-1 仅对齐稳定帧确认放宽（3→2 帧）→ alignmentPhaseTimings.stable_viewport_wait
//   P0-2 侧门种子宽松采纳（RecoveryConfidence）→ structure_search / wall_clock
//   P1-2 缓存命中验证放宽 → structure_search / wall_clock
//
// 退出码：0 = 仅对齐成功；1 = 锁定失败/对齐失败/参数错误；2 = Fatal 异常。

using System.Diagnostics;
using IDVBuff.Cli;
using IDVBuff.Features.Maps;
using IDVBuff.RealCLI.Cli;
using IDVBuff.RealCLI.Output;
using IDVBuff.RealCLI.Stubs;
using Microsoft.UI.Dispatching;

namespace IDVBuff.RealCLI;

internal static class MapOpenCommand
{
    private enum LockState { NoLock, IdentityOnly, LockNoTransform, FullLock }

    public static async Task<int> RunAsync(string[] args, DispatcherQueue dispatcher)
    {
        string? imagePath = null;
        string? outputPath = null;
        string? settingsRoot = null;
        int? candidate = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--image":
                case "-i":
                    imagePath = args[++i]; break;
                case "--candidate":
                case "-c":
                    candidate = int.Parse(args[++i]); break;
                case "--out":
                case "-o":
                    outputPath = args[++i]; break;
                case "--settings":
                case "-s":
                    settingsRoot = args[++i]; break;
            }
        }

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            Console.Error.WriteLine("错误：缺少 --image <path> 参数。");
            return 1;
        }
        if (!File.Exists(imagePath))
        {
            Console.Error.WriteLine($"错误：文件不存在 —— {imagePath}");
            return 1;
        }

        try
        {
            var orchestrator = OrchestratorFactory.BuildOrchestrator(
                dispatcher, imagePath, settingsRoot, out var overlay);
            var result = await RunMapOpenAsync(orchestrator, overlay, imagePath, candidate);

            if (outputPath is not null)
                await RealCliOutputWriter.WriteAsync(result, outputPath);
            else
                RealCliOutputWriter.WriteLine(result);

            return result.AlignmentSucceeded ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal 异常：{ex}");
            return 2;
        }
    }

    private static async Task<RealCliSessionResult> RunMapOpenAsync(
        SessionOrchestrator orchestrator,
        RecordingOverlayWindow overlay,
        string imagePath,
        int? candidate)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await orchestrator.InitializeAsync();
            await orchestrator.BeginMatchAsync(PlayerSlot.Player1, "S0 厄运之女 · 困难");

            // 强制候选选择（--candidate）只在后台扫描关闭时生效；
            // 临时会话级关闭，不污染用户 settings。
            var backgroundWasEnabled = orchestrator.Settings.BackgroundScanEnabled;
            if (backgroundWasEnabled)
                orchestrator.SetBackgroundScanEnabledForSession(false);

            // ── 阶段①：首次扫描锁定 ──
            var scanLock = Stopwatch.StartNew();
            try
            {
                var selector = candidate is null
                    ? null
                    : new RealCliCandidateSelector(candidate);
                await orchestrator.RunQuickScanAsync(selector);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return BuildCandidateRangeFailure(
                    orchestrator, overlay, imagePath, candidate,
                    ex.Message, sw.Elapsed.TotalMilliseconds);
            }
            scanLock.Stop();

            var lockStatus = ClassifyLock(orchestrator);
            var candidateChoices = SessionResultBuilder.BuildCandidateChoices(orchestrator);
            if (lockStatus == LockState.NoLock)
            {
                return BuildLockFailure(
                    orchestrator, overlay, imagePath, lockStatus, candidate,
                    candidateChoices, scanLock.Elapsed.TotalMilliseconds,
                    sw.Elapsed.TotalMilliseconds);
            }

            // ── 阶段②：关图 → 重开（同步外部游戏地图状态）──
            orchestrator.SynchronizeExternalGameMapState(false);
            orchestrator.SynchronizeExternalGameMapState(true);

            // ── 阶段③：仅对齐（不扫描识别）──
            var alignment = Stopwatch.StartNew();
            await orchestrator.RunAlignmentAsync();
            alignment.Stop();

            var timings = orchestrator.LastAlignmentPhaseTimings?
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var alignmentSucceeded =
                timings is not null
                && orchestrator.LastRecognition?.Result.OverlayTransform is not null;

            var result = BuildAlignmentResult(
                orchestrator, overlay, imagePath, lockStatus, candidate,
                candidateChoices, scanLock.Elapsed.TotalMilliseconds,
                alignment.Elapsed.TotalMilliseconds, timings,
                alignmentSucceeded, sw.Elapsed.TotalMilliseconds, null);
            await orchestrator.EndMatchAsync();
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RealCliSessionResult
            {
                ImagePath = imagePath,
                Succeeded = false,
                StatusMessage = $"Fatal 异常：{ex.Message}",
                FatalError = ex.ToString(),
                TotalWallMs = sw.Elapsed.TotalMilliseconds
            };
        }
        finally
        {
            await orchestrator.DisposeAsync();
        }
    }

    private static LockState ClassifyLock(SessionOrchestrator orchestrator)
    {
        var rec = orchestrator.LastRecognition;
        if (rec is not null)
            return rec.Result.OverlayTransform is not null
                ? LockState.FullLock
                : LockState.LockNoTransform;
        return orchestrator.PendingAlignmentIdentity is not null
            ? LockState.IdentityOnly
            : LockState.NoLock;
    }

    private static string ToName(LockState state) => state switch
    {
        LockState.FullLock => "FullLock",
        LockState.LockNoTransform => "LockNoTransform",
        LockState.IdentityOnly => "IdentityOnly",
        _ => "NoLock"
    };

    private static RealCliSessionResult BuildAlignmentResult(
        SessionOrchestrator orchestrator,
        RecordingOverlayWindow overlay,
        string imagePath,
        LockState lockStatus,
        int? candidate,
        List<RealCliCandidateChoiceOutput>? candidateChoices,
        double scanLockMs,
        double alignmentMs,
        Dictionary<string, double>? timings,
        bool alignmentSucceeded,
        double totalMs,
        string? error) => new()
    {
        ImagePath = imagePath,
        Succeeded = alignmentSucceeded,
        StatusMessage = orchestrator.StatusMessage,
        Recognition = SessionResultBuilder.BuildRecognition(orchestrator),
        FailureReason = alignmentSucceeded
            ? null
            : (orchestrator.StatusMessage ?? "仅对齐未产出有效变换"),
        BackgroundScanStatus = orchestrator.BackgroundScanStatus.ToString(),
        IsBackgroundScanCompleted = orchestrator.IsBackgroundScanCompleted,
        OverlayEvents = overlay.Events.ToList(),
        AlignmentSession = SessionResultBuilder.BuildAlignmentSession(orchestrator),
        ScanPhaseTimings = orchestrator.LastScanPhaseTimings?
            .ToDictionary(kv => kv.Key, kv => kv.Value),
        Diagnostics = SessionResultBuilder.BuildDiagnostics(orchestrator),
        LogEntries = SessionResultBuilder.BuildLogEntries(orchestrator),
        TotalWallMs = totalMs,
        FatalError = error,
        AlignmentPhaseTimings = timings,
        ScanLockWallMs = scanLockMs,
        AlignmentWallMs = alignmentMs,
        LockStatus = ToName(lockStatus),
        AlignmentSucceeded = alignmentSucceeded,
        RequestedCandidate = candidate,
        CandidateCount = candidateChoices?.Count ?? 0,
        CandidateChoices = candidateChoices
    };

    private static RealCliSessionResult BuildCandidateRangeFailure(
        SessionOrchestrator orchestrator,
        RecordingOverlayWindow overlay,
        string imagePath,
        int? candidate,
        string message,
        double totalMs)
    {
        var choices = SessionResultBuilder.BuildCandidateChoices(orchestrator);
        return new RealCliSessionResult
        {
            ImagePath = imagePath,
            Succeeded = false,
            StatusMessage = $"候选位置越界：--candidate {candidate} 超出候选范围（共 {choices?.Count ?? 0} 个）。",
            FailureReason = message,
            BackgroundScanStatus = orchestrator.BackgroundScanStatus.ToString(),
            IsBackgroundScanCompleted = orchestrator.IsBackgroundScanCompleted,
            OverlayEvents = overlay.Events.ToList(),
            Diagnostics = SessionResultBuilder.BuildDiagnostics(orchestrator),
            LogEntries = SessionResultBuilder.BuildLogEntries(orchestrator),
            TotalWallMs = totalMs,
            LockStatus = "NoLock",
            AlignmentSucceeded = false,
            RequestedCandidate = candidate,
            CandidateCount = choices?.Count ?? 0,
            CandidateChoices = choices
        };
    }

    private static RealCliSessionResult BuildLockFailure(
        SessionOrchestrator orchestrator,
        RecordingOverlayWindow overlay,
        string imagePath,
        LockState lockStatus,
        int? candidate,
        List<RealCliCandidateChoiceOutput>? candidateChoices,
        double scanLockMs,
        double totalMs)
    {
        var status = lockStatus == LockState.NoLock
            ? orchestrator.StatusMessage ?? "无法锁定地图"
            : "锁定未达到完整变换";
        return new RealCliSessionResult
        {
            ImagePath = imagePath,
            Succeeded = false,
            StatusMessage = status,
            FailureReason = status,
            BackgroundScanStatus = orchestrator.BackgroundScanStatus.ToString(),
            IsBackgroundScanCompleted = orchestrator.IsBackgroundScanCompleted,
            OverlayEvents = overlay.Events.ToList(),
            Recognition = SessionResultBuilder.BuildRecognition(orchestrator),
            Diagnostics = SessionResultBuilder.BuildDiagnostics(orchestrator),
            AlignmentSession = SessionResultBuilder.BuildAlignmentSession(orchestrator),
            LogEntries = SessionResultBuilder.BuildLogEntries(orchestrator),
            TotalWallMs = totalMs,
            AlignmentPhaseTimings = null,
            ScanLockWallMs = scanLockMs,
            AlignmentWallMs = 0,
            LockStatus = ToName(lockStatus),
            AlignmentSucceeded = false,
            RequestedCandidate = candidate,
            CandidateCount = candidateChoices?.Count ?? 0,
            CandidateChoices = candidateChoices
        };
    }
}
