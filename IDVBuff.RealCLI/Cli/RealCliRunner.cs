// IDVB Real CLI — 会话运行驱动器（RealCliRunner）
//
// 驱动 SessionOrchestrator 完整生命周期（Initialize -> BeginMatch -> RunQuickScan -> EndMatch）

using System.Diagnostics;
using IDVBuff.Features.Maps;
using IDVBuff.RealCLI.Output;
using IDVBuff.RealCLI.Stubs;

namespace IDVBuff.RealCLI.Cli;

internal static class RealCliRunner
{
    public static async Task<RealCliSessionResult> RunRecognitionAsync(
        SessionOrchestrator orchestrator,
        RecordingOverlayWindow overlay,
        string imagePath,
        bool consume = false,
        string? expectedMap = null,
        string? mapClass = null,
        bool prewarmVpsg3 = false,
        TimeSpan? prewarmTimeout = null)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // 初始化：加载设置、预热缓存、检查完整性
            await orchestrator.InitializeAsync();

            // 确保收集结构化诊断日志，供 RealCLI 分析管线执行路径
            await orchestrator.SetCollectLogsAsync(true);
            orchestrator.LogCollector.IsEnabled = true;

            if (prewarmVpsg3)
            {
                var timeout = prewarmTimeout ?? TimeSpan.FromSeconds(15);
                var ready = await orchestrator.WaitForVpsg3PreparedAsync(timeout);
                if (!ready)
                {
                    Console.Error.WriteLine($"[警告] VPSG 3.0 索引就绪等待超时({timeout.TotalSeconds:F0}s)，继续执行扫描...");
                }
            }

            // Real CLI 也遵循产品生命周期：扫描必须发生在进入对局之后。
            var targetMapClass = mapClass
                ?? orchestrator.Settings.LastSelectedMapClass
                ?? "S0 厄运之女 · 噩梦（爱吃醋）";
            await orchestrator.BeginMatchAsync(targetMapClass);

            await orchestrator.RunQuickScanAsync();

            if (consume)
            {
                orchestrator.SynchronizeExternalGameMapState(true);
                await orchestrator.ConsumeBackgroundScanAsync();
            }

            sw.Stop();

            // 收集结果
            var result = SessionResultBuilder.ExtractResult(
                orchestrator,
                overlay,
                imagePath,
                sw.Elapsed.TotalMilliseconds,
                null,
                expectedMap);

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
                TotalWallMs = sw.Elapsed.TotalMilliseconds,
                VerificationResult = "Unrecognized",
                IsMatchCorrect = false
            };
        }
        finally
        {
            await orchestrator.DisposeAsync();
        }
    }
}
