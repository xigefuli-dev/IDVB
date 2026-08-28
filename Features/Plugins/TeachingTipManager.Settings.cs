using System.Text.Json;
using IDVBuff.PluginContracts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace IDVBuff.Features.Plugins;

public sealed partial class TeachingTipManager
{
    private FrameworkElement? BuildSettingRow(
        IPluginSettingsProvider provider,
        string pluginId,
        IPluginSetting setting,
        ICollection<(NumberBox Input, Action Commit)> numericEditors,
        ICollection<Action> textEditors,
        Action endNumericEditing,
        Action refreshVisibility)
    {
        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(new TextBlock
        {
            Text = setting.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
        });

        switch (setting)
        {
            case PluginToggleSetting toggle:
                {
                    var initial = ReadProviderValue(provider, setting) as bool?
                        ?? toggle.DefaultValue;
                    var control = new ToggleSwitch
                    {
                        OnContent = "开",
                        OffContent = "关",
                        IsOn = initial
                    };
                    control.Toggled += (_, _) =>
                        PersistSetting(provider, pluginId, setting.Key,
                            JsonSerializer.SerializeToElement(control.IsOn));
                    row.Children.Add(control);
                    break;
                }
            case PluginSliderSetting slider:
                {
                    if (slider.Maximum < slider.Minimum
                        || double.IsNaN(slider.Minimum) || double.IsNaN(slider.Maximum))
                        break;
                    var initial = ReadProviderValue(provider, setting) switch
                    {
                        double d => d,
                        long l => (double)l,
                        int i => (double)i,
                        _ => slider.DefaultValue
                    };
                    initial = CoerceSlider(initial, slider);
                    var valueInput = new NumberBox
                    {
                        Value = initial,
                        Minimum = slider.Minimum,
                        Maximum = slider.Maximum,
                        SmallChange = slider.StepFrequency > 0 ? slider.StepFrequency : 1,
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                        Width = 88,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var control = new Slider
                    {
                        Minimum = slider.Minimum,
                        Maximum = slider.Maximum,
                        StepFrequency = Math.Max(0, Math.Min(
                            slider.StepFrequency, slider.Maximum - slider.Minimum)),
                        SnapsTo = SliderSnapsTo.StepValues,
                        Value = initial
                    };
                    var sliderRow = new Grid { ColumnSpacing = 12 };
                    sliderRow.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star)
                    });
                    sliderRow.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = GridLength.Auto
                    });
                    Grid.SetColumn(control, 0);
                    Grid.SetColumn(valueInput, 1);
                    sliderRow.Children.Add(control);
                    sliderRow.Children.Add(valueInput);
                    control.ValueChanged += (_, e) =>
                    {
                        var snapped = SnapSliderValue(e.NewValue, slider);
                        valueInput.Value = snapped;
                        PersistSetting(provider, pluginId, setting.Key,
                            JsonSerializer.SerializeToElement(snapped));
                    };
                    void CommitNumberInput()
                    {
                        var requested = PluginNumericInput.TryGetValue(valueInput, out var typedValue)
                            ? typedValue
                            : double.IsFinite(valueInput.Value)
                                ? valueInput.Value
                                : control.Value;
                        var snapped = SnapSliderValue(requested, slider);
                        valueInput.Value = snapped;
                        control.Value = snapped;
                        PersistSetting(provider, pluginId, setting.Key,
                            JsonSerializer.SerializeToElement(snapped));
                    }
                    valueInput.LostFocus += (_, _) => CommitNumberInput();
                    numericEditors.Add((valueInput, CommitNumberInput));
                    PluginNumericInput.Attach(valueInput, typedValue => PersistSetting(provider,
                        pluginId, setting.Key, JsonSerializer.SerializeToElement(SnapSliderValue(typedValue, slider))), () =>
                    {
                        CommitNumberInput();
                        endNumericEditing();
                    }, endNumericEditing);
                    row.Children.Add(sliderRow);
                    break;
                }
            case PluginChoiceSetting choice:
                {
                    if (choice.Options.Length == 0)
                        break;
                    var raw = ReadProviderValue(provider, setting);
                    var selectedIndex = raw is string s
                        && Array.IndexOf(choice.Options, s) >= 0
                            ? Array.IndexOf(choice.Options, s)
                            : choice.DefaultIndex;
                    selectedIndex = Math.Clamp(selectedIndex, 0, choice.Options.Length - 1);
                    var control = new ComboBox
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        MinWidth = 200
                    };
                    foreach (var option in choice.Options)
                        control.Items.Add(option);
                    control.SelectedIndex = selectedIndex;
                    control.SelectionChanged += (_, _) =>
                    {
                        if (control.SelectedIndex >= 0
                            && control.SelectedIndex < choice.Options.Length)
                        {
                            PersistSetting(provider, pluginId, setting.Key,
                                JsonSerializer.SerializeToElement(
                                    choice.Options[control.SelectedIndex]));
                            refreshVisibility();
                        }
                    };
                    row.Children.Add(control);
                    break;
                }
            case PluginKeyBindingSetting binding:
                row.Children.Add(BuildKeyBindingControl(provider, pluginId, binding));
                break;
            case PluginTextSetting text:
                {
                    if (string.Equals(pluginId, "custom-phrases", StringComparison.Ordinal)
                        && string.Equals(setting.Key, "phrases", StringComparison.Ordinal))
                    {
                        row.Children.Add(BuildPhraseListEditor(
                            provider, pluginId, text, textEditors));
                        break;
                    }

                    var initial = ReadProviderValue(provider, setting) as string
                        ?? text.DefaultValue;
                    initial = text.Coerce(initial);
                    var control = new TextBox
                    {
                        Text = initial,
                        PlaceholderText = text.PlaceholderText,
                        AcceptsReturn = text.Multiline,
                        TextWrapping = text.Multiline
                            ? TextWrapping.Wrap
                            : TextWrapping.NoWrap,
                        MaxLength = Math.Max(1, text.MaxLength),
                        Height = text.Multiline ? 132 : double.NaN,
                        MinWidth = 200,
                        VerticalContentAlignment = text.Multiline
                            ? VerticalAlignment.Top
                            : VerticalAlignment.Center
                    };
                    void CommitText()
                    {
                        var value = text.Coerce(control.Text);
                        // 只规范化写入值，不要把规范化结果重新赋回正在编辑的控件。
                        // WinUI TextBox 在输入换行时可能暂时使用不同的换行表示；此时
                        // 重设 Text 会重置 SelectionStart，导致光标跳到开头并破坏后续输入。
                        PersistSetting(provider, pluginId, setting.Key,
                            JsonSerializer.SerializeToElement(value));
                    }
                    // 编辑过程中只同步插件内存态，不要每个字符都同步改写设置文件。
                    // 尤其是多行框刚输入 Enter 时，尾部空行属于合法的中间编辑态；如果
                    // 此时经过插件的提交态规范化再参与后续生命周期恢复，下一行会被吞掉。
                    // LostFocus 与 TeachingTip 统一关闭提交仍保证最终完整文本原子落盘。
                    control.TextChanged += (_, _) =>
                        SafeSetProviderValue(provider, setting.Key,
                            text.Coerce(control.Text));
                    control.LostFocus += (_, _) => CommitText();
                    textEditors.Add(CommitText);
                    row.Children.Add(control);
                    break;
                }
            default:
                row.Children.Add(new TextBlock
                {
                    Text = $"不支持的设置类型：{setting.GetType().Name}",
                    FontSize = 12,
                    Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
                });
                break;
        }

        if (!string.IsNullOrWhiteSpace(setting.Description))
        {
            row.Children.Add(new TextBlock
            {
                Text = setting.Description,
                FontSize = 12,
                Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }
        return row;
    }

    private FrameworkElement BuildPhraseListEditor(
        IPluginSettingsProvider provider,
        string pluginId,
        PluginTextSetting setting,
        ICollection<Action> textEditors)
    {
        const int maximumPhraseCount = 30;
        var initial = (ReadProviderValue(provider, setting) as string
                ?? setting.DefaultValue)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static phrase => phrase.Trim())
            .Where(static phrase => phrase.Length > 0)
            .Take(maximumPhraseCount)
            .ToList();
        if (initial.Count == 0)
            initial.Add(string.Empty);

        var phrases = initial;
        var editors = new List<TextBox>();
        var list = new StackPanel { Spacing = 8 };

        string CurrentValue() => string.Join(Environment.NewLine, phrases);

        void Commit() => PersistSetting(
            provider,
            pluginId,
            setting.Key,
            JsonSerializer.SerializeToElement(CurrentValue()));

        void Rebuild()
        {
            list.Children.Clear();
            editors.Clear();
            for (var phraseIndex = 0; phraseIndex < phrases.Count; phraseIndex++)
            {
                var itemIndex = phraseIndex;
                var input = new TextBox
                {
                    Text = phrases[itemIndex],
                    PlaceholderText = "输入短语",
                    AcceptsReturn = false,
                    TextWrapping = TextWrapping.NoWrap,
                    MaxLength = Math.Max(1, setting.MaxLength),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                editors.Add(input);

                var line = new Grid { ColumnSpacing = 8 };
                line.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var delete = new Button
                {
                    Content = new SymbolIcon(Symbol.Delete),
                    IsEnabled = phrases.Count > 1,
                    Padding = new Thickness(10),
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                delete.Click += (_, _) =>
                {
                    if (phrases.Count <= 1 || itemIndex >= phrases.Count)
                        return;
                    phrases.RemoveAt(itemIndex);
                    Rebuild();
                    Commit();
                };
                input.TextChanged += (_, _) =>
                {
                    if (itemIndex >= phrases.Count)
                        return;
                    phrases[itemIndex] = input.Text;
                    Commit();
                };
                Grid.SetColumn(input, 0);
                Grid.SetColumn(delete, 1);
                line.Children.Add(input);
                line.Children.Add(delete);
                list.Children.Add(line);
            }

            var create = new Button
            {
                Content = "+  创建短语",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsEnabled = phrases.Count < maximumPhraseCount
            };
            create.Click += (_, _) =>
            {
                if (phrases.Count >= maximumPhraseCount)
                    return;
                phrases.Add(string.Empty);
                Rebuild();
                Commit();
                editors[^1].Focus(FocusState.Programmatic);
            };
            list.Children.Add(create);
        }

        Rebuild();
        textEditors.Add(Commit);
        return list;
    }
}
