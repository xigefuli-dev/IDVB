using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed partial class MapStructureRegistrarTests
{
    // ═══════════════════════════════════════════════════════════════
    // 互逆缩放 (Reciprocal Scale) 测试 — baselineScale < 1.0
    //
    // 注意：当前互逆缩放实现存在已知问题：
    // 1. Fast 路径（TryFastCoarseAlign）无条件激活互逆缩放，
    //    而 Legacy 路径在 RestrictSearchToLockedTransform=true 时跳过。
    //    这一不一致可能导致两条路径对相同输入返回不同结果。
    // 2. 互逆缩放下 CollectCandidates → Evaluate 的坐标映射在
    //    降采样的 referenceDistance 上存在边界越界风险(#OpenCV roi 异常)。
    // 3. Fast 路径的 dsStructure 生命周期管理存在问题，
    //    在 RestrictedSearch 路径中可能出现 ObjectDisposedException。
    // 以下测试重点验证：代码路径可达、无意外崩溃、状态正确重置。
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ReciprocalScale_BelowOne_LegacyPath_ExecutesWithoutException()
    {
        // 验证 Legacy 路径在 baselineScale < 1.0 时可到达互逆缩放代码路径。
        // 使用极小的裁剪区域 + 适中的缩比确保 query 远小于降采样参考图。
        using var reference = BuildLargeReference(); // 640×480
        var crop = new Rect(200, 150, 100, 80);
        using var source = new Mat(reference, crop);
        const double targetScale = 0.7;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * targetScale),
                (int)Math.Round(source.Height * targetScale)),
            0d, 0d, InterpolationFlags.Area);

        var viewport = new MapScreenRect(
            100d, 80d, live.Width, live.Height);
        var tuning = ReciprocalTuning();

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        // 核心：不应抛出异常
        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = tuning,
                AllowScaleSearch = false
            });

        // 无论接受与否，结果对象应完整
        Assert.NotNull(result);
        Assert.True(result.ScaleHypothesisCount > 0);
    }

    [Fact]
    public void ReciprocalScale_FastFallbackToLegacy_ExecutesWithoutDisposedContext()
    {
        // 验证 Fast 路径在 baselineScale < 1.0 时可到达互逆缩放代码路径。
        using var reference = BuildLargeReference();
        var crop = new Rect(200, 150, 100, 80);
        using var source = new Mat(reference, crop);
        const double targetScale = 0.7;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * targetScale),
                (int)Math.Round(source.Height * targetScale)),
            0d, 0d, InterpolationFlags.Area);

        var viewport = new MapScreenRect(
            100d, 80d, live.Width, live.Height);
        var tuning = ReciprocalTuning();
        tuning.EnableFastAlignment = true;
        // This is the production path that previously reused the fast path's
        // disposed downsampled structure in Legacy.
        tuning.FastFallbackToLegacy = true;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        // 核心：不应抛出异常
        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = tuning,
                AllowScaleSearch = false
            });

        Assert.NotNull(result);
    }

    [Fact]
    public void ReciprocalScale_ContextReset_BetweenSequentialCalls()
    {
        // 连续两次 Register 调用之间，_currentReciprocalScale
        // 应被重置为 None，避免第二次调用泄露第一次的状态。
        // 测试策略：
        //   Call 1: baselineScale < 1.0 → 触发互逆缩放（接受/拒绝均可）
        //   Call 2: baselineScale = 1.0 → 标准 1:1 场景，必须正确通过
        using var reference = BuildLargeReference();
        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        // Call 1: 互逆缩放路径
        var crop1 = new Rect(200, 150, 100, 80);
        using var source1 = new Mat(reference, crop1);
        const double lowScale = 0.7;
        using var liveSmall = new Mat();
        Cv2.Resize(
            source1,
            liveSmall,
            new Size(
                (int)Math.Round(source1.Width * lowScale),
                (int)Math.Round(source1.Height * lowScale)),
            0d, 0d, InterpolationFlags.Area);

        registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = liveSmall,
                ViewportBounds = new MapScreenRect(
                    100d, 80d, liveSmall.Width, liveSmall.Height),
                LockedTransform = LockedAtScale(reference, lowScale),
                Tuning = ReciprocalTuning(),
                AllowScaleSearch = false
            });

        // Call 2: 标准 1:1 场景 — 不应受 Call 1 的互逆缩放状态影响
        var crop2 = new Rect(200, 150, 180, 130);
        using var liveNormal = new Mat(reference, crop2).Clone();
        var normalViewport = new MapScreenRect(
            0d, 0d, liveNormal.Width, liveNormal.Height);
        var expectedOffsetX = normalViewport.X - crop2.X;
        var expectedOffsetY = normalViewport.Y - crop2.Y;

        var second = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = liveNormal,
                ViewportBounds = normalViewport,
                LockedTransform = Locked(reference),
                Tuning = TestTuning(),
                AllowScaleSearch = false
            });

        Assert.True(second.Accepted,
            $"Second call should succeed regardless of first call's state. "
            + $"Rejection: {second.RejectionReason}");
        Assert.NotNull(second.Transform);
        Assert.InRange(
            Math.Abs(second.Transform.OffsetX - expectedOffsetX),
            0d,
            3d);
        Assert.InRange(
            Math.Abs(second.Transform.OffsetY - expectedOffsetY),
            0d,
            3d);
    }

    [Fact]
    public void ReciprocalScale_FastVsLegacy_RestrictedSearch_BehaviorDocumented()
    {
        // ⚠️ 已知不一致：Fast 路径在 TryFastCoarseAlign 中无条件激活
        // 互逆缩放（line 1121），而 Legacy 路径在 RegisterLegacy 中
        // 会检查 !RestrictSearchToLockedTransform（line 235）。
        //
        // 本测试记录当前行为，当修复后需要更新断言。
        using var reference = BuildLargeReference();
        var crop = new Rect(200, 150, 100, 80);
        using var source = new Mat(reference, crop);
        const double targetScale = 0.7;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * targetScale),
                (int)Math.Round(source.Height * targetScale)),
            0d, 0d, InterpolationFlags.Area);

        var viewport = new MapScreenRect(
            100d, 80d, live.Width, live.Height);
        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        // Legacy（Fast 禁用，RestrictedSearch）
        var legacyTuning = ReciprocalTuning();
        var legacyResult = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = legacyTuning,
                AllowScaleSearch = false,
                RestrictSearchToLockedTransform = true
            });

        // Fast（不允许回退，RestrictedSearch）
        var fastTuning = ReciprocalTuning();
        fastTuning.EnableFastAlignment = true;
        fastTuning.FastFallbackToLegacy = false;
        var fastResult = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = fastTuning,
                AllowScaleSearch = false,
                RestrictSearchToLockedTransform = true
            });

        // 记录当前行为。修复 Fast 路径的缺失条件后，两者应一致。
        var bothRejected = !legacyResult.Accepted && !fastResult.Accepted;
        var bothAccepted = legacyResult.Accepted && fastResult.Accepted;
        Assert.True(
            bothRejected || bothAccepted,
            $"Legacy accepted={legacyResult.Accepted} ({legacyResult.RejectionReason}), "
            + $"Fast accepted={fastResult.Accepted} ({fastResult.RejectionReason}). "
            + "Expected consistent accept/reject decision.");
    }

    [Fact]
    public void ReciprocalScale_VisibleAware_EdgeCandidatesDoNotThrowRoiException()
    {
        // 回归：互逆缩放（baselineScale < 1.0）下 referenceDistance 是降采样
        // 图，而 IoU 响应图基于原始 reference.StructureMask 计算，边缘峰值
        // 位置会超出 referenceDistance 边界。此前在 Evaluate() 中裁剪
        // distance patch 时抛 OpenCVException，被侧门扫描吞掉后导致快捷
        // 扫描完成标志未设置（无法锁定缩放）。修复：越界候选直接跳过。
        using var reference = BuildLargeReference(); // 640×480
        var crop = new Rect(200, 150, 100, 80);
        using var source = new Mat(reference, crop);
        const double targetScale = 0.7;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * targetScale),
                (int)Math.Round(source.Height * targetScale)),
            0d, 0d, InterpolationFlags.Area);

        var viewport = new MapScreenRect(100d, 80d, live.Width, live.Height);
        var tuning = ReciprocalTuning();
        tuning.EnableVisibleMask = true;
        tuning.EnableVisibleAwareInjection = true;
        tuning.EnableVisibleAwareEarlyExit = false;
        tuning.VisibleAwareMinimumVisibleFraction = 0.01d;
        tuning.VisibleAwareMinimumVisibleStructurePixels = 10;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        // 核心：不得抛 OpenCVException
        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = tuning,
                AllowScaleSearch = false
            });

        Assert.NotNull(result);
        // 不要求接受；只要求越界候选被安全跳过
        Assert.False(result.VisibleAwareEarlyAccepted);
    }

    [Fact]
    public void ReciprocalScale_AboveOne_NotActivated_NormalPathWorks()
    {
        // baselineScale > 1.0 时互逆缩放不应激活，走正常路径。
        using var reference = BuildLargeReference(); // 640×480
        var crop = new Rect(160, 110, 200, 150);
        using var source = new Mat(reference, crop);
        const double targetScale = 1.3;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * targetScale),
                (int)Math.Round(source.Height * targetScale)),
            0d, 0d, InterpolationFlags.Linear);

        var viewport = new MapScreenRect(
            200d, 150d, live.Width, live.Height);
        var tuning = ReciprocalTuning();

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());
        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = tuning,
                AllowScaleSearch = true
            });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.InRange(
            result.Transform.ScaleX,
            targetScale - 0.18,
            targetScale + 0.18);
    }
}
