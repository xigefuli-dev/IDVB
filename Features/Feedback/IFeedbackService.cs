namespace IDVBuff.Features.Feedback;

/// <summary>
/// 反馈服务接口。
/// </summary>
public interface IFeedbackService
{
    /// <summary>
    /// 提交用户反馈（包含问题描述与打包的日志、诊断数据包）。
    /// </summary>
    Task<FeedbackSubmissionResult> SubmitFeedbackAsync(
        FeedbackSubmissionPayload payload,
        CancellationToken cancellationToken = default);
}
