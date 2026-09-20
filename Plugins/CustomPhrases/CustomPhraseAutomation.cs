using System.ComponentModel;
using System.Runtime.InteropServices;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.CustomPhrases;

/// <summary>
/// 自定义短语的真实输入流程：聊天菜单按键 → 客户区归一化坐标点击 →
/// Ctrl+V → 回车发送。每个注入步骤都检查前台窗口，避免焦点已经离开游戏时误操作。
/// </summary>
public sealed class CustomPhraseAutomation
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfXdown = 0x0080;
    private const uint MouseeventfXup = 0x0100;
    private const uint VirtualKeyV = 0x56;
    private const int KeyHoldMilliseconds = 20;
    private const int StepDelayMilliseconds = 70;
    private static readonly IntPtr InputMarker =
        new(InputInjectionMarkers.HostGeneratedInput);

    private readonly IPluginGameWindowService _gameWindow;
    private readonly Action<string> _log;
    private readonly CustomPhraseOptions _options;

    public CustomPhraseAutomation(
        IPluginGameWindowService gameWindow,
        Action<string> log,
        CustomPhraseOptions options)
    {
        _gameWindow = gameWindow ?? throw new ArgumentNullException(nameof(gameWindow));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task EnableMouseAsync(
        PluginInputBinding binding,
        IntPtr expectedWindow,
        CancellationToken cancellationToken)
    {
        if (!binding.IsConfigured)
            throw new InvalidOperationException("启用鼠标按键尚未设置。");
        EnsureGameWindow(expectedWindow);
        await PressBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        await JitteredDelayAsync(StepDelayMilliseconds, cancellationToken)
            .ConfigureAwait(false);
        EnsureGameWindow(expectedWindow);
    }

    public async Task SendAsync(
        string phrase,
        PluginInputBinding chatMenuBinding,
        IntPtr expectedWindow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phrase);
        if (!chatMenuBinding.IsConfigured)
            throw new InvalidOperationException("聊天菜单按键尚未设置。");
        if (expectedWindow == IntPtr.Zero)
            throw new InvalidOperationException("触发短语时没有有效的游戏窗口。");

        EnsureGameWindow(expectedWindow);
        if (!NativeClipboard.TrySetText(phrase, out var clipboardFailure))
            throw new InvalidOperationException(clipboardFailure);

        EnsureGameWindow(expectedWindow);
        await PressBindingAsync(chatMenuBinding, cancellationToken).ConfigureAwait(false);
        await JitteredDelayAsync(StepDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        var bounds = EnsureGameWindow(expectedWindow);
        if (!CustomPhrasePluginData.TryGetChatBoxCoordinate(
                bounds.Width,
                bounds.Height,
                out var chatBoxCoordinate))
        {
            throw new InvalidOperationException(
                "自定义短语未执行：当前游戏客户区不是精确的 16:9 或 16:10。");
        }
        var point = bounds.ToScreenPoint(chatBoxCoordinate);
        MoveCursor(point.X, point.Y);
        await JitteredDelayAsync(0, cancellationToken).ConfigureAwait(false);
        await ClickAsync(PluginMouseButton.Left, cancellationToken).ConfigureAwait(false);
        await JitteredDelayAsync(StepDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        EnsureGameWindow(expectedWindow);
        await PressChordAsync(
            VirtualKeyV,
            PluginInputModifiers.Control,
            cancellationToken)
            .ConfigureAwait(false);
        await JitteredDelayAsync(StepDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        EnsureGameWindow(expectedWindow);
        await PressKeyAsync(CustomPhrasePluginData.SendVirtualKey, cancellationToken)
            .ConfigureAwait(false);
        await JitteredDelayAsync(0, cancellationToken).ConfigureAwait(false);
        _log("自定义短语已完成粘贴并发送。");
    }

    private Task JitteredDelayAsync(int baseMilliseconds, CancellationToken cancellationToken)
    {
        var (minimum, maximum) = _options.GetOrderedRandomDelayRange();
        return Task.Delay(
            Math.Max(0, baseMilliseconds)
            + Random.Shared.Next(minimum, maximum + 1),
            cancellationToken);
    }

    private PluginClientBounds EnsureGameWindow(IntPtr expectedWindow)
    {
        if (!_gameWindow.TryGetForegroundClientBounds(
                out var bounds,
                out var actualWindow,
                out var failureReason)
            || actualWindow != expectedWindow)
        {
            throw new InvalidOperationException(
                actualWindow != expectedWindow && actualWindow != IntPtr.Zero
                    ? "游戏窗口已失去前台焦点，已取消自定义短语发送。"
                    : failureReason ?? "无法确认前台游戏窗口。");
        }
        return bounds;
    }

    private async Task PressBindingAsync(
        PluginInputBinding binding,
        CancellationToken cancellationToken)
    {
        if (binding.Kind == PluginInputBindingKind.Mouse)
        {
            await ClickAsync(binding.MouseButton, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (binding.Kind != PluginInputBindingKind.Keyboard)
            throw new InvalidOperationException("聊天菜单按键类型不受支持。");

        await PressChordAsync(
            binding.VirtualKey,
            binding.Modifiers,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PressChordAsync(
        uint key,
        PluginInputModifiers modifiers,
        CancellationToken cancellationToken)
    {
        var modifierKeys = GetModifierKeys(modifiers).ToArray();
        foreach (var modifier in modifierKeys)
            EnsureSendInput(SendKey(modifier, keyUp: false), "修饰键按下");
        try
        {
            await PressKeyAsync(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            for (var index = modifierKeys.Length - 1; index >= 0; index--)
                EnsureSendInput(SendKey(modifierKeys[index], keyUp: true), "修饰键抬起");
        }
    }

    private async Task PressKeyAsync(
        uint key,
        CancellationToken cancellationToken)
    {
        EnsureSendInput(SendKey(key, keyUp: false), "按键按下");
        try
        {
            await JitteredDelayAsync(KeyHoldMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            EnsureSendInput(SendKey(key, keyUp: true), "按键抬起");
        }
    }

    private async Task ClickAsync(
        PluginMouseButton button,
        CancellationToken cancellationToken)
    {
        var flags = GetMouseFlags(button);
        EnsureSendInput(SendMouse(flags.Down, flags.Data), "鼠标按下");
        try
        {
            await JitteredDelayAsync(KeyHoldMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            EnsureSendInput(SendMouse(flags.Up, flags.Data), "鼠标抬起");
        }
    }

    private static IEnumerable<uint> GetModifierKeys(PluginInputModifiers modifiers)
    {
        if (modifiers.HasFlag(PluginInputModifiers.Control))
            yield return 0x11;
        if (modifiers.HasFlag(PluginInputModifiers.Alt))
            yield return 0x12;
        if (modifiers.HasFlag(PluginInputModifiers.Shift))
            yield return 0x10;
        if (modifiers.HasFlag(PluginInputModifiers.Windows))
            yield return 0x5B;
    }

    private static (uint Down, uint Up, uint Data) GetMouseFlags(PluginMouseButton button) =>
        button switch
        {
            PluginMouseButton.Left => (MouseeventfLeftdown, MouseeventfLeftup, 0),
            PluginMouseButton.Right => (MouseeventfRightdown, MouseeventfRightup, 0),
            PluginMouseButton.Middle => (MouseeventfMiddledown, MouseeventfMiddleup, 0),
            PluginMouseButton.XButton1 => (MouseeventfXdown, MouseeventfXup, 1u << 16),
            PluginMouseButton.XButton2 => (MouseeventfXdown, MouseeventfXup, 2u << 16),
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };

    private static void MoveCursor(int x, int y)
    {
        if (!SetCursorPos(x, y))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法移动鼠标到聊天框。");
    }

    private static bool SendKey(uint key, bool keyUp) =>
        SendInput(
            1,
            [new NativeInput
            {
                Type = InputKeyboard,
                Data = new NativeInputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = (ushort)key,
                        Flags = keyUp ? KeyeventfKeyup : 0,
                        ExtraInfo = InputMarker
                    }
                }
            }],
            Marshal.SizeOf<NativeInput>()) == 1;

    private static uint SendMouse(uint flags, uint mouseData) =>
        SendInput(
            1,
            [new NativeInput
            {
                Type = InputMouse,
                Data = new NativeInputUnion
                {
                    Mouse = new MouseInput
                    {
                        MouseData = mouseData,
                        Flags = flags,
                        ExtraInfo = InputMarker
                    }
                }
            }],
            Marshal.SizeOf<NativeInput>());

    private static void EnsureSendInput(uint sent, string action)
    {
        if (sent != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{action}失败。");
    }

    private static void EnsureSendInput(bool sent, string action)
    {
        if (!sent)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{action}失败。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
