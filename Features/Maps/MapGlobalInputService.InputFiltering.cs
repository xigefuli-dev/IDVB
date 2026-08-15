using System.Runtime.InteropServices;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Features.Maps;

public sealed partial class MapGlobalInputService
{
    private static bool IsMarkedInjectedMouse(IntPtr lParam)
    {
        var mouse = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
        return (mouse.Flags & 0x00000001) != 0
            && mouse.ExtraInfo == new IntPtr(InputInjectionMarkers.HostGeneratedInput);
    }
}
