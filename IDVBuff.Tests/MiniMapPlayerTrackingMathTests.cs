using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MiniMapPlayerTrackingMathTests
{
    [Fact]
    public void ScreenPointInverseTransform_CalculatesCorrectNormalizedReferenceCoordinates()
    {
        // 模拟 1920x1080 下的大地图对齐变换
        var transform = new MapOverlayTransform
        {
            ReferenceWidth = 2048,
            ReferenceHeight = 1536,
            ScaleX = 0.5,
            ScaleY = 0.5,
            OffsetX = 400,
            OffsetY = 200,
            OrientationDegrees = 0
        };

        // 假设在屏幕物理点 (820, 480) 识别到 1 号玩家
        var screenX = 820.0;
        var screenY = 480.0;

        // 逆变换到参考地图像素坐标
        var refX = (screenX - transform.OffsetX) / transform.ScaleX;
        var refY = (screenY - transform.OffsetY) / transform.ScaleY;

        Assert.Equal(840.0, refX, 2);
        Assert.Equal(560.0, refY, 2);

        // 归一化坐标
        var normX = Math.Clamp(refX / transform.ReferenceWidth, 0.0, 1.0);
        var normY = Math.Clamp(refY / transform.ReferenceHeight, 0.0, 1.0);

        Assert.Equal(840.0 / 2048.0, normX, 5);
        Assert.Equal(560.0 / 1536.0, normY, 5);

        // 映射到小地图 (200x150, 偏移 20, 20)
        var destLeft = 20.0;
        var destTop = 20.0;
        var destWidth = 200.0;
        var destHeight = 150.0;

        var miniPx = destLeft + (normX * destWidth);
        var miniPy = destTop + (normY * destHeight);

        Assert.True(miniPx >= destLeft && miniPx <= destLeft + destWidth);
        Assert.True(miniPy >= destTop && miniPy <= destTop + destHeight);
    }

    [Fact]
    public void GtaPlayerTrackingState_ExpandsRadiusWhenLost_AndClampsToMax()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new GtaPlayerTrackingState
        {
            PlayerSlot = PlayerSlot.Player1,
            VisualKind = GtaPlayerTrackingVisualKind.Crosshair,
            NormalizedX = 0.5,
            NormalizedY = 0.5,
            LastSeenTimestamp = now
        };

        // 1. 锁定状态下，半径始终为基础半径
        Assert.Equal(8.0, state.GetExpandingRadius(now, baseRadius: 8.0, maxRadius: 40.0, growthRatePerSecond: 10.0));

        // 2. 失去实时位置
        state.VisualKind = GtaPlayerTrackingVisualKind.ExpandingRing;
        state.LostTimestamp = now;

        // 刚丢失时（0秒）为基础半径
        Assert.Equal(8.0, state.GetExpandingRadius(now, baseRadius: 8.0, maxRadius: 40.0, growthRatePerSecond: 10.0));

        // 丢失 1.5 秒后：8.0 + 1.5 * 10 = 23.0
        var after15Sec = now.AddSeconds(1.5);
        Assert.Equal(23.0, state.GetExpandingRadius(after15Sec, baseRadius: 8.0, maxRadius: 40.0, growthRatePerSecond: 10.0));

        // 丢失 5.0 秒后：8.0 + 5.0 * 10 = 58.0 > maxRadius(40.0)，应被 clamp 截断在 40.0
        var after5Sec = now.AddSeconds(5.0);
        Assert.Equal(40.0, state.GetExpandingRadius(after5Sec, baseRadius: 8.0, maxRadius: 40.0, growthRatePerSecond: 10.0));
    }
}
