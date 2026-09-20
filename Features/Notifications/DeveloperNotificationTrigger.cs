using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// 开发者通知测试触发器。
/// 供开发者模式与测试入口调用，用于验证错误/警告/通知三种配色、要点 A 换行高度拉伸、蓝色进度条与动画合成重定向。
/// </summary>
public static class DeveloperNotificationTrigger
{
    private static int _errorIndex;
    private static int _warnIndex;
    private static int _noticeIndex;

    /// <summary>
    /// 触发错误通知测试（红色系，带底部蓝色进度条并模拟进度增长，默认 5 秒后自动消失）。
    /// </summary>
    public static IOverlayNotification TriggerError()
    {
        var count = Interlocked.Increment(ref _errorIndex);
        var handle = OverlayNotificationCenter.Error(
            $"[错误 #{count}] 图像配准校验失败，请检查地图文件或视口范围",
            progress: 0.20);

        _ = Task.Run(async () =>
        {
            for (int p = 35; p <= 100; p += 15)
            {
                await Task.Delay(350).ConfigureAwait(false);
                if (handle.IsDismissed) break;
                handle.UpdateProgress(
                    p / 100.0,
                    p < 100
                        ? $"[错误 #{count}] 正在重试特征搜寻 ({p}%)..."
                        : $"[错误 #{count}] 错误恢复流程已完成");
            }
        });

        return handle;
    }

    /// <summary>
    /// 触发警告通知测试（橙色系，长文本自动换行测试要点 A 卡片高度自适应变大，默认 5 秒后自动消失）。
    /// </summary>
    public static IOverlayNotification TriggerWarning()
    {
        var count = Interlocked.Increment(ref _warnIndex);
        return OverlayNotificationCenter.Warning(
            $"[警告 #{count}] 结构置信度低于安全阈值（要点A测试：这是一段较长的测试文本，换行会导致通知框自身自适应变大，队列其他卡片自动合成下移）",
            progress: 0.65);
    }

    /// <summary>
    /// 触发普通通知测试（绿色系，标准单行状态提示，默认 5 秒后自动消失）。
    /// </summary>
    public static IOverlayNotification TriggerNotice()
    {
        var count = Interlocked.Increment(ref _noticeIndex);
        return OverlayNotificationCenter.Notice(
            $"[通知 #{count}] 已成功匹配军工厂 1F，小地图定位已就绪。");
    }
}
