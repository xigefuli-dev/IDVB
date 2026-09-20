using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.MapAlignment.Probe.Pipeline;

/// <summary>
/// CLI 专用管线上下文，承载跨阶段共享的数据。
/// 各 PipelineStrategy 从此上下文读取输入、写回结果。
/// </summary>
public sealed class ProbeContext
{
    /// <summary>输入图片文件路径。</summary>
    public string ImagePath { get; init; } = string.Empty;

    /// <summary>加载后的图像（由策略负责 Dispose）。</summary>
    public Mat? Image { get; set; }

    /// <summary>裁剪后的截图（使用视口裁剪，由策略负责 Dispose）。</summary>
    public Mat? Screenshot { get; set; }

    /// <summary>截图视口（在截图坐标系内）。</summary>
    public MapScreenRect Viewport { get; set; }

    /// <summary>使用全帧模式（不裁剪视口）。</summary>
    public bool UseFullFrame { get; set; }

    /// <summary>归一化视口区域（从 settings 或 --viewport 读入）。</summary>
    public NormalizedRectangle? ViewportRegion { get; set; }

    /// <summary>视口边缘膨胀比例。</summary>
    public double ViewportMargin { get; set; } = 0.20;

    /// <summary>客户端宽度（用于反归一化检测结果）。</summary>
    public double ClientWidth { get; set; } = 2560d;

    /// <summary>门模板路径。</summary>
    public string GateTemplatePath { get; set; } = string.Empty;

    /// <summary>门检测阈值。</summary>
    public double GateThreshold { get; set; } = MapRecognitionTuning.DefaultGateTemplateThreshold;

    /// <summary>是否启用结构配准。</summary>
    public bool EnableStructure { get; set; }

    /// <summary>是否启用 ECC 精修。</summary>
    public bool EnableEcc { get; set; }

    /// <summary>是否强制最佳候选。</summary>
    public bool ForceBestCandidate { get; set; }

    /// <summary>排名 top N。</summary>
    public int TopCount { get; set; } = 1;

    /// <summary>结构配准 top 候选数。</summary>
    public int TopCandidates { get; set; } = 6;

    /// <summary>降采样因子（结构配准）。</summary>
    public double DownscaleFactor { get; set; } = 0.5;

    /// <summary>settings.json 路径（可选）。</summary>
    public string? SettingsPath { get; set; }

    /// <summary>楼层模板路径 (1F)。</summary>
    public string FirstFloorTemplatePath { get; set; } = string.Empty;

    /// <summary>楼层模板路径 (2F)。</summary>
    public string SecondFloorTemplatePath { get; set; } = string.Empty;

    /// <summary>侧门扫描 top N。</summary>
    public int SideScanTop { get; set; } = 10;

    /// <summary>输出 JSON 路径（可选）。</summary>
    public string? OutputPath { get; set; }

    /// <summary>结构填充单图输出路径。</summary>
    public string? StructureFillOutputPath { get; set; }

    /// <summary>结构填充批处理输出目录。</summary>
    public string? StructureFillOutputDirectory { get; set; }

    /// <summary>Use the guide-map tone profile before structure extraction.</summary>
    public bool StructureFillGuideMap { get; set; }

    /// <summary>侧门扫描指定地图 ID（可空，为空扫描全部）。</summary>
    public Guid? SideScanMapId { get; set; }

    /// <summary>批量评估配置。</summary>
    public BatchOptions? Batch { get; set; }
}

public sealed class BatchOptions
{
    public string Glob { get; init; } = string.Empty;
    public int Parallelism { get; init; } = 1;
    public string? OutputPath { get; init; }
}
