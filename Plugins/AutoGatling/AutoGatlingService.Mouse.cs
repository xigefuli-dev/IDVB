using System.Runtime.InteropServices;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.AutoGatling;

public sealed partial class AutoGatlingService
{
    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var mouse = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            if (!InputInjectionMarkers.IsHostGeneratedMouseInput(mouse.ExtraInfo)
                && PluginMouseInput.TryDecode((uint)wParam.ToInt64(), mouse.MouseData,
                    out var button, out var down))
            {
                if (HandleMouseTrigger(_activateBinding, ref _activateKeyDown, button, down, true)
                    || HandleMouseTrigger(_reloadBinding, ref _reloadKeyDown, button, down, false))
                    return new IntPtr(1);
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private bool HandleMouseTrigger(PluginInputBinding binding, ref bool held,
        PluginMouseButton button, bool down, bool activate)
    {
        if (binding.Kind != PluginInputBindingKind.Mouse || binding.MouseButton != button)
            return false;
        if (!down && !held)
            return false;
        var firstDown = down && !held;
        held = down;
        if (firstDown)
            StartOperation(activate);
        return true;
    }

}
