namespace IDVBuff.Plugins.SceneQuickActions;

internal sealed partial class SceneQuickActionsRunner
{
    private bool ProcessInviteScene(
        GrabbedFrame frame,
        SceneQuickActionsOptions options,
        DateTime now,
        CancellationToken token)
    {
        var matched = _matcher.TryMatch(
            frame.Gray,
            SceneQuickActionsTemplates.AcceptButton,
            options.MatchThreshold,
            out var acceptHit);
        if (!matched)
        {
            if (!_inviteArmed)
                _inviteArmed = true;
            return false;
        }

        if (!_inviteArmed || now < _inviteReadyAt)
            return false;

        _inviteArmed = false;
        _inviteReadyAt = now.AddMilliseconds(options.CooldownMilliseconds);
        PerformInvite(frame, acceptHit, options, token);
        return true;
    }

    private void PerformInvite(
        GrabbedFrame frame,
        MatchHit hit,
        SceneQuickActionsOptions options,
        CancellationToken token)
    {
        var (x, y) = SceneQuickActionsMatcher.ToScreen(hit.CenterX, hit.CenterY, frame.Bounds, frame.Size);
        _logger.Info($"识别到「接受」按钮（相似度 {hit.Score:P0}），执行「按 Tab 开背包切出鼠标 → 点击接受 → 按 Tab 关背包」。");

        var wokeMouse = false;
        try
        {
            WakeMouse(options, frame.WindowHandle, token, ref wokeMouse);
            EnsureForeground(frame.WindowHandle);
            NativeInput.MoveSmoothly(x, y, MoveDurationMilliseconds, token);
            SleepAfterStep(options, PreClickDelayMilliseconds, token);
            EnsureForeground(frame.WindowHandle);
            NativeInput.ClickLeft(ClickHoldMilliseconds);
            SleepAfterStep(options, 0, token);
            _logger.Info($"已点击「接受」（屏幕坐标 {x},{y}）。");
        }
        finally
        {
            // 无论点击成功与否都要把背包关掉：留在背包界面里会挡住游戏后续操作。
            if (wokeMouse)
                CloseBackpack(frame.WindowHandle, options);
        }
    }

    private void ProcessPickupScene(
        GrabbedFrame frame,
        SceneQuickActionsOptions options,
        DateTime now,
        CancellationToken token)
    {
        // 「拾取全部」文案在任何键位下都成立（模板已去掉按键框）；「快捷替换」按钮也算面板存在的证据。
        var hasAnchor = _matcher.TryMatch(
            frame.Gray,
            SceneQuickActionsTemplates.PickupAll,
            options.MatchThreshold,
            out var anchorHit);
        var hasQuickReplace = _matcher.TryMatch(
            frame.Gray,
            SceneQuickActionsTemplates.QuickReplace,
            options.MatchThreshold,
            out var quickReplaceHit);

        if (!hasAnchor && !hasQuickReplace)
        {
            if (!_pickupArmed)
                _pickupArmed = true;
            return;
        }

        if (!_pickupArmed || now < _pickupReadyAt)
            return;

        // 用鼠标点击「拾取全部」时必须先认到它的位置，认不到就再等下一帧，而不是乱点。
        if (!options.PickupAllBinding.IsConfigured && !hasAnchor)
        {
            LogThrottled("可拾取面板出现了，但没认到「拾取全部」的位置，本次不点击。");
            return;
        }

        _pickupArmed = false;
        _pickupReadyAt = now.AddMilliseconds(options.CooldownMilliseconds);
        PerformPickup(
            frame,
            hasAnchor ? anchorHit : null,
            hasQuickReplace ? quickReplaceHit : null,
            options,
            token);
    }

    private void PerformPickup(
        GrabbedFrame frame,
        MatchHit? pickupHit,
        MatchHit? quickReplaceHit,
        SceneQuickActionsOptions options,
        CancellationToken token)
    {
        var wokeMouse = false;
        try
        {
            if (options.PickupWakeMouse)
                WakeMouse(options, frame.WindowHandle, token, ref wokeMouse);
            // 第一步：拾取全部（按下配置的按键，未配置则点击提示位置）
            if (options.PickupAllBinding.IsConfigured)
            {
                EnsureForeground(frame.WindowHandle);
                NativeInput.InjectBinding(options.PickupAllBinding, KeyHoldMilliseconds);
                _logger.Info($"已按下「拾取全部」按键（{options.PickupAllBinding.DisplayName}）。");
            }
            else if (pickupHit is { } anchor)
            {
                var (x, y) = SceneQuickActionsMatcher.ToScreen(
                    anchor.CenterX,
                    anchor.CenterY,
                    frame.Bounds,
                    frame.Size);
                EnsureForeground(frame.WindowHandle);
                NativeInput.MoveSmoothly(x, y, MoveDurationMilliseconds, token);
                SleepAfterStep(options, PreClickDelayMilliseconds, token);
                EnsureForeground(frame.WindowHandle);
                NativeInput.ClickLeft(ClickHoldMilliseconds);
                _logger.Info($"已点击「拾取全部」（相似度 {anchor.Score:P0}，屏幕坐标 {x},{y}）。");
            }
            else
            {
                return;
            }

            SleepAfterStep(options, options.BetweenClicksMilliseconds, token);

            // 第二步：快捷替换
            if (options.QuickReplaceBinding.IsConfigured)
            {
                PressQuickReplaceKey(options, token);
                return;
            }

            ClickQuickReplace(frame, quickReplaceHit, options, token);
        }
        finally
        {
            // 切出过背包就必须关掉，否则会把拾取面板顶掉、还挡住后续操作。
            if (wokeMouse)
                CloseBackpack(frame.WindowHandle, options);
        }
    }

    /// <summary>「快捷替换」用按键：只需要确认面板还在，位置不重要。</summary>
    private void PressQuickReplaceKey(SceneQuickActionsOptions options, CancellationToken token)
    {
        for (var attempt = 0; attempt < QuickReplaceAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (attempt > 0)
                NativeInput.Sleep(QuickReplaceRetryMilliseconds, token);

            if (!_grabber.TryGrab(out var fresh, out var failure, token) || fresh is null)
            {
                LogThrottled($"按下「快捷替换」按键前抓帧失败：{failure}");
                break;
            }

            using (fresh)
            {
                var panelOpen =
                    _matcher.TryMatch(
                        fresh.Gray,
                        SceneQuickActionsTemplates.PickupAll,
                        options.MatchThreshold,
                        out _)
                    || _matcher.TryMatch(
                        fresh.Gray,
                        SceneQuickActionsTemplates.QuickReplace,
                        options.MatchThreshold,
                        out _);
                if (!panelOpen)
                    continue;

                EnsureForeground(fresh.WindowHandle);
                NativeInput.InjectBinding(options.QuickReplaceBinding, KeyHoldMilliseconds);
                SleepAfterStep(options, 0, token);
                _logger.Info($"已按下「快捷替换」按键（{options.QuickReplaceBinding.DisplayName}）。");
                return;
            }
        }

        LogThrottled("没能确认可拾取面板仍在，已跳过「快捷替换」按键，避免误触其它操作。");
    }

    /// <summary>「快捷替换」用鼠标：必须重新认到按钮位置，绝不盲点。</summary>
    private void ClickQuickReplace(
        GrabbedFrame frame,
        MatchHit? quickReplaceHit,
        SceneQuickActionsOptions options,
        CancellationToken token)
    {
        for (var attempt = 0; attempt < QuickReplaceAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (attempt > 0)
                NativeInput.Sleep(QuickReplaceRetryMilliseconds, token);

            if (!_grabber.TryGrab(out var fresh, out var failure, token) || fresh is null)
            {
                LogThrottled($"「快捷替换」定位时抓帧失败：{failure}");
                break;
            }

            using (fresh)
            {
                if (!_matcher.TryMatch(
                        fresh.Gray,
                        SceneQuickActionsTemplates.QuickReplace,
                        options.MatchThreshold,
                        out var freshHit))
                {
                    continue;
                }

                ClickHit(fresh, freshHit, "快捷替换", options, token);
                return;
            }
        }

        // 拿不到新画面时，退回「拾取全部」那一帧里已经确认过的位置。
        if (quickReplaceHit is { } remembered)
        {
            ClickHit(frame, remembered, "快捷替换（上一帧位置）", options, token);
            return;
        }

        LogThrottled("面板里没有识别到「快捷替换」，已跳过第二步。");
    }

    private void ClickHit(GrabbedFrame frame, MatchHit hit, string label,
        SceneQuickActionsOptions options, CancellationToken token)
    {
        var (x, y) = SceneQuickActionsMatcher.ToScreen(hit.CenterX, hit.CenterY, frame.Bounds, frame.Size);
        EnsureForeground(frame.WindowHandle);
        NativeInput.MoveSmoothly(x, y, MoveDurationMilliseconds, token);
        SleepAfterStep(options, PreClickDelayMilliseconds, token);
        EnsureForeground(frame.WindowHandle);
        NativeInput.ClickLeft(ClickHoldMilliseconds);
        SleepAfterStep(options, 0, token);
        _logger.Info($"已点击「{label}」（相似度 {hit.Score:P0}，屏幕坐标 {x},{y}）。");
    }

}
