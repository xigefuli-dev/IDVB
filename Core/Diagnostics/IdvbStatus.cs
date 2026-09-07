// IDVB 强类型诊断状态对象
// 支持 HTTP 风格状态码、细分子码、用户友好文本、技术细节、异常对象与因果溯源链 (Cause Chain)。

using System.Text;

namespace IDVBuff.Core.Diagnostics;

/// <summary>
/// 强类型诊断状态对象。承载所有操作的成功、降级、客户端/环境错误及内部异常。
/// </summary>
public sealed record IdvbStatus
{
    /// <summary>HTTP 风格的主状态分类码（1xx / 2xx / 3xx / 4xx / 5xx）。</summary>
    public int Code { get; init; } = IdvbHttpCode.Ok;

    /// <summary>细分诊断子码（如 33013）。</summary>
    public int SubCode { get; init; }

    /// <summary>机读短语标识（如 "Vpsg3MarginInsufficient"）。</summary>
    public string ReasonPhrase { get; init; } = "OK";

    /// <summary>用户可见的友好中文说明（用于 UI 弹窗、状态栏提示）。</summary>
    public string UserMessage { get; init; } = string.Empty;

    /// <summary>可选的技术参数或诊断细节（如数值阈值、尺寸、坐标等）。</summary>
    public string? TechnicalDetail { get; init; }

    /// <summary>发生该状态的具体组件或阶段（如 "Vpsg3.FastBootstrap"）。</summary>
    public string? Stage { get; init; }

    /// <summary>捕获的内部异常堆栈（5xx 错误时填充）。</summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// 导致当前状态的上游因果源头（关键核心！记录为什么降级、新特性为什么放弃）。
    /// </summary>
    public IdvbStatus? Cause { get; init; }

    /// <summary>是否属于成功状态（2xx）。</summary>
    public bool IsSuccess => Code is >= 200 and < 300;

    /// <summary>是否属于降级/重试状态（3xx）。</summary>
    public bool IsFallback => Code is >= 300 and < 400;

    /// <summary>是否属于客户端/环境/前置依赖错误（4xx）。</summary>
    public bool IsClientError => Code is >= 400 and < 500;

    /// <summary>是否属于内部未捕获或算子内部严重异常（5xx）。</summary>
    public bool IsServerError => Code >= 500;

    /// <summary>
    /// 快速构建成功状态 (200 OK)。
    /// </summary>
    public static IdvbStatus Ok(string userMessage = "操作成功", int subCode = 0, string? stage = null) => new()
    {
        Code = IdvbHttpCode.Ok,
        SubCode = subCode,
        ReasonPhrase = "OK",
        UserMessage = userMessage,
        Stage = stage
    };

    /// <summary>
    /// 快速构建显式降级状态 (3xx Fallback)。绝不静默！
    /// </summary>
    public static IdvbStatus Fallback(int code, int subCode, string reasonPhrase, string userMessage, string? technicalDetail = null, string? stage = null, IdvbStatus? cause = null) => new()
    {
        Code = code,
        SubCode = subCode,
        ReasonPhrase = reasonPhrase,
        UserMessage = userMessage,
        TechnicalDetail = technicalDetail,
        Stage = stage,
        Cause = cause
    };

    /// <summary>
    /// 快速构建客户端/环境错误 (4xx ClientError)。
    /// </summary>
    public static IdvbStatus ClientError(int code, int subCode, string reasonPhrase, string userMessage, string? technicalDetail = null, string? stage = null, IdvbStatus? cause = null) => new()
    {
        Code = code,
        SubCode = subCode,
        ReasonPhrase = reasonPhrase,
        UserMessage = userMessage,
        TechnicalDetail = technicalDetail,
        Stage = stage,
        Cause = cause
    };

    /// <summary>
    /// 全局未捕获异常或严重故障转换 (5xx InternalError)。
    /// </summary>
    public static IdvbStatus FromException(Exception ex, string stage, int subCode = 50000, string? customMessage = null) => new()
    {
        Code = IdvbHttpCode.InternalError,
        SubCode = subCode,
        ReasonPhrase = "InternalError",
        UserMessage = customMessage ?? $"内部执行异常：{ex.GetType().Name}",
        TechnicalDetail = ex.Message,
        Stage = stage,
        Exception = ex
    };

    /// <summary>
    /// 递归获取整条因果溯源链文本描述，彻底暴露新特性死因。
    /// 格式：[Code:Reason] UserMessage (TechDetail) <- CausedBy: [SubCode:Reason] ...
    /// </summary>
    public string ToTraceString()
    {
        var sb = new StringBuilder();
        AppendTrace(sb);
        return sb.ToString();
    }

    private void AppendTrace(StringBuilder sb)
    {
        sb.Append('[').Append(Code);
        if (SubCode > 0)
        {
            sb.Append(':').Append(SubCode);
        }
        sb.Append(' ').Append(ReasonPhrase).Append("] ");
        sb.Append(UserMessage);

        if (!string.IsNullOrWhiteSpace(TechnicalDetail))
        {
            sb.Append(" (").Append(TechnicalDetail).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(Stage))
        {
            sb.Append(" @").Append(Stage);
        }

        if (Cause is not null)
        {
            sb.Append(" <- CausedBy: ");
            Cause.AppendTrace(sb);
        }
    }

    public override string ToString() => ToTraceString();
}
