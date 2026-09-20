using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>
/// VPSG 3.0 降级通知分类与提示策略。
/// 严格区分资源缺失（橙色警告）、服务故障（红色错误）与正常单帧算法拒识（静默）。
/// </summary>
public static class Vpsg3DegradationNotificationPolicy
{
    public const string MissingPrebuiltMessage = "没有可用的预制线图，自动降级至可用算法。";
    public const string CoreServiceFailureMessage = "VPSG 3.0 路由失效，核心服务不可用。已自动降级至早期算法。";

    /// <summary>
    /// 根据诊断状态及因果链判断是否需要触发用户级通知。
    /// </summary>
    /// <param name="status">当前或上游诊断状态。</param>
    /// <param name="isWarning">是否属于橙色警告（true 为警告，false 为错误）。</param>
    /// <param name="message">弹出的通知提示文案。</param>
    /// <param name="category">用于防抖去重的类别关键字。</param>
    /// <returns>若需要触发通知返回 true；若应保持静默返回 false。</returns>
    public static bool TryClassifyNotification(
        IdvbStatus? status,
        out bool isWarning,
        out string? message,
        out string? category)
    {
        isWarning = false;
        message = null;
        category = null;

        if (status is null) return false;

        for (var current = status; current is not null; current = current.Cause)
        {
            if (string.Equals(current.ReasonPhrase, "Vpsg3PrebuiltMissing", StringComparison.Ordinal))
            {
                isWarning = true;
                message = MissingPrebuiltMessage;
                category = "prebuilt-missing";
                return true;
            }

            if (string.Equals(current.ReasonPhrase, "Vpsg3CoreServiceFailed", StringComparison.Ordinal))
            {
                isWarning = false;
                message = CoreServiceFailureMessage;
                category = "core-failed";
                return true;
            }
        }

        return false;
    }
}
