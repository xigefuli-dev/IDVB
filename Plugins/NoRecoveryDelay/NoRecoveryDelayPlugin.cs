using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.NoRecoveryDelay;

[Plugin("no-recovery-delay", DisplayName = "无后摇信仰",
    Description = "在两个背包武器间交替拖放，以消除攻击后摇。", Version = "1.0.0")]
public sealed class NoRecoveryDelayPlugin : PluginBase, IPluginSettingsProvider
{
    private static readonly string[] EquipmentSlots = ["1", "2", "3", "4"];
    private static readonly string[] InventorySlots = ["1", "2", "3", "4", "5", "6"];
    private static readonly string[] LoopModes = ["按住循环", "轮次循环"];
    private readonly NoRecoveryDelayOptions _options = new();
    private readonly NoRecoveryDelayService _service;
    private PluginInputBinding _inventoryBinding = PluginInputBinding.Keyboard(0x09);
    private PluginInputBinding _activateBinding = PluginInputBinding.Keyboard(0x54);
    private bool _enabled;

    public NoRecoveryDelayPlugin() => _service = new(_options, message => Context.Logger.Info(message));
    public override string Id => "no-recovery-delay";
    public override string DisplayName => "无后摇信仰";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        Choice("equipment-slot", "装备格序号（1-4）", "Shape3 中从左到右的装备格。", EquipmentSlots, 0),
        Choice("inventory-slot-1", "背包内武器序号①", "Shape1 与 Shape2 合并后的第 1–6 格；不得与②重复。", InventorySlots, 0),
        Choice("inventory-slot-2", "背包内武器序号②", "Shape1 与 Shape2 合并后的第 1–6 格；不得与①重复。", InventorySlots, 1),
        Choice("loop-mode", "循环方式", null, LoopModes, 0),
        new PluginSliderSetting { Key = "loop-count", DisplayName = "循环次数",
            Description = "轮次循环的总轮次，每完成一次①或②算一轮。", Minimum = 1, Maximum = 20,
            StepFrequency = 1, DefaultValue = 1, VisibleWhenKey = "loop-mode", VisibleWhenValue = "轮次循环" },
        new PluginKeyBindingSetting { Key = "inventory-binding", DisplayName = "背包按键",
            Description = "打开或关闭背包的游戏内按键，默认为 Tab。", DefaultValue = "keyboard:9:0",
            AllowedKinds = PluginInputBindingKinds.Keyboard },
        new PluginKeyBindingSetting { Key = "activate-binding", DisplayName = "激活无后摇",
            Description = "按住循环或触发指定轮次，默认为 T。", DefaultValue = "keyboard:54:0",
            AllowedKinds = PluginInputBindingKinds.Keyboard },
        Delay("standard-delay-ms", "通用延迟（毫秒）", 50, 1),
        Delay("key-press-delay-ms", "按键保持（毫秒）", 10, 10),
        Delay("drag-delay-ms", "拖动时长（毫秒）", 50, 25),
        Delay("minimum-random-delay-ms", "随机延迟下限（毫秒）", 30, 30, 0),
        Delay("maximum-random-delay-ms", "随机延迟上限（毫秒）", 50, 50, 0)
    ];

    public override void OnStart()
    {
        _enabled = true;
        try { ConfigureAndStart(); Context.Logger.Info("无后摇信仰已启动；游戏客户区必须为精确 16:9 或 16:10。"); }
        catch (Exception ex) { Context.Logger.Error($"无后摇信仰启动失败：{ex.Message}"); }
    }
    public override void OnDisable() { _enabled = false; _service.Stop(); }
    public override void OnUnload() { _enabled = false; _service.Dispose(); }

    public object? GetSettingValue(string key) => key switch
    {
        "equipment-slot" => _options.EquipmentSlot.ToString(),
        "inventory-slot-1" => _options.InventorySlot1.ToString(),
        "inventory-slot-2" => _options.InventorySlot2.ToString(),
        "loop-mode" => _options.LoopMode == NoRecoveryDelayLoopMode.Hold ? LoopModes[0] : LoopModes[1],
        "loop-count" => (double)_options.LoopCount,
        "inventory-binding" => _inventoryBinding.StorageValue,
        "activate-binding" => _activateBinding.StorageValue,
        "standard-delay-ms" => (double)_options.StandardDelayMilliseconds,
        "key-press-delay-ms" => (double)_options.KeyPressDelayMilliseconds,
        "drag-delay-ms" => (double)_options.DragDelayMilliseconds,
        "minimum-random-delay-ms" => (double)_options.MinimumRandomDelayMilliseconds,
        "maximum-random-delay-ms" => (double)_options.MaximumRandomDelayMilliseconds,
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        if (value is string text)
        {
            if (key == "equipment-slot" && int.TryParse(text, out var equipment)) _options.EquipmentSlot = Math.Clamp(equipment, 1, 4);
            else if (key == "inventory-slot-1" && int.TryParse(text, out var first) && first != _options.InventorySlot2) _options.InventorySlot1 = Math.Clamp(first, 1, 6);
            else if (key == "inventory-slot-2" && int.TryParse(text, out var second) && second != _options.InventorySlot1) _options.InventorySlot2 = Math.Clamp(second, 1, 6);
            else if (key == "loop-mode" && Array.IndexOf(LoopModes, text) is var mode && mode >= 0) _options.LoopMode = (NoRecoveryDelayLoopMode)mode;
            else if (PluginInputBinding.TryParse(text, PluginInputBindingKinds.Keyboard, out var binding))
            {
                if (key == "inventory-binding") _inventoryBinding = binding;
                else if (key == "activate-binding") _activateBinding = binding;
                else return;
                if (_enabled) ConfigureAndStart();
            }
            return;
        }
        if (!TryNumber(value, out var number)) return;
        switch (key)
        {
            case "loop-count": _options.LoopCount = Math.Clamp(number, 1, 20); break;
            case "standard-delay-ms": _options.StandardDelayMilliseconds = Math.Clamp(number, 1, 10000); break;
            case "key-press-delay-ms": _options.KeyPressDelayMilliseconds = Math.Clamp(number, 10, 10000); break;
            case "drag-delay-ms": _options.DragDelayMilliseconds = Math.Clamp(number, 25, 10000); break;
            case "minimum-random-delay-ms": _options.MinimumRandomDelayMilliseconds = Math.Clamp(number, PluginRandomDelayPolicy.GetMinimum(30), 10000); break;
            case "maximum-random-delay-ms": _options.MaximumRandomDelayMilliseconds = Math.Clamp(number, PluginRandomDelayPolicy.GetMinimum(50), 10000); break;
        }
    }

    private void ConfigureAndStart() { _service.ConfigureBindings(_inventoryBinding, _activateBinding); _service.Start(); }
    private static bool TryNumber(object? value, out int number)
    { if (value is double d && double.IsFinite(d)) { number = (int)Math.Round(d); return true; } if (value is int i) { number = i; return true; } if (value is long l) { number = (int)Math.Clamp(l, int.MinValue, int.MaxValue); return true; } number = 0; return false; }
    private static PluginChoiceSetting Choice(string key, string name, string? description, string[] options, int index) =>
        new() { Key = key, DisplayName = name, Description = description, Options = options, DefaultIndex = index };
    private static PluginSliderSetting Delay(string key, string name, double value, double minimum,
        double? minimumWhenUnsafe = null) => new() { Key = key, DisplayName = name, Minimum = minimum,
            MinimumWhenUnsafe = minimumWhenUnsafe, Maximum = 10000, StepFrequency = 1, DefaultValue = value };
}
