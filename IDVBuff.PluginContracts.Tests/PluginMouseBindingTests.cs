using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;
using IDVBuff.Plugins.AutoClicker;
using IDVBuff.Plugins.AutoGatling;
using IDVBuff.Plugins.CustomPhrases;
using IDVBuff.Plugins.NoRecoveryDelay;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

[SupportedOSPlatform("windows")]
public sealed class PluginMouseBindingTests
{
    [Theory]
    [InlineData(PluginMouseButton.Left, 0x0202)]
    [InlineData(PluginMouseButton.Right, 0x0205)]
    [InlineData(PluginMouseButton.Middle, 0x0208)]
    [InlineData(PluginMouseButton.XButton1, 0x020C)]
    [InlineData(PluginMouseButton.XButton2, 0x020C)]
    public void ClickerHandoffDoesNotClearPhysicalHoldWhenMouseMarkerIsTruncated(
        PluginMouseButton button, int upMessage)
    {
        var service = new AutoClickerService(new AutoClickerOptions());
        service.ConfigureBindings(PluginInputBinding.Mouse(button), PluginInputBinding.Keyboard(0x46));
        var type = service.GetType();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var held = type.GetField("_physicalButtonDown", flags)!;
        var nativeType = type.GetNestedType("MsLlHookStruct", BindingFlags.NonPublic)!;
        var mouse = Activator.CreateInstance(nativeType)!;
        nativeType.GetField("MouseData")!.SetValue(mouse, button switch
        {
            PluginMouseButton.XButton1 => 1u << 16,
            PluginMouseButton.XButton2 => 2u << 16,
            _ => 0u
        });
        nativeType.GetField("Flags")!.SetValue(mouse, 1u);
        var callback = type.GetMethod("MouseHookCallback", flags)!;
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(nativeType));
        try
        {
            // Keep the service stopped: callbacks cannot install hooks or emit input.
            foreach (var marker in new[] { InputInjectionMarkers.HostGeneratedInput,
                InputInjectionMarkers.HostGeneratedMouseInput })
            {
                held.SetValue(service, true);
                nativeType.GetField("ExtraInfo")!.SetValue(mouse, new IntPtr(marker));
                Marshal.StructureToPtr(mouse, pointer, false);
                callback.Invoke(service, [0, new IntPtr(upMessage), pointer]);
                Assert.True((bool)held.GetValue(service)!);
            }
            // An unrelated driver-injected release must still end the hold.
            nativeType.GetField("ExtraInfo")!.SetValue(mouse, new IntPtr(123));
            Marshal.StructureToPtr(mouse, pointer, false);
            callback.Invoke(service, [0, new IntPtr(upMessage), pointer]);
            Assert.False((bool)held.GetValue(service)!);
            held.SetValue(service, true);
            nativeType.GetField("Flags")!.SetValue(mouse, 0u);
            nativeType.GetField("ExtraInfo")!.SetValue(mouse, IntPtr.Zero);
            Marshal.StructureToPtr(mouse, pointer, false);
            callback.Invoke(service, [0, new IntPtr(upMessage), pointer]);
            Assert.False((bool)held.GetValue(service)!);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [Theory]
    [InlineData(PluginMouseButton.Left, 0x0201u, 0x0202u, 0u, 2u, 4u)]
    [InlineData(PluginMouseButton.Right, 0x0204u, 0x0205u, 0u, 8u, 16u)]
    [InlineData(PluginMouseButton.Middle, 0x0207u, 0x0208u, 0u, 32u, 64u)]
    [InlineData(PluginMouseButton.XButton1, 0x020Bu, 0x020Cu, 1u, 128u, 256u)]
    [InlineData(PluginMouseButton.XButton2, 0x020Bu, 0x020Cu, 2u, 128u, 256u)]
    public void MouseMessagesAndInjectionUseTheirRespectiveDataLayouts(
        PluginMouseButton button, uint downMessage, uint upMessage,
        uint data, uint downFlags, uint upFlags)
    {
        Assert.True(PluginMouseInput.TryDecode(downMessage, data << 16, out var decoded, out var down));
        Assert.Equal(button, decoded);
        Assert.True(down);
        Assert.True(PluginMouseInput.TryDecode(upMessage, data << 16, out decoded, out down));
        Assert.Equal(button, decoded);
        Assert.False(down);
        Assert.Equal((downFlags, data), PluginMouseInput.Encode(button, true));
        Assert.Equal((upFlags, data), PluginMouseInput.Encode(button, false));
    }

    [Fact]
    public void UnknownSideButtonIsNotSilentlyAssignedToSideButtonOne()
    {
        Assert.False(PluginMouseInput.TryDecode(0x020B, 3u << 16, out _, out _));
        Assert.False(PluginMouseInput.TryDecode(0x020A, 120u << 16, out _, out _));
    }

    [Theory]
    [InlineData(PluginMouseButton.XButton1)]
    [InlineData(PluginMouseButton.XButton2)]
    public void BuiltInProvidersSaveAndRestoreSideButtons(PluginMouseButton button)
    {
        IPluginSettingsProvider[] plugins =
            [new AutoGatlingPlugin(), new NoRecoveryDelayPlugin(), new CustomPhrasePlugin(), new AutoClickerPlugin()];
        var value = PluginInputBinding.Mouse(button).StorageValue;
        foreach (var plugin in plugins)
        foreach (var setting in plugin.Settings.OfType<PluginKeyBindingSetting>())
        {
            Assert.True(setting.AllowedKinds.HasFlag(PluginInputBindingKinds.Mouse));
            plugin.SetSettingValue(setting.Key, value);
            Assert.Equal(value, plugin.GetSettingValue(setting.Key));
            plugin.SetSettingValue(setting.Key, "none");
            Assert.Equal("none", plugin.GetSettingValue(setting.Key));
            plugin.SetSettingValue(setting.Key, "keyboard:87:0"); // G HUB mapping to F24
            Assert.Equal("keyboard:87:0", plugin.GetSettingValue(setting.Key));
        }
    }

    [Theory]
    [InlineData(PluginMouseButton.XButton1)]
    [InlineData(PluginMouseButton.XButton2)]
    public void GatlingMouseTriggerConsumesMatchingPressAndReleaseOnly(PluginMouseButton button)
    {
        var service = new AutoGatlingService(new AutoGatlingOptions(), _ => { });
        var handler = typeof(AutoGatlingService).GetMethod("HandleMouseTrigger",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object[] args = [PluginInputBinding.Mouse(button), false, button, true, true];
        // Service is stopped: dispatch cannot start automation or install native hooks.
        Assert.True((bool)handler.Invoke(service, args)!);
        Assert.True((bool)args[1]);
        Assert.True((bool)handler.Invoke(service, args)!);
        args[3] = false;
        Assert.True((bool)handler.Invoke(service, args)!);
        Assert.False((bool)args[1]);
        Assert.False((bool)handler.Invoke(service, args)!);
        args[0] = PluginInputBinding.Keyboard(0x54);
        args[3] = true;
        Assert.False((bool)handler.Invoke(service, args)!);
    }

    [Theory]
    [InlineData(PluginMouseButton.XButton1)]
    [InlineData(PluginMouseButton.XButton2)]
    public void ServicesAcceptMouseBindingsWithoutInstallingHooks(PluginMouseButton button)
    {
        new AutoGatlingService(new AutoGatlingOptions(), _ => { }).ConfigureBindings(
            PluginInputBinding.Mouse(button), PluginInputBinding.Keyboard(0x86),
            PluginInputBinding.Keyboard(0x87));
        new NoRecoveryDelayService(new NoRecoveryDelayOptions(), _ => { }).ConfigureBindings(
            PluginInputBinding.Mouse(button), PluginInputBinding.Keyboard(0x87));
        new AutoClickerService(new AutoClickerOptions()).ConfigureBindings(
            PluginInputBinding.Keyboard(0x87), PluginInputBinding.Mouse(button));
    }

    [Theory]
    [InlineData(PluginMouseButton.XButton1, 1u)]
    [InlineData(PluginMouseButton.XButton2, 2u)]
    public void ClickerProducesNativeMouseDownAndUpWithoutSendingInput(PluginMouseButton button, uint data)
    {
        var service = new AutoClickerService(new AutoClickerOptions());
        service.ConfigureBindings(PluginInputBinding.Keyboard(0x87), PluginInputBinding.Mouse(button));
        var create = typeof(AutoClickerService).GetMethod("CreateOutputInput",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var up in new[] { false, true })
        {
            var input = create.Invoke(service, [up])!;
            Assert.Equal(0u, input.GetType().GetField("Type")!.GetValue(input));
            var union = input.GetType().GetField("Data")!.GetValue(input)!;
            var mouse = union.GetType().GetField("Mouse")!.GetValue(union)!;
            Assert.Equal(data, mouse.GetType().GetField("MouseData")!.GetValue(mouse));
            Assert.Equal(up ? 256u : 128u, mouse.GetType().GetField("Flags")!.GetValue(mouse));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DriverMappedInjectedKeyboardEventsReachTriggers(bool noRecovery)
    {
        object service;
        if (noRecovery)
        {
            var instance = new NoRecoveryDelayService(new NoRecoveryDelayOptions(), _ => { });
            instance.ConfigureBindings(PluginInputBinding.Keyboard(9), PluginInputBinding.Keyboard(0x87));
            service = instance;
        }
        else
        {
            var instance = new AutoGatlingService(new AutoGatlingOptions(), _ => { });
            instance.ConfigureBindings(PluginInputBinding.Keyboard(9), PluginInputBinding.Keyboard(0x87),
                PluginInputBinding.Keyboard(0x86));
            service = instance;
        }
        var type = service.GetType();
        var nativeType = type.GetNestedType("KbdLlHookStruct", BindingFlags.NonPublic)!;
        var keyboard = Activator.CreateInstance(nativeType)!;
        nativeType.GetField("VirtualKey")!.SetValue(keyboard, 0x87u);
        nativeType.GetField("Flags")!.SetValue(keyboard, 0x10u);
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(nativeType));
        try
        {
            Marshal.StructureToPtr(keyboard, pointer, false);
            var callback = type.GetMethod("KeyboardHookCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var held = type.GetField("_activateKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
            // Stopped services cannot launch automation. Only exercise event decoding and state.
            Assert.Equal(new IntPtr(1), callback.Invoke(service, [0, new IntPtr(0x100), pointer]));
            Assert.True((bool)held.GetValue(service)!);
            Assert.Equal(new IntPtr(1), callback.Invoke(service, [0, new IntPtr(0x101), pointer]));
            Assert.False((bool)held.GetValue(service)!);
            nativeType.GetField("ExtraInfo")!.SetValue(keyboard,
                new IntPtr(0x4944564255464652L));
            Marshal.StructureToPtr(keyboard, pointer, false);
            callback.Invoke(service, [0, new IntPtr(0x100), pointer]);
            Assert.False((bool)held.GetValue(service)!);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [Theory]
    [InlineData(false, PluginMouseButton.XButton1)]
    [InlineData(false, PluginMouseButton.XButton2)]
    [InlineData(true, PluginMouseButton.XButton1)]
    [InlineData(true, PluginMouseButton.XButton2)]
    public void NativeMouseCallbacksUpdateHoldStateAndIgnoreHostOutput(bool noRecovery, PluginMouseButton button)
    {
        object service;
        if (noRecovery)
        {
            var instance = new NoRecoveryDelayService(new NoRecoveryDelayOptions(), _ => { });
            instance.ConfigureBindings(PluginInputBinding.Keyboard(9), PluginInputBinding.Mouse(button));
            service = instance;
        }
        else
        {
            var instance = new AutoGatlingService(new AutoGatlingOptions(), _ => { });
            instance.ConfigureBindings(PluginInputBinding.Keyboard(9), PluginInputBinding.Mouse(button),
                PluginInputBinding.Keyboard(0x86));
            service = instance;
        }
        var type = service.GetType();
        var nativeType = type.GetNestedType("MsLlHookStruct", BindingFlags.NonPublic)!;
        var mouse = Activator.CreateInstance(nativeType)!;
        nativeType.GetField("MouseData")!.SetValue(mouse,
            (button == PluginMouseButton.XButton1 ? 1u : 2u) << 16);
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(nativeType));
        try
        {
            Marshal.StructureToPtr(mouse, pointer, false);
            var callback = type.GetMethod("MouseHookCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var held = type.GetField("_activateKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal(new IntPtr(1), callback.Invoke(service, [0, new IntPtr(0x20B), pointer]));
            Assert.True((bool)held.GetValue(service)!);
            Assert.Equal(new IntPtr(1), callback.Invoke(service, [0, new IntPtr(0x20C), pointer]));
            Assert.False((bool)held.GetValue(service)!);
            foreach (var marker in new[] { InputInjectionMarkers.HostGeneratedInput,
                InputInjectionMarkers.HostGeneratedMouseInput })
            {
                nativeType.GetField("ExtraInfo")!.SetValue(mouse, new IntPtr(marker));
                Marshal.StructureToPtr(mouse, pointer, false);
                callback.Invoke(service, [0, new IntPtr(0x20B), pointer]);
                Assert.False((bool)held.GetValue(service)!);
            }
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
