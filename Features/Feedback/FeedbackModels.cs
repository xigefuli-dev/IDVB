namespace IDVBuff.Features.Feedback;

/// <summary>
/// 反馈提交的数据载荷。
/// </summary>
public sealed class FeedbackSubmissionPayload
{
    /// <summary>问题描述（必须填写且加权字符数大于 10）。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>是否包含日志数据。</summary>
    public bool IncludeLogs { get; set; }

    /// <summary>日志数据打包生成的 ZIP 文件路径（若未生成则为 null）。</summary>
    public string? LogsZipPath { get; set; }

    /// <summary>日志 ZIP 字节大小。</summary>
    public long LogsZipSizeBytes { get; set; }

    /// <summary>是否包含诊断数据。</summary>
    public bool IncludeDiagnostics { get; set; }

    /// <summary>诊断数据打包生成的 ZIP 文件路径（若未生成则为 null）。</summary>
    public string? DiagnosticsZipPath { get; set; }

    /// <summary>诊断数据 ZIP 字节大小。</summary>
    public long DiagnosticsZipSizeBytes { get; set; }

    /// <summary>创建时间戳。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 反馈提交的结果。
/// </summary>
public sealed class FeedbackSubmissionResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public Exception? Error { get; set; }

    public static FeedbackSubmissionResult Ok(string message = "反馈提交成功") =>
        new() { Success = true, Message = message };

    public static FeedbackSubmissionResult Fail(string message, Exception? error = null) =>
        new() { Success = false, Message = message, Error = error };
}
