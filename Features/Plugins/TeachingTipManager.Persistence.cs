using System.Text.Json;
using IDVBuff.PluginContracts;

namespace IDVBuff.Features.Plugins;

public sealed partial class TeachingTipManager
{
    private void RestorePersistedValues(IPluginSettingsProvider provider, string pluginId)
    {
        foreach (var setting in provider.Settings)
        {
            object? value;
            if (_store.TryGetSetting(pluginId, setting.Key, out var stored)
                && TryRestore(setting, stored, out var restored))
            {
                value = restored;
            }
            else
            {
                value = DefaultFor(setting);
            }
            if (value is not null)
                SafeSetProviderValue(provider, setting.Key, value);
        }
    }

    private static object? DefaultFor(IPluginSetting setting) => setting switch
    {
        PluginToggleSetting toggle => toggle.DefaultValue,
        PluginSliderSetting slider => slider.DefaultValue,
        // 空 Options 的 choice 无默认值可取：返回 null，调用方跳过写回。
        // 与 BuildSettingRow 的空 Options 跳过渲染保持一致，避免 RestorePersistedValues
        // 在此处抛 InvalidOperationException 冒泡到未处理的 XAML 事件。
        PluginChoiceSetting choice => choice.Options.Length > 0 ? choice.DefaultValue : null,
        PluginKeyBindingSetting binding =>
            PluginInputBinding.TryParse(
                binding.DefaultValue,
                binding.AllowedKinds,
                out _)
                ? binding.DefaultValue
                : null,
        PluginTextSetting text => text.Coerce(text.DefaultValue),
        _ => null
    };

    /// <summary>把存储的 JsonElement 还原为符合描述符类型的 CLR 值；类型不符返回 false。</summary>
    private static bool TryRestore(IPluginSetting setting, JsonElement stored, out object? value)
    {
        switch (setting)
        {
            case PluginToggleSetting:
                if (stored.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    value = stored.GetBoolean();
                    return true;
                }
                break;
            case PluginSliderSetting slider:
                if (stored.ValueKind == JsonValueKind.Number
                    && stored.TryGetDouble(out var raw))
                {
                    value = CoerceSlider(raw, slider);
                    return true;
                }
                break;
            case PluginChoiceSetting choice:
                if (stored.ValueKind == JsonValueKind.String
                    && stored.GetString() is { } text
                    && choice.Options.Contains(text, StringComparer.Ordinal))
                {
                    value = text;
                    return true;
                }
                break;
            case PluginKeyBindingSetting binding:
                if (stored.ValueKind == JsonValueKind.String
                    && stored.GetString() is { } bindingText
                    && PluginInputBinding.TryParse(
                        bindingText,
                        binding.AllowedKinds,
                        out _))
                {
                    value = bindingText;
                    return true;
                }
                break;
            case PluginTextSetting textSetting:
                if (stored.ValueKind == JsonValueKind.String
                    && stored.GetString() is { } textValue)
                {
                    value = textSetting.Coerce(textValue);
                    return true;
                }
                break;
        }
        value = null;
        return false;
    }

    private void SafeSetProviderValue(IPluginSettingsProvider provider, string key, object? value)
    {
        try
        {
            provider.SetSettingValue(key, value);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"TTM 写回插件设置失败 {key}: {exception}");
        }
    }

}
