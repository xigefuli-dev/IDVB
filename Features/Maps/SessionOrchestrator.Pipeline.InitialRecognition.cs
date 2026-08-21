// IDVB Remaster — Session Orchestrator 识别管线

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private sealed class InitialRecognitionPipelineState
    {
        public RuntimeMapRecognition? Recognition;
        public string? FailureReason;
        public IReadOnlyList<MapRecognitionChoice>? PendingChoices;
        public string PendingChoicesReason = string.Empty;
        public MapAlignmentSession? PendingSideEntranceSeed;
        public RuntimeMapRecognition? PendingSideEntranceIdentity;
        public SideEntranceScanResult? PendingSideEntranceScan;
        public Dictionary<Guid, MapFeatureCacheKey> RepairCacheKeys = new();
        public bool ScanSucceeded;
    }

    private void RunInitialRecognition(
        CapturedGameFrame frame,
        InitialRecognitionPipelineState result,
        bool recognizeOnly = false)
    {
        // 侧门识别「识别即对齐」：识别阶段的结构验证对齐（确认候选图）是
        // 侧门身份确认的必需步骤，后台扫描（recognizeOnly）同样走此链路，
        // 只是收尾不提交可靠会话、不锁定最终识别（见 SideEntrance.cs）。
        if (_settings!.FirstScanStrategy == FirstScanStrategy.SideEntrance)
        {
            RunInitialSideEntranceRecognition(frame, result, recognizeOnly);
            return;
        }

        RunInitialDefaultRecognition(frame, result, recognizeOnly);
    }

}
/*
 * 文件职责：SessionOrchestrator.Pipeline.InitialRecognition。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
