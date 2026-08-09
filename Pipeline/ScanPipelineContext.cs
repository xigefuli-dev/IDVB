// IDVB Remaster Phase 3.1 — Scan Pipeline Context

using IDVBuff.Core.Models;
using OpenCvSharp;

namespace IDVBuff.Pipeline;

/// <summary>
/// 扫描管线的上下文 — 携带扫描流程各阶段的输入输出。
/// 注意：部分属性使用 object 类型以桥接 Core/Pipeline 与主项目之间的类型边界。
/// 这些 object 属性由 SessionOrchestrator（主项目侧）在管线启动前填充真实类型，
/// Stage 仅负责透传，不做类型假设。
/// </summary>
public sealed class ScanPipelineContext : PipelineContext
{
    /// <summary>游戏截图路径。</summary>
    public string? ScreenshotPath { get; set; }

    /// <summary>视口裁剪后的图像（OpenCV Mat）。</summary>
    public Mat? ViewportImage { get; set; }

    // ── 桥接属性：由主项目侧的 SessionOrchestrator 填充真实类型 ──

    /// <summary>
    /// 视口屏幕坐标（桥接：主项目侧填充 MapScreenRect 实例）。
    /// GateDetectStage / MapIdentifyStage 将此透传给 IGateDetector / IMapIdentifier。
    /// </summary>
    public object? ViewportBoundsRaw { get; set; }

    /// <summary>
    /// 地图几何指纹列表（桥接：主项目侧填充 IReadOnlyList&lt;MapGeometryFingerprint&gt; 实例）。
    /// MapIdentifyStage 将此透传给 IMapIdentifier.RankGeometry()。
    /// </summary>
    public object? FingerprintsRaw { get; set; }

    /// <summary>门检测耗时（毫秒）。</summary>
    public double GateDetectMs => GetPhaseMs("gate_detect");

    /// <summary>几何排名耗时（毫秒）。</summary>
    public double GeometryRankMs => GetPhaseMs("geometry_rank");

    // ── 类型安全的 Core 模型 ──

    /// <summary>检测到的门列表（Core 模型，由 GateDetectStage 填充）。</summary>
    public List<GateDetection> DetectedGates { get; init; } = [];

    /// <summary>候选列表。</summary>
    public List<MapCandidate> Candidates { get; set; } = [];

    /// <summary>选中的候选（Top-1 或用户选择）。</summary>
    public MapCandidate? SelectedCandidate { get; set; }

    /// <summary>扫描结果。</summary>
    public ScanResult? Result { get; set; }

    /// <summary>是否需要显示候选图选择界面。</summary>
    public bool NeedsUserSelection { get; set; }

    /// <summary>扫描阶段使用的门模板阈值，由运行时设置注入。</summary>
    public double GateTemplateThreshold { get; set; } = 0.6d;

    /// <summary>捕获窗口的客户端宽度，用于把门模板尺度换算到当前窗口。</summary>
    public double ClientWidth { get; set; } = 1920d;

    /// <summary>跳过楼层识别（适用手动指定楼层或已知固定楼层的场景）。</summary>
    public bool SkipFloorDetection { get; set; }
}
