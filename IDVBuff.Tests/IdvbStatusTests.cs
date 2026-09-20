using IDVBuff.Core.Diagnostics;
using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests;

public sealed class IdvbStatusTests
{
    [Fact]
    public void StatusCodes_AdhereToHttpCategorySemantics()
    {
        var ok = IdvbStatus.Ok();
        Assert.True(ok.IsSuccess);
        Assert.False(ok.IsFallback);
        Assert.False(ok.IsClientError);
        Assert.False(ok.IsServerError);

        var fallback = IdvbStatus.Fallback(
            IdvbHttpCode.Vpsg3FallbackToLegacy,
            IdvbSubCode.Vpsg3FallbackApertureMarginLow,
            "Vpsg3SolverRejected",
            "VPSG 3.0 未收敛");
        Assert.False(fallback.IsSuccess);
        Assert.True(fallback.IsFallback);
        Assert.False(fallback.IsClientError);
        Assert.False(fallback.IsServerError);

        var clientErr = IdvbStatus.ClientError(
            IdvbHttpCode.UnprocessableVisual,
            IdvbSubCode.StructureWeakAbsoluteScore,
            "WeakAbsoluteScore",
            "结构贴合度不足");
        Assert.False(clientErr.IsSuccess);
        Assert.False(clientErr.IsFallback);
        Assert.True(clientErr.IsClientError);
        Assert.False(clientErr.IsServerError);

        var serverErr = IdvbStatus.FromException(
            new InvalidOperationException("OpenCv 内存耗尽"),
            stage: "StructureRegistration.MatrixAlloc");
        Assert.False(serverErr.IsSuccess);
        Assert.False(serverErr.IsFallback);
        Assert.False(serverErr.IsClientError);
        Assert.True(serverErr.IsServerError);
    }

    [Fact]
    public void CauseChain_PreservesRootCauseAcrossFallbacks()
    {
        // 1. 新特性 VPSG 3.0 失败，产生 301 降级状态
        var vpsgStatus = IdvbStatus.Fallback(
            IdvbHttpCode.Vpsg3FallbackToLegacy,
            IdvbSubCode.Vpsg3FallbackApertureMarginLow,
            "Vpsg3SolverRejected",
            "VPSG 3.0 快速对齐未接受 · ApertureMarginTooLow",
            technicalDetail: "scale=1.1739, margin=0.012, threshold=0.050",
            stage: "Vpsg3.FastBootstrapSolver");

        // 2. 老版本传统配准兜底运行并失败，产生 422 错误，并将 vpsgStatus 挂载为 Cause
        var finalFailure = IdvbStatus.ClientError(
            IdvbHttpCode.UnprocessableVisual,
            IdvbSubCode.StructureWeakAbsoluteScore,
            "WeakAbsoluteScore",
            "最佳候选与墙体结构的绝对贴合度不足",
            technicalDetail: "bestScore=0.742, margin=0.081",
            stage: "MapStructureValidator.AbsoluteGate",
            cause: vpsgStatus);

        // 3. 验证因果链
        var trace = finalFailure.ToTraceString();

        // 必须同时包含老版本的表现和新特性的真实死因！
        Assert.Contains("[422:34222 WeakAbsoluteScore]", trace);
        Assert.Contains("最佳候选与墙体结构的绝对贴合度不足", trace);
        Assert.Contains("<- CausedBy:", trace);
        Assert.Contains("[301:33012 Vpsg3SolverRejected]", trace);
        Assert.Contains("ApertureMarginTooLow", trace);
        Assert.Contains("scale=1.1739", trace);
    }

    [Fact]
    public void MapLogCollector_RecordsStatusCodeAndCauseChain()
    {
        using var collector = new MapLogCollector();
        collector.IsEnabled = true;

        var vpsgStatus = IdvbStatus.Fallback(
            IdvbHttpCode.Vpsg3FallbackToLegacy,
            IdvbSubCode.Vpsg3FallbackNotConverged,
            "Vpsg3NotConverged",
            "VPSG 3.0 未收敛");

        var finalStatus = IdvbStatus.ClientError(
            IdvbHttpCode.UnprocessableVisual,
            IdvbSubCode.StructureWeakAbsoluteScore,
            "RegistrationFailed",
            "配准失败",
            cause: vpsgStatus);

        collector.AppendStatus(finalStatus, MapLogCategory.StructureRegistration);

        var entries = collector.GetEntries();
        Assert.NotEmpty(entries);
        var entry = entries[^1];

        Assert.Equal(IdvbHttpCode.UnprocessableVisual, entry.StatusCode);
        Assert.Equal(IdvbSubCode.StructureWeakAbsoluteScore, entry.SubCode);
        Assert.NotNull(entry.StatusChain);
        Assert.Contains("<- CausedBy: [301:33013 Vpsg3NotConverged]", entry.StatusChain);
    }

    [Fact]
    public void Attempt_WithStatus_PreservesCauseAcrossLegacyFallback()
    {
        // 模拟 VPSG3 快速对齐降级
        var vpsgStatus = IdvbStatus.Fallback(
            IdvbHttpCode.Vpsg3FallbackToLegacy,
            IdvbSubCode.Vpsg3FallbackApertureMarginLow,
            "Vpsg3SolverRejected",
            "VPSG 3.0 快速对齐未接受 · 光圈裕度不足",
            technicalDetail: "margin=0.015 < 0.050",
            stage: "Vpsg3.FastBootstrapSolver");

        // 模拟老版本配准最终失败
        var failureStatus = IdvbStatus.ClientError(
            IdvbHttpCode.UnprocessableVisual,
            IdvbSubCode.StructureWeakAbsoluteScore,
            "LockedFloorAlignmentFailed",
            "锁定楼层结构配准未通过",
            stage: "LockedFloorFeature.ValidateCandidates",
            cause: vpsgStatus);

        var attempt = new MapRecognitionAttempt
        {
            FailureReason = failureStatus.UserMessage,
            Status = failureStatus
        };

        // 验证上游消费方或 UI 能够拿到带有新特性死因的 TraceString
        Assert.NotNull(attempt.Status);
        Assert.True(attempt.Status.IsClientError);
        Assert.NotNull(attempt.Status.Cause);
        Assert.True(attempt.Status.Cause.IsFallback);
        Assert.Equal(IdvbSubCode.Vpsg3FallbackApertureMarginLow, attempt.Status.Cause.SubCode);

        var trace = attempt.Status.ToTraceString();
        Assert.Contains("锁定楼层结构配准未通过", trace);
        Assert.Contains("<- CausedBy: [301:33012 Vpsg3SolverRejected] VPSG 3.0 快速对齐未接受 · 光圈裕度不足 (margin=0.015 < 0.050) @Vpsg3.FastBootstrapSolver", trace);
    }
}
