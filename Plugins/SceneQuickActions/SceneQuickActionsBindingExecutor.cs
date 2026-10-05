using System.Runtime.ExceptionServices;
using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>记录本次成功按下的输入，失败时也逐个尝试释放，避免中途失败遗留修饰键。</summary>
internal static class SceneQuickActionsBindingExecutor
{
    public static void Execute(PluginInputBinding binding, int holdMilliseconds,
        Action<uint, bool> sendKeyboard, Action<PluginMouseButton, bool> sendMouse, Action<int> hold)
    {
        if (!binding.IsConfigured)
            throw new InvalidOperationException("尚未设置「切出鼠标」按键。");

        var pressedKeys = new List<uint>();
        var mousePressed = false;
        var failures = new List<Exception>();
        try
        {
            foreach (var key in GetModifierKeys(binding.Modifiers))
                PressKey(key);
            foreach (var key in (binding.CompanionVirtualKeys ?? [])
                         .Where(key => key != 0 && key <= ushort.MaxValue).Distinct())
                PressKey(key);

            if (binding.Kind == PluginInputBindingKind.Mouse)
            {
                sendMouse(binding.MouseButton, false);
                mousePressed = true;
            }
            else
            {
                PressKey(binding.VirtualKey);
            }
            hold(Math.Clamp(holdMilliseconds, 1, 500));
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            if (mousePressed)
                TryRelease(() => sendMouse(binding.MouseButton, true));
            for (var index = pressedKeys.Count - 1; index >= 0; index--)
            {
                var key = pressedKeys[index];
                TryRelease(() => sendKeyboard(key, true));
            }
        }

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("注入输入失败，部分输入释放也失败。", failures);

        void PressKey(uint key)
        {
            sendKeyboard(key, false);
            pressedKeys.Add(key);
        }

        void TryRelease(Action release)
        {
            try { release(); }
            catch (Exception exception) { failures.Add(exception); }
        }
    }

    private static IEnumerable<uint> GetModifierKeys(PluginInputModifiers modifiers)
    {
        if (modifiers.HasFlag(PluginInputModifiers.Control)) yield return 0x11;
        if (modifiers.HasFlag(PluginInputModifiers.Alt)) yield return 0x12;
        if (modifiers.HasFlag(PluginInputModifiers.Shift)) yield return 0x10;
        if (modifiers.HasFlag(PluginInputModifiers.Windows)) yield return 0x5B;
    }
}
