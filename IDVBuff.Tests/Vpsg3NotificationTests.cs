using IDVBuff.Core.Diagnostics;
using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests;

public sealed class Vpsg3NotificationTests
{
    [Fact]
    public void TryClassifyNotification_WhenStatusIsNull_ReturnsFalse()
    {
        var result = Vpsg3DegradationNotificationPolicy.TryClassifyNotification(
            null,
            out var isWarning,
            out var message,
            out var category);

        Assert.False(result);
        Assert.False(isWarning);
        Assert.Null(message);
        Assert.Null(category);
    }

    [Fact]
    public void TryClassifyNotification_WhenPrebuiltMissingDirectly_ReturnsOrangeWarning()
    {
        var status = IdvbStatus.ClientError(
            IdvbHttpCode.UnprocessableVisual,
            IdvbSubCode.Vpsg3FallbackIndexNotReady,
            "Vpsg3PrebuiltMissing",
            "当前地图或楼层缺少 VPSG 3.0 预制线图索引",
            technicalDetail: "map=1#floor_1; prebuilt=null",
            stage: "Vpsg3.Align");

        var result = Vpsg3DegradationNotificationPolicy.TryClassifyNotification(
            status,
            out var isWarning,
            out var message,
            out var category);

        Assert.True(result);
        Assert.True(isWarning);
        Assert.Equal("没有可用的预制线图，自动降级至可用算法。", message);
        Assert.Equal("prebuilt-missing", category);
    }

    [Fact]
    public void TryClassifyNotification_WhenCoreServiceFailedDirectly_ReturnsRedError()
    {
        var status = new IdvbStatus
        {
            Code = IdvbHttpCode.InternalError,
            SubCode = IdvbSubCode.StructureCvException,
            ReasonPhrase = "Vpsg3CoreServiceFailed",
            UserMessage = "VPSG 3.0 核心匹配服务不可用",
            TechnicalDetail = "Vpsg3Service.IsDisposed=true",
            Stage = "Vpsg3.Align"
        };

        var result = Vpsg3DegradationNotificationPolicy.TryClassifyNotification(
            status,
            out var isWarning,
            out var message,
            out var category);

        Assert.True(result);
        Assert.False(isWarning);
        Assert.Equal("VPSG 3.0 路由失效，核心服务不可用。已自动降级至早期算法。", message);
        Assert.Equal("core-failed", category);
    }

    [Fact]
    public void TryClassifyNotification_WhenCauseContainsPrebuiltMissing_ReturnsOrangeWarning()
    {
        var rootCause = IdvbStatus.ClientError(
            IdvbHttpCode.UnprocessableVisual,
            IdvbSubCode.Vpsg3FallbackIndexNotReady,
            "Vpsg3PrebuiltMissing",
            "缺少预制线图",
            technicalDetail: "map=arms_factory#1F",
            stage: "Vpsg3.Align");

        var outerStatus = IdvbStatus.ClientError(
            IdvbHttpCode.UnprocessableVisual,
            IdvbSubCode.StructureWeakAbsoluteScore,
            "LockedFloorFeatureUnreliable",
            "锁定楼层未能提取到可靠的几何特征",
            technicalDetail: "rejection=VPSG: missing; SIFT: off",
            stage: "LockedFloorFeature.Fit",
            cause: rootCause);

        var result = Vpsg3DegradationNotificationPolicy.TryClassifyNotification(
            outerStatus,
            out var isWarning,
            out var message,
            out var category);

        Assert.True(result);
        Assert.True(isWarning);
        Assert.Equal("没有可用的预制线图，自动降级至可用算法。", message);
        Assert.Equal("prebuilt-missing", category);
    }

    [Fact]
    public void TryClassifyNotification_WhenCauseContainsCoreServiceFailed_ReturnsRedError()
    {
        var rootCause = new IdvbStatus
        {
            Code = IdvbHttpCode.InternalError,
            SubCode = IdvbSubCode.StructureCvException,
            ReasonPhrase = "Vpsg3CoreServiceFailed",
            UserMessage = "VPSG 3.0 服务异常",
            TechnicalDetail = "ObjectDisposedException",
            Stage = "Vpsg3.Align"
        };

        var successWithFallback = IdvbStatus.Ok(
            "楼层特征对齐通过(VPSG降级) · floor=1F") with { Cause = rootCause };

        var result = Vpsg3DegradationNotificationPolicy.TryClassifyNotification(
            successWithFallback,
            out var isWarning,
            out var message,
            out var category);

        Assert.True(result);
        Assert.False(isWarning);
        Assert.Equal("VPSG 3.0 路由失效，核心服务不可用。已自动降级至早期算法。", message);
        Assert.Equal("core-failed", category);
    }

    [Fact]
    public void TryClassifyNotification_WhenNormalSolverRejected_ReturnsFalse()
    {
        var status = IdvbStatus.ClientError(
            IdvbHttpCode.UnprocessableVisual,
            IdvbSubCode.StructureWeakAbsoluteScore,
            "Vpsg3SolverRejected",
            "VPSG 3.0 求解器拒绝当前几何候选",
            technicalDetail: "aperture margin 0.05 < threshold 0.12",
            stage: "Vpsg3.Align");

        var result = Vpsg3DegradationNotificationPolicy.TryClassifyNotification(
            status,
            out var isWarning,
            out var message,
            out var category);

        Assert.False(result);
        Assert.False(isWarning);
        Assert.Null(message);
        Assert.Null(category);
    }
}
