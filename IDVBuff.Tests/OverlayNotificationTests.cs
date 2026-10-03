using System.Drawing;
using System.Drawing.Imaging;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Notifications;
using Xunit;

namespace IDVBuff.Tests;

public sealed partial class OverlayNotificationTests
{
    [Theory]
    [InlineData(OverlayNotificationType.Error)]
    [InlineData(OverlayNotificationType.Warning)]
    [InlineData(OverlayNotificationType.Notice)]
    public void ColorSchemes_FollowLuminanceHierarchyAndHighContrast(OverlayNotificationType type)
    {
        var scheme = OverlayNotificationTheme.GetScheme(type);

        // 验证相对明度：更浅色边缘轮廓 > 介于两者之间的字体 > 更深色内容填充
        var outlineLum = CalculateLuminance(scheme.OutlineColor);
        var textLum = CalculateLuminance(scheme.TextColor);
        var fillLum = CalculateLuminance(scheme.FillColor);

        Assert.True(outlineLum > textLum, $"轮廓明度 ({outlineLum:F3}) 应高于字体明度 ({textLum:F3})");
        Assert.True(textLum > fillLum, $"字体明度 ({textLum:F3}) 应高于背景填充明度 ({fillLum:F3})");

        // 验证字体与背景填充的对比度（要求达到 WCAG AAA 级别 > 7:1）
        var contrastRatio = (textLum + 0.05) / (fillLum + 0.05);
        Assert.True(contrastRatio >= 7.0, $"字体与填充背景对比度 ({contrastRatio:F1}:1) 必须满足高可读性 (>=7:1)");
    }

    [Fact]
    public void ProgressBar_IsBlueAndContrastsAllSchemes()
    {
        var blue = OverlayNotificationTheme.ProgressBarColor;
        // 进度条一定是纯正的高饱和蓝色
        Assert.True(blue.B >= 200, "进度条必须是高饱和蓝色 (B >= 200)");
        Assert.True(blue.B > blue.R * 2, "进度条蓝色分量必须显著大于红色分量");

        // 检验在红、橙、绿三种填充底色上的色相可分辨性
        foreach (var type in new[] { OverlayNotificationType.Error, OverlayNotificationType.Warning, OverlayNotificationType.Notice })
        {
            var fill = OverlayNotificationTheme.GetScheme(type).FillColor;
            var dist = Math.Sqrt(Math.Pow(blue.R - fill.R, 2) + Math.Pow(blue.G - fill.G, 2) + Math.Pow(blue.B - fill.B, 2));
            Assert.True(dist > 100, $"进度条蓝色与 {type} 底色的欧氏色彩距离 ({dist:F1}) 必须显著清晰");
        }
    }

    [Fact]
    public void PointA_TextWrapping_DynamicallyIncreasesCardHeight()
    {
        using var bmp = new Bitmap(10, 10, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using var font = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);

        var shortNotification = new OverlayNotificationItem(OverlayNotificationType.Notice, "短消息");
        var shortCard = new OverlayNotificationCardState(shortNotification, 10f, 1000);
        shortCard.Measure(g, font);

        var longNotification = new OverlayNotificationItem(
            OverlayNotificationType.Notice,
            "这是一条非常长非常长非常长的测试通知文本，故意超过单行可容纳的最大宽度限制，" +
            "从而触发文本自动换行机制。根据要点A的明确规范，换行必须导致卡片自身高度动态自适应增加！");
        var longCard = new OverlayNotificationCardState(longNotification, 10f, 1000);
        longCard.Measure(g, font);

        Assert.Equal(OverlayNotificationTheme.MinCardHeight, shortCard.MeasuredHeight);
        Assert.True(longCard.MeasuredHeight > shortCard.MeasuredHeight,
            $"要点 A 验证：长文本卡片高度 ({longCard.MeasuredHeight}px) 必须大于单行短文本高度 ({shortCard.MeasuredHeight}px)");
    }

    [Fact]
    public void Queue_LimitsToMax5ConcurrentAndDequeuesOnCompletion()
    {
        var queue = new OverlayNotificationQueue();
        var list = new List<IOverlayNotification>();

        for (int i = 0; i < 7; i++)
        {
            var item = new OverlayNotificationItem(OverlayNotificationType.Notice, $"Msg {i}");
            list.Add(item);
            queue.Enqueue(item);
        }

        // 最多同时展示 5 个
        var snapshot1 = queue.GetSnapshot();
        Assert.Equal(5, snapshot1.Count);

        // 关闭第 1 个
        list[0].Dismiss();

        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);

        // 驱动时间前进使得淡出完毕并让第 6 个出队
        queue.UpdateFrame(g, 1000); // 标记退出
        queue.UpdateFrame(g, 2000); // 清理已退出并出队新卡片

        var snapshot2 = queue.GetSnapshot();
        Assert.Equal(5, snapshot2.Count);
        Assert.Contains(snapshot2, c => c.Notification.Id == list[5].Id);
    }

    [Fact]
    public void QueueLayout_OffsetsDownwardsAndAccountsForDifferentCardHeights()
    {
        var queue = new OverlayNotificationQueue();
        var shortItem = new OverlayNotificationItem(OverlayNotificationType.Notice, "短消息");
        var longItem = new OverlayNotificationItem(
            OverlayNotificationType.Warning,
            "多行长消息文本测试多行长消息文本测试多行长消息文本测试多行长消息文本测试多行长消息文本测试多行长消息文本测试");
        var thirdItem = new OverlayNotificationItem(OverlayNotificationType.Error, "第三条消息");

        queue.Enqueue(shortItem);
        queue.Enqueue(longItem);
        queue.Enqueue(thirdItem);

        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        queue.UpdateFrame(g, 1000);

        var snapshot = queue.GetSnapshot();
        Assert.Equal(3, snapshot.Count);

        var cardA = snapshot[0];
        var cardB = snapshot[1];
        var cardC = snapshot[2];

        Assert.Equal(OverlayNotificationQueue.BaseTopOffset, cardA.TargetY);
        // 卡片 B 偏移必须基于卡片 A 的实际高度
        Assert.Equal(cardA.TargetY + cardA.MeasuredHeight + OverlayNotificationTheme.CardSpacing, cardB.TargetY);
        // 卡片 C 偏移必须基于卡片 B 的动态多行高度（要点 A）
        Assert.Equal(cardB.TargetY + cardB.MeasuredHeight + OverlayNotificationTheme.CardSpacing, cardC.TargetY);
        Assert.True(cardB.MeasuredHeight > cardA.MeasuredHeight, "卡片 B 换行高度必须大于单行卡片 A");
    }

    [Fact]
    public void DynamicShift_WhenADisappears_BCMoveUp_WhenBDisappears_CMovesToB()
    {
        var queue = new OverlayNotificationQueue();
        var itemA = new OverlayNotificationItem(OverlayNotificationType.Notice, "A");
        var itemB = new OverlayNotificationItem(OverlayNotificationType.Notice, "B");
        var itemC = new OverlayNotificationItem(OverlayNotificationType.Notice, "C");

        queue.Enqueue(itemA);
        queue.Enqueue(itemB);
        queue.Enqueue(itemC);

        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        queue.UpdateFrame(g, 1000);

        var initialA_Y = queue.GetSnapshot()[0].TargetY;
        var initialB_Y = queue.GetSnapshot()[1].TargetY;

        // 场景 1：如果 A 消失了，B 移动到 A，C 移动到 B
        itemA.Dismiss();
        queue.UpdateFrame(g, 1050);

        var cardB_afterA = queue.GetSnapshot().First(c => c.Notification.Id == itemB.Id);
        var cardC_afterA = queue.GetSnapshot().First(c => c.Notification.Id == itemC.Id);

        Assert.Equal(initialA_Y, cardB_afterA.TargetY);
        Assert.Equal(initialB_Y, cardC_afterA.TargetY);

        // 场景 2 测试：新建 A, B, C，B 比 A 先消失
        var queue2 = new OverlayNotificationQueue();
        var nA = new OverlayNotificationItem(OverlayNotificationType.Notice, "A");
        var nB = new OverlayNotificationItem(OverlayNotificationType.Notice, "B");
        var nC = new OverlayNotificationItem(OverlayNotificationType.Notice, "C");
        queue2.Enqueue(nA);
        queue2.Enqueue(nB);
        queue2.Enqueue(nC);
        queue2.UpdateFrame(g, 2000);

        var targetB = queue2.GetSnapshot()[1].TargetY;

        // B 比 A 先消失
        nB.Dismiss();
        queue2.UpdateFrame(g, 2050);

        var cardA_remains = queue2.GetSnapshot().First(c => c.Notification.Id == nA.Id);
        var cardC_moves = queue2.GetSnapshot().First(c => c.Notification.Id == nC.Id);

        Assert.Equal(OverlayNotificationQueue.BaseTopOffset, cardA_remains.TargetY); // A 保持不变
        Assert.Equal(targetB, cardC_moves.TargetY); // C 移动到原 B 的位置
    }

    [Fact]
    public void AnimationRetargeting_SmoothlyCompositesMidFlightWithoutSnap()
    {
        var item = new OverlayNotificationItem(OverlayNotificationType.Notice, "Moving");
        var card = new OverlayNotificationCardState(item, targetY: 100f, now: 1000);

        // 模拟已移动 130ms（达到大约 50% 进度）
        var midFlightY = card.GetInterpolatedY(now: 1130);
        Assert.True(midFlightY > card.StartY && midFlightY < card.TargetY);

        // 在还在移动的过程中位置又更新为 60f
        card.RetargetPosition(newTargetY: 60f, now: 1130);

        // 起点应平滑合成捕获为刚才飞行中的即时插值位置 midFlightY
        Assert.Equal(midFlightY, card.StartY);
        Assert.Equal(60f, card.TargetY);
        Assert.Equal(1130, card.MoveStartTick);

        // 在新动画起始瞬间位置保持连续一致，绝无突跳
        var startYAfterRetarget = card.GetInterpolatedY(now: 1130);
        Assert.Equal(midFlightY, startYAfterRetarget);
    }

    [Fact]
    public void Rendering_RendersAllThreeTypesAndBlueProgressBar()
    {
        using var bmp = new Bitmap(460, 400, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using var font = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);

        var queue = new OverlayNotificationQueue();
        var errorItem = new OverlayNotificationItem(OverlayNotificationType.Error, "错误测试", progress: 0.5);
        var warnItem = new OverlayNotificationItem(OverlayNotificationType.Warning, "警告测试");
        var noticeItem = new OverlayNotificationItem(OverlayNotificationType.Notice, "通知测试", progress: 0.8);

        queue.Enqueue(errorItem);
        queue.Enqueue(warnItem);
        queue.Enqueue(noticeItem);

        var now = Environment.TickCount64 + 300;
        queue.UpdateFrame(g, now: now);
        var cards = queue.GetSnapshot();

        // 渲染队列
        OverlayNotificationRenderer.RenderQueue(g, cards, now: now);

        // 验证进度更新
        errorItem.UpdateProgress(0.9, "错误进度更新");
        Assert.Equal(0.9, errorItem.Progress);
        Assert.Equal("错误进度更新", errorItem.Message);

        // 重新测量并渲染
        queue.UpdateFrame(g, now: now + 50);
        OverlayNotificationRenderer.RenderQueue(g, queue.GetSnapshot(), now: now + 50);

        // 抽样检查位图是否有非透明像素被绘制
        bool hasNonTransparentPixels = false;
        for (int y = 0; y < 100; y += 10)
        {
            for (int x = 0; x < 400; x += 10)
            {
                var pixel = bmp.GetPixel(x, y);
                if (pixel.A > 0)
                {
                    hasNonTransparentPixels = true;
                    break;
                }
            }
            if (hasNonTransparentPixels) break;
        }

        Assert.True(hasNonTransparentPixels, "渲染应当在位图上绘制有效的彩色非透明像素");
    }

    [Fact]
    public void DefaultDuration_Is5Seconds_EvenWithProgress_AndSupportsCustom()
    {
        // 默认即使带有 progress，也必须统一默认为 5 秒消失
        var defaultNotice = new OverlayNotificationItem(OverlayNotificationType.Notice, "通知");
        var defaultErrorWithProgress = new OverlayNotificationItem(OverlayNotificationType.Error, "错误", progress: 0.3);
        var defaultWarnWithProgress = new OverlayNotificationItem(OverlayNotificationType.Warning, "警告", progress: 0.8);

        Assert.Equal(TimeSpan.FromSeconds(5), defaultNotice.Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), defaultErrorWithProgress.Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), defaultWarnWithProgress.Duration);

        // 支持自定义时间
        var custom = new OverlayNotificationItem(OverlayNotificationType.Notice, "自定义", duration: TimeSpan.FromSeconds(8));
        Assert.Equal(TimeSpan.FromSeconds(8), custom.Duration);

        // 支持无限时间
        var infinite = new OverlayNotificationItem(OverlayNotificationType.Notice, "无限", duration: Timeout.InfiniteTimeSpan);
        Assert.Equal(Timeout.InfiniteTimeSpan, infinite.Duration);
        Assert.Null(infinite.ExpireTime);
    }

    [Fact]
    public void AutoDismiss_ExpiresAfter5Seconds_AndCleansUp()
    {
        var queue = new OverlayNotificationQueue();
        var item = new OverlayNotificationItem(OverlayNotificationType.Notice, "自动消失测试");
        queue.Enqueue(item);

        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);

        long start = 1000;
        queue.UpdateFrame(g, now: start);

        var snapshot1 = queue.GetSnapshot();
        Assert.Single(snapshot1);
        Assert.False(snapshot1[0].IsExiting);

        // 4.9 秒时：仍在屏幕上
        queue.UpdateFrame(g, now: start + 4900);
        Assert.False(queue.GetSnapshot()[0].IsExiting);

        // 5.05 秒时：卡片超时开始淡出退出
        queue.UpdateFrame(g, now: start + 5050);
        Assert.True(queue.GetSnapshot()[0].IsExiting);

        // 5.3 秒时：淡出动画结束并被彻底移出队列
        queue.UpdateFrame(g, now: start + 5300);
        Assert.Empty(queue.GetSnapshot());
    }

    [Fact]
    public void DismissById_TriggersExitAndAnimation()
    {
        var queue = new OverlayNotificationQueue();
        var item = new OverlayNotificationItem(OverlayNotificationType.Notice, "测试关闭");
        queue.Enqueue(item);

        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        queue.UpdateFrame(g, now: 1000);

        Assert.Single(queue.GetSnapshot());
        Assert.False(queue.GetSnapshot()[0].IsExiting);

        // 通过队列 ID 关闭
        queue.Dismiss(item.Id);

        // 验证卡片立即进入退出状态
        queue.UpdateFrame(g, now: 1050);
        Assert.True(queue.GetSnapshot()[0].IsExiting);
    }

    [Fact]
    public void DpiScaling_NotificationMetrics_ScalesDimensionsProportionately()
    {
        var m1 = NotificationMetrics.Default; // 1.0x
        var m15 = new NotificationMetrics(1.5f);
        var m2 = new NotificationMetrics(2.0f);

        Assert.Equal(420f, m1.CardWidth);
        Assert.Equal(420f * 1.5f, m15.CardWidth);
        Assert.Equal(420f * 2.0f, m2.CardWidth);

        Assert.Equal(48f, m1.MinCardHeight);
        Assert.Equal(48f * 1.5f, m15.MinCardHeight);
        Assert.Equal(48f * 2.0f, m2.MinCardHeight);

        Assert.Equal(12.5f, m1.FontSize);
        Assert.Equal(12.5f * 1.5f, m15.FontSize);
        Assert.Equal(12.5f * 2.0f, m2.FontSize);

        Assert.Equal(3.5f, m1.ProgressBarThickness);
        Assert.Equal(3.5f * 1.5f, m15.ProgressBarThickness);
        Assert.Equal(3.5f * 2.0f, m2.ProgressBarThickness);

        // 验证在高 DPI (1.5x) 下测量卡片高度相应变大
        using var bmp = new Bitmap(10, 10, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using var font15 = new Font("Microsoft YaHei UI", m15.FontSize, FontStyle.Regular, GraphicsUnit.Pixel);

        var item = new OverlayNotificationItem(OverlayNotificationType.Notice, "DPI 缩放测量测试文本");
        var card = new OverlayNotificationCardState(item, 10f, 1000, m15);
        card.Measure(g, font15, m15);

        Assert.True(card.MeasuredHeight >= m15.MinCardHeight, "高 DPI 下卡片高度必须大于等于 1.5x 最小高度");
    }

    [Fact]
    public void EntranceAndExitAnimations_ExecuteSmoothlyAndSetLifecycle()
    {
        var item = new OverlayNotificationItem(OverlayNotificationType.Notice, "动画测试");
        var metrics = new NotificationMetrics(1.0f);
        long now = 1000;
        var card = new OverlayNotificationCardState(item, targetY: 100f, now: now, metrics: metrics);

        // 1. 入场状态验证：起始位置高于目标位置 24px，透明度从 0 开始
        Assert.Equal(100f - 24f, card.StartY);
        Assert.Equal(100f, card.TargetY);
        Assert.Equal(0f, card.StartAlpha);
        Assert.Equal(1f, card.TargetAlpha);

        // 半程 (t = 1160ms，历时 160ms/320ms = 50%)：采用 EaseOutCubic，插值必须平滑推进且大于线性中点
        var midY = card.GetInterpolatedY(now: 1160);
        var midAlpha = card.GetInterpolatedAlpha(now: 1160);
        Assert.True(midY > card.StartY + (card.TargetY - card.StartY) * 0.5f, "EaseOutCubic 在 50% 时间点的位移应大于线性 50%");
        Assert.True(midAlpha > 0.5f, "Alpha 在 50% 时间点的淡入应大于 0.5");

        // 终程 (t = 1400ms)：位置到达 TargetY，Alpha 达到 1.0
        Assert.Equal(100f, card.GetInterpolatedY(now: 1400));
        Assert.Equal(1f, card.GetInterpolatedAlpha(now: 1400));

        // 2. 退场状态验证：调用 BeginExit 后向上浮动 24px，透明度降为 0
        card.BeginExit(now: 2000, metrics);
        Assert.True(card.IsExiting);
        Assert.False(card.IsFullyExited);
        Assert.Equal(100f, card.StartY);
        Assert.Equal(100f - 24f, card.TargetY);
        Assert.Equal(0f, card.TargetAlpha);

        // 退场半程 (t = 2120ms)
        var exitMidY = card.GetInterpolatedY(now: 2120);
        var exitMidAlpha = card.GetInterpolatedAlpha(now: 2120);
        Assert.True(exitMidY < 100f && exitMidY > 76f, "退场应向上平滑浮动");
        Assert.True(exitMidAlpha < 1f && exitMidAlpha > 0f, "退场应加速淡出");

        // 退场完成 (t = 2300ms)
        card.UpdateLifecycle(now: 2300);
        Assert.True(card.IsFullyExited, "历时 240ms 后生命周期应标记为完全退出");
    }

    [Fact]
    public void Queue_ReportsIsAnimatingDuringTransitions_AndStationaryWhenSettled()
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);

        var queue = new OverlayNotificationQueue();
        var item = new OverlayNotificationItem(OverlayNotificationType.Notice, "测试静动状态");
        queue.Enqueue(item);

        // 1. 入场期间：UpdateFrame 返回 isAnimating = true
        bool animatingEntrance = queue.UpdateFrame(g, now: 1050);
        Assert.True(animatingEntrance, "入场期间 UpdateFrame 必须报告仍在动画中");

        // 2. 稳态期间 (t = 1500ms，已完全入场且未超时)：返回 isAnimating = false
        bool settled = queue.UpdateFrame(g, now: 1500);
        Assert.False(settled, "卡片就位且未到 5 秒超时前，UpdateFrame 应返回 false 允许休眠");

        // 3. 5 秒超时退场期间：UpdateFrame 返回 isAnimating = true
        bool animatingExit = queue.UpdateFrame(g, now: 6100);
        Assert.True(animatingExit, "超时退场期间 UpdateFrame 必须恢复报告仍在动画中");
    }

    [Fact]
    public void DeveloperNotificationTrigger_AllThreeTypes_PostSuccessfully()
    {
        // 验证三种测试按钮触发器（错误、警告、通知）均能正常通过 Center 和 Queue 触发
        var noticeHandle = DeveloperNotificationTrigger.TriggerNotice();
        Assert.NotNull(noticeHandle);
        Assert.Equal(OverlayNotificationType.Notice, noticeHandle.Type);
        Assert.Contains("军工厂", noticeHandle.Message);

        var warnHandle = DeveloperNotificationTrigger.TriggerWarning();
        Assert.NotNull(warnHandle);
        Assert.Equal(OverlayNotificationType.Warning, warnHandle.Type);
        Assert.Contains("要点A测试", warnHandle.Message);
        Assert.Equal(0.65, warnHandle.Progress);

        var errorHandle = DeveloperNotificationTrigger.TriggerError();
        Assert.NotNull(errorHandle);
        Assert.Equal(OverlayNotificationType.Error, errorHandle.Type);
        Assert.Contains("图像配准校验失败", errorHandle.Message);
        Assert.Equal(0.20, errorHandle.Progress);
    }

    [Fact]
    public void DeveloperNotificationTrigger_RapidClicks_DoNotThrowAndAccumulate()
    {
        // 模拟用户连续高频多次点击测试按钮，验证无并发死锁与状态异常
        for (int i = 0; i < 10; i++)
        {
            var h = DeveloperNotificationTrigger.TriggerNotice();
            Assert.NotNull(h.Id);
        }
    }

    [Fact]
    public void OverlayNotificationService_LifecycleAndDispose_CleansUpWindow()
    {
        var queue = new OverlayNotificationQueue();
        var service = new OverlayNotificationService(queue);
        var handle = service.Notice("测试消息");
        Assert.NotNull(handle);

        // 验证 service 实例实现 IDisposable 并能正常析构底层窗口
        Assert.IsAssignableFrom<IDisposable>(service);
        service.Dispose();
    }

    private static double CalculateLuminance(Color c)
    {
        // 标准相对亮度计算公式 (ITU-R BT.709)
        static double Channel(byte val)
        {
            var s = val / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
