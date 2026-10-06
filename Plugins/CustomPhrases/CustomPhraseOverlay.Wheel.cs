using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace IDVBuff.Plugins.CustomPhrases;

/// <summary>
/// 短语菜单的滚轮支持。
///
/// 这层浮层是「非激活 + 鼠标穿透」的：鼠标滚轮消息（WM_MOUSEWHEEL）只会发给焦点窗口，
/// 穿透窗口根本收不到。所以这里改用 Raw Input（RIDEV_INPUTSINK）在浮层窗口上接收鼠标设备的
/// 原始输入——它不改变焦点，也不吞输入（游戏照常收到它自己的滚轮）。
///
/// 菜单显示期间：上滚 → 高亮上一条，下滚 → 高亮下一条；到顶 / 到底停住，不循环。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class CustomPhraseOverlay
{
    private const uint WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RiMouseWheel = 0x0400;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const ushort HidUsagePageGenericDesktop = 0x01;
    private const ushort HidUsageMouse = 0x02;
    private const int RawInputHeaderSize = 24;

    /// <summary>窗口句柄 → 浮层实例：WM_INPUT 落在静态窗口过程里，需要转回实例。</summary>
    internal static readonly ConcurrentDictionary<IntPtr, CustomPhraseOverlay> Overlays = new();

    private bool _rawInputRegistered;
    private int _scrollGate;

    /// <summary>注册鼠标 Raw Input（浮层窗口创建后做一次即可）。</summary>
    private void EnsureRawInput()
    {
        if (_rawInputRegistered || _handle == IntPtr.Zero)
            return;

        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = HidUsagePageGenericDesktop,
                Usage = HidUsageMouse,
                Flags = RidevInputSink,
                Target = _handle
            }
        };
        if (RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            _rawInputRegistered = true;
    }

    private void UnregisterRawInput()
    {
        if (!_rawInputRegistered)
            return;

        _rawInputRegistered = false;
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = HidUsagePageGenericDesktop,
                Usage = HidUsageMouse,
                Flags = RidevRemove,
                Target = IntPtr.Zero
            }
        };
        RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    /// <summary>解析一次 WM_INPUT，只关心滚轮。</summary>
    internal void HandleRawInput(IntPtr rawInputHandle)
    {
        if (rawInputHandle == IntPtr.Zero)
            return;

        var size = 0u;
        if (GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, RawInputHeaderSize) != 0
            || size == 0
            || size > 512)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, RawInputHeaderSize) != size)
                return;
            if (Marshal.ReadInt32(buffer) != (int)RimTypeMouse)
                return;

            var mouse = Marshal.PtrToStructure<RawMouse>(IntPtr.Add(buffer, RawInputHeaderSize));
            if ((mouse.ButtonFlags & RiMouseWheel) == 0)
                return;

            var delta = (short)mouse.ButtonData;
            if (delta == 0)
                return;

            // 上滚（正值）= 上一条。
            ScrollSelection(delta > 0 ? -1 : 1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 把高亮放回第一条：菜单刚打开时调用（调用方已持有 _sync，这里 lock 可重入）。
    /// 独立成方法也让离线探针能直接验证「默认高亮第一条」。
    /// </summary>
    internal void ResetSelectionToFirst()
    {
        lock (_sync)
            _selectedIndex = _phrases.Length == 0 ? -1 : 0;
    }

    /// <summary>把高亮上 / 下移动一条；到顶、到底就停住。</summary>
    private void ScrollSelection(int step)
    {
        if (Interlocked.CompareExchange(ref _scrollGate, 1, 0) != 0)
            return;

        var changed = false;
        try
        {
            lock (_sync)
            {
                if (!_visible || _phrases.Length == 0)
                    return;
                var next = Math.Clamp(_selectedIndex + step, 0, _phrases.Length - 1);
                if (next != _selectedIndex)
                {
                    _selectedIndex = next;
                    changed = true;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _scrollGate, 0);
        }

        if (changed)
            Render();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    /// <summary>RAWMOUSE：usFlags 后是 4 字节的 union（usButtonFlags / usButtonData），必须显式对齐。</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RawInputDevice[] devices,
        uint deviceCount,
        uint deviceSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        int headerSize);
}
