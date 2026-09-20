// IDVB 统一诊断状态码定义
// 遵循类 HTTP 语义：
//   1xx Informational (前置协商/准备中)
//   2xx Success (确定性成功)
//   3xx Redirection / Fallback (显式降级回退，因果溯源核心！)
//   4xx Client / Environment Error (前置依赖不满足、环境/参数/视觉无效)
//   5xx Server / Internal Error (算法崩溃、未捕获异常、超时熔断)

namespace IDVBuff.Core.Diagnostics;

/// <summary>
/// HTTP 风格的主状态码分类常量。
/// </summary>
public static class IdvbHttpCode
{
    // 1xx: Informational
    public const int Continue = 100;
    public const int Processing = 102;
    public const int ShadowEvaluating = 110;

    // 2xx: Success
    public const int Ok = 200;
    public const int DualGateAligned = 201;
    public const int Vpsg3Aligned = 202;
    public const int StructureAligned = 203;
    public const int CachedStateReused = 204;
    public const int IdentityOnlyLocked = 206;

    // 3xx: Redirection / Fallback (显式降级，绝不静默)
    public const int FallbackTriggered = 300;
    public const int Vpsg3FallbackToLegacy = 301;
    public const int CacheExpiredFallback = 302;
    public const int ViewportNotChanged = 304;

    // 4xx: Client / Environment / Operational
    public const int BadRequest = 400;
    public const int GameNotForeground = 401;
    public const int FeatureDisabled = 403;
    public const int MapNotFound = 404;
    public const int CaptureTimeout = 408;
    public const int StateConflict = 409;
    public const int PreconditionFailed = 412;
    public const int UnprocessableVisual = 422;
    public const int RateLimited = 429;

    // 5xx: Server / Pipeline Internal
    public const int InternalError = 500;
    public const int NotImplemented = 501;
    public const int SidecarFailed = 502;
    public const int ServiceUnavailable = 503;
    public const int PipelineTimeout = 504;
    public const int OutOfMemory = 507;
}

/// <summary>
/// 细分诊断子码 (SubCode)，格式为 5 位整数：
/// [模块前缀(1位)][大类(3位)][细分(1-2位)]
/// 模块段分配：
///   1xxxx: 捕获与视口 (Capture)
///   2xxxx: 地图目录与先验识别 (Catalog / Floor / Prior)
///   3xxxx: 结构配准与对齐 VPSG (Structure Registration / VPSG3)
///   4xxxx: 跟踪与会话状态 (Tracking / Session)
///   5xxxx: 测绘与 IDVM (Survey / PoseGraph)
///   6xxxx: 插件与扩展运行时 (Plugin)
///   7xxxx: 升级与分发 (Updater)
/// </summary>
public static class IdvbSubCode
{
    // --- 1xxxx: 捕获模块 ---
    public const int CaptureWindowNotFound = 14041;
    public const int CaptureWindowMinimized = 14121;
    public const int CaptureWindowOccluded = 14122;
    public const int CaptureTimeout = 14081;
    public const int CaptureDirect3dFailed = 15001;

    // --- 2xxxx: 目录与初识模块 ---
    public const int MapCatalogEmpty = 24041;
    public const int MapTemplateNotFound = 24042;
    public const int MapAmbiguousCandidates = 24091;
    public const int FloorKeyNotMatched = 24043;
    public const int MapLockedIdentityAwaitingAlignment = 22061;

    // --- 3xxxx: 结构配准与 VPSG 模块 (核心重点) ---
    public const int StructureOk = 32001;
    public const int StructureVpsg3Ok = 32021;
    public const int StructureDualGateOk = 32011;

    // 3xx 降级类 (新特性放弃并交接给传统老算法)
    public const int Vpsg3FallbackDegraded = 33011;
    public const int Vpsg3FallbackApertureMarginLow = 33012;
    public const int Vpsg3FallbackNotConverged = 33013;
    public const int Vpsg3FallbackIndexNotReady = 33014;
    public const int StructureScaleCacheExpired = 33021;

    // 4xx 视觉不可达/质量不达标
    public const int StructureInsufficientEdges = 34221;
    public const int StructureWeakAbsoluteScore = 34222;
    public const int StructureCandidateMarginLow = 34223;
    public const int StructureScaleOutOFRange = 34224;
    public const int StructureEccNotConverged = 34225;
    public const int StructureLowGeometricConfidence = 34226;

    // 5xx 算子内部崩溃
    public const int StructureCvException = 35001;
    public const int StructureTimeBudgetExceeded = 35041;

    // --- 4xxxx: 跟踪与会话模块 ---
    public const int TrackingSteadyReused = 42041;
    public const int TrackingLost = 44041;
    public const int TrackingDiverged = 44091;

    // --- 5xxxx: 测绘与 IDVM 模块 ---
    public const int IdvmCorrupted = 54221;
    public const int IdvmVersionMismatch = 54091;

    // --- 6xxxx: 插件与扩展 ---
    public const int PluginCrash = 65001;

    // --- 7xxxx: 升级模块 ---
    public const int UpdateSignatureInvalid = 74011;
    public const int UpdatePayloadCorrupted = 74221;
}
