using System.Runtime.Versioning;
using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.CustomPhrases;

[SupportedOSPlatform("windows")]
[Plugin(
    "custom-phrases",
    DisplayName = "自定义短语",
    Description = "按住菜单键选择短语，自动打开聊天菜单、粘贴并发送。",
    Version = "1.0.0")]
public sealed class CustomPhrasePlugin : PluginBase, IPluginSettingsProvider
{
    public const string ChatMenuBindingKey = "chat-menu-binding";
    public const string EnableMouseBindingKey = "enable-mouse-binding";
    public const string PhraseMenuBindingKey = "phrase-menu-binding";
    public const string PhrasesKey = "phrases";
    public const string MinimumRandomDelayKey = "minimum-random-delay-ms";
    public const string MaximumRandomDelayKey = "maximum-random-delay-ms";

    private readonly CustomPhraseOverlay _overlay = new();
    private readonly CustomPhraseOptions _options = new();
    private string _phrases = string.Empty;
    private PluginInputBinding _chatMenuBinding = PluginInputBinding.Keyboard(0x0D);
    private PluginInputBinding _enableMouseBinding = PluginInputBinding.Keyboard(0xC0);
    private PluginInputBinding _phraseMenuBinding = PluginInputBinding.Keyboard(0x5D);
    private IPluginInputService? _input;
    private IPluginGameWindowService? _gameWindow;
    private CustomPhraseAutomation? _automation;
    private long _sendCooldownUntilTick;
    private CancellationTokenSource? _sendCancellation;
    private CancellationTokenSource? _menuCancellation;
    private string[]? _activePhrases;
    private bool _enabled;
    private bool _menuHeld;

    public override string Id => "custom-phrases";

    public override string DisplayName => "自定义短语";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginKeyBindingSetting
        {
            Key = ChatMenuBindingKey,
            DisplayName = "聊天菜单按键",
            Description = "自动发送时用于打开聊天菜单的按键，默认为回车。",
            DefaultValue = "keyboard:D:0",
            AllowedKinds = PluginInputBindingKinds.Keyboard
        },
        new PluginKeyBindingSetting
        {
            Key = EnableMouseBindingKey,
            DisplayName = "启用鼠标按键",
            Description = "打开短语菜单前自动按下此键以启用游戏鼠标，默认为 ` 键。",
            DefaultValue = "keyboard:C0:0",
            AllowedKinds = PluginInputBindingKinds.Keyboard
        },
        new PluginKeyBindingSetting
        {
            Key = PhraseMenuBindingKey,
            DisplayName = "启动自定义短语菜单按键",
            Description = "按住此键显示短语序列，松开后发送当前高亮短语；默认为键盘菜单键。",
            DefaultValue = "keyboard:5D:0",
            AllowedKinds = PluginInputBindingKinds.Keyboard
        },
        new PluginSliderSetting
        {
            Key = MinimumRandomDelayKey,
            DisplayName = "随机延迟下限（毫秒）",
            Description = "每个自动输入操作都会追加此范围内的随机延迟。",
            Minimum = CustomPhraseOptions.MinimumRandomDelayMillisecondsAllowed,
            MinimumWhenUnsafe = 0,
            Maximum = CustomPhraseOptions.MaximumDelayMilliseconds,
            StepFrequency = 1,
            DefaultValue = 30
        },
        new PluginSliderSetting
        {
            Key = MaximumRandomDelayKey,
            DisplayName = "随机延迟上限（毫秒）",
            Description = "同样受到主设置中的低延迟安全选项监管。",
            Minimum = CustomPhraseOptions.MinimumRandomDelayUpperBoundMillisecondsAllowed,
            MinimumWhenUnsafe = 0,
            Maximum = CustomPhraseOptions.MaximumDelayMilliseconds,
            StepFrequency = 1,
            DefaultValue = 50
        },
        new PluginTextSetting
        {
            Key = PhrasesKey,
            DisplayName = "自定义短语（最多 30 条）",
            Description = "每行输入一条短语。矩形中最多显示 5 个字，超出部分显示为…；实际发送内容不会被截断。",
            DefaultValue = "",
            Multiline = false,
            MaxLength = 4096,
            MaxLineCount = CustomPhrasePluginData.MaxPhraseCount,
            PlaceholderText = "例如：集合\n注意左侧\n准备撤离"
        }
    ];

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);
        _input = context.GetService<IPluginInputService>();
        _gameWindow = context.GetService<IPluginGameWindowService>();
        if (_input is null)
            context.Logger.Error("无法取得插件输入服务，自定义短语不可用。");
        if (_gameWindow is null)
            context.Logger.Error("无法取得游戏窗口服务，自定义短语不可用。");
        if (_input is not null)
            _input.BindingInvoked += OnBindingInvoked;
        if (_gameWindow is not null)
            _automation = new CustomPhraseAutomation(
                _gameWindow,
                message => Context.Logger.Info(message),
                _options);
    }

    public override void OnEnable()
    {
        _enabled = true;
        ApplyBindings();
    }

    public override void OnDisable()
    {
        _enabled = false;
        _menuHeld = false;
        CancelMenuOpening();
        _activePhrases = null;
        _input?.ClearBindings(Context.PluginId);
        _overlay.Hide();
        CancelSending();
    }

    public override void OnUnload()
    {
        _enabled = false;
        _menuHeld = false;
        _activePhrases = null;
        CancelMenuOpening();
        if (_input is not null)
        {
            _input.BindingInvoked -= OnBindingInvoked;
            _input.ClearBindings(Context.PluginId);
        }
        _overlay.Dispose();
        CancelSending();
        _automation = null;
        _input = null;
        _gameWindow = null;
    }

    public object? GetSettingValue(string key) => key switch
    {
        ChatMenuBindingKey => _chatMenuBinding.StorageValue,
        EnableMouseBindingKey => _enableMouseBinding.StorageValue,
        PhraseMenuBindingKey => _phraseMenuBinding.StorageValue,
        MinimumRandomDelayKey => (double)_options.MinimumRandomDelayMilliseconds,
        MaximumRandomDelayKey => (double)_options.MaximumRandomDelayMilliseconds,
        PhrasesKey => _phrases,
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        if (key == PhrasesKey && value is string phrases)
        {
            // 保留编辑框中的换行（包括用户刚按 Enter 产生的尾部空行）。过滤空行、
            // Trim 和最多 30 条的运行时语义只应在真正打开短语菜单时执行。
            _phrases = CustomPhrasePluginData.CoerceEditorText(phrases);
            return;
        }

        if (value is double number)
        {
            switch (key)
            {
                case MinimumRandomDelayKey:
                    _options.MinimumRandomDelayMilliseconds = (int)Math.Round(number);
                    return;
                case MaximumRandomDelayKey:
                    _options.MaximumRandomDelayMilliseconds = (int)Math.Round(number);
                    return;
            }
        }

        if (value is not string text
            || !PluginInputBinding.TryParse(
                text,
                PluginInputBindingKinds.Keyboard,
                out var binding))
        {
            return;
        }

        switch (key)
        {
            case ChatMenuBindingKey:
                _chatMenuBinding = binding;
                break;
            case PhraseMenuBindingKey:
                _phraseMenuBinding = binding;
                break;
            case EnableMouseBindingKey:
                _enableMouseBinding = binding;
                break;
            default:
                return;
        }

        if (_enabled)
            ApplyBindings();
    }

    private void ApplyBindings()
    {
        if (_input is null)
            return;
        _input.SetBinding(Context.PluginId, ChatMenuBindingKey, _chatMenuBinding);
        _input.SetBinding(Context.PluginId, EnableMouseBindingKey, _enableMouseBinding);
        _input.SetBinding(Context.PluginId, PhraseMenuBindingKey, _phraseMenuBinding);
    }

    private void OnBindingInvoked(object? sender, PluginInputEventArgs args)
    {
        if (!_enabled
            || !string.Equals(args.PluginId, Context.PluginId, StringComparison.Ordinal)
            || !string.Equals(args.BindingKey, PhraseMenuBindingKey, StringComparison.Ordinal))
        {
            return;
        }

        if (args.IsDown)
            BeginPhraseMenu();
        else
            EndPhraseMenu();
    }

    private async void BeginPhraseMenu()
    {
        if (_menuHeld)
            return;
        if (Environment.TickCount64 < Interlocked.Read(ref _sendCooldownUntilTick))
            return;

        var phrases = CustomPhrasePluginData.ParsePhrases(_phrases);
        if (phrases.Count == 0)
        {
            Context.Logger.Warning("自定义短语未执行：请先在插件设置中填写至少一条短语。");
            return;
        }
        if (_gameWindow is null)
        {
            Context.Logger.Warning("自定义短语菜单未显示：当前没有可用的游戏窗口服务。");
            return;
        }
        if (!_gameWindow.TryGetForegroundClientBounds(
                out _,
                out var windowHandle,
                out var failureReason))
        {
            Context.Logger.Warning(
                $"自定义短语菜单未显示：{failureReason ?? "当前没有可用的游戏窗口。"}");
            return;
        }

        _menuHeld = true;
        _activePhrases = phrases.ToArray();
        CancelMenuOpening();
        var cancellation = new CancellationTokenSource();
        _menuCancellation = cancellation;
        try
        {
            if (_automation is null)
                return;
            // 不使用 Task.Run：调用方位于 WinUI Dispatcher，await 后必须回到同一
            // 线程创建/显示 Win32 覆盖层，避免跨线程窗口生命周期死锁。
            await _automation.EnableMouseAsync(
                _enableMouseBinding.Clone(),
                windowHandle,
                cancellation.Token);
            if (!_menuHeld || cancellation.IsCancellationRequested)
                return;
            if (_gameWindow is null
                || !_gameWindow.TryGetForegroundClientBounds(
                    out var currentBounds,
                    out var currentWindow,
                    out failureReason)
                || currentWindow != windowHandle)
            {
                throw new InvalidOperationException(
                    failureReason ?? "游戏窗口已失去前台焦点。");
            }
            _overlay.Show(_activePhrases ?? [], currentBounds);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _menuHeld = false;
            _activePhrases = null;
            _overlay.Hide();
            Context.Logger.Error($"自定义短语菜单显示失败：{exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_menuCancellation, cancellation))
                _menuCancellation = null;
            cancellation.Dispose();
        }
    }

    private void EndPhraseMenu()
    {
        if (!_menuHeld)
            return;

        _menuHeld = false;
        CancelMenuOpening();
        var selectedIndex = _overlay.Hide();
        var phrases = _activePhrases;
        _activePhrases = null;
        if (phrases is null)
            return;
        if (selectedIndex < 0 || selectedIndex >= phrases.Length)
        {
            EnableMouseAfterEmptySelection();
            return;
        }

        var automation = _automation;
        var gameWindow = _gameWindow;
        if (automation is null || gameWindow is null)
            return;
        if (!gameWindow.TryGetForegroundClientBounds(
                out _,
                out var windowHandle,
                out var failureReason))
        {
            Context.Logger.Warning(
                $"自定义短语未发送：{failureReason ?? "当前没有可用的游戏窗口。"}");
            return;
        }

        CancelSending();
        var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        var phrase = phrases[selectedIndex];
        var chatBinding = _chatMenuBinding.Clone();
        _ = Task.Run(async () =>
        {
            try
            {
                await automation.SendAsync(
                    phrase,
                    chatBinding,
                    windowHandle,
                    cancellation.Token).ConfigureAwait(false);
                Interlocked.Exchange(
                    ref _sendCooldownUntilTick,
                    Environment.TickCount64 + CustomPhrasePluginData.SendCooldownMilliseconds);
            }
            catch (OperationCanceledException)
            {
                Context.Logger.Info("自定义短语发送已取消。");
            }
            catch (Exception exception)
            {
                Context.Logger.Error($"自定义短语发送失败：{exception.Message}");
            }
            finally
            {
                if (ReferenceEquals(_sendCancellation, cancellation))
                    _sendCancellation = null;
                cancellation.Dispose();
            }
        });
    }

    private void EnableMouseAfterEmptySelection()
    {
        var automation = _automation;
        var gameWindow = _gameWindow;
        if (automation is null || gameWindow is null)
            return;
        if (!gameWindow.TryGetForegroundClientBounds(
                out _,
                out var windowHandle,
                out var failureReason))
        {
            Context.Logger.Warning(
                $"自定义短语菜单取消后未能恢复鼠标：{failureReason ?? "当前没有可用的游戏窗口。"}");
            return;
        }

        CancelSending();
        var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        var enableMouseBinding = _enableMouseBinding.Clone();
        _ = Task.Run(async () =>
        {
            try
            {
                await automation.EnableMouseAsync(
                    enableMouseBinding,
                    windowHandle,
                    cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Context.Logger.Error($"自定义短语菜单取消后恢复鼠标失败：{exception.Message}");
            }
            finally
            {
                if (ReferenceEquals(_sendCancellation, cancellation))
                    _sendCancellation = null;
                cancellation.Dispose();
            }
        });
    }

    private void CancelSending()
    {
        var cancellation = _sendCancellation;
        _sendCancellation = null;
        cancellation?.Cancel();
    }

    private void CancelMenuOpening()
    {
        var cancellation = _menuCancellation;
        _menuCancellation = null;
        cancellation?.Cancel();
    }
}
