// IDVB 强类型诊断异常

namespace IDVBuff.Core.Diagnostics;

/// <summary>
/// 承载 <see cref="IdvbStatus"/> 的统一诊断异常。
/// </summary>
public sealed class IdvbException : Exception
{
    public IdvbStatus Status { get; }

    public IdvbException(IdvbStatus status)
        : base(status.UserMessage, status.Exception)
    {
        Status = status;
    }

    public IdvbException(IdvbStatus status, string message)
        : base(message, status.Exception)
    {
        Status = status;
    }

    public override string ToString() =>
        $"IdvbException: {Status.ToTraceString()}" + (InnerException is not null ? $"{Environment.NewLine}Inner: {InnerException}" : string.Empty);
}
