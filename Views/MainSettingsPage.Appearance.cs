using IDVBuff.Appearance;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MainSettingsPage
{
    private FrameworkElement CreateAppearanceSettings()
    {
        var content = new StackPanel { Spacing = 16 };
        var definitions = ThemeRegistry.BuiltIn.Definitions.ToArray();
        var preset = new ComboBox { MinWidth = 140, ItemsSource = definitions, DisplayMemberPath = nameof(ThemeDefinition.DisplayName) };
        var mode = new ComboBox { MinWidth = 140 };
        foreach (var label in new[] { "跟随系统", "浅色", "深色" }) mode.Items.Add(label);
        var material = new ComboBox { MinWidth = 140 };
        foreach (var label in new[] { "纯色", "玻璃" }) material.Items.Add(label);
        var accent = new ComboBox { MinWidth = 140 };
        foreach (var label in new[] { "Windows 强调色", "方案默认色", "自定义色" }) accent.Items.Add(label);
        var custom = new TextBox { PlaceholderText = "#RRGGBB", MaxLength = 7, MinWidth = 120 };
        var applyColor = new Button { Content = "应用颜色" };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true };
        var updating = false;

        void Restore()
        {
            updating = true;
            var preferences = ThemeService.Preferences;
            preset.SelectedItem = definitions.First(definition => definition.Id == preferences.ThemeId);
            mode.SelectedIndex = (int)preferences.Mode;
            material.SelectedIndex = (int)preferences.Material;
            accent.SelectedIndex = (int)preferences.AccentSource;
            custom.Text = preferences.CustomAccent ?? "#245DD8";
            custom.IsEnabled = accent.SelectedIndex == (int)AccentSource.Custom;
            applyColor.IsEnabled = custom.IsEnabled;
            updating = false;
        }

        void Apply()
        {
            if (updating) return;
            try
            {
                ThemeService.Apply(ThemeService.Preferences with
                {
                    ThemeId = ((ThemeDefinition)preset.SelectedItem).Id,
                    Mode = (AppearanceMode)mode.SelectedIndex,
                    Material = (ThemeMaterial)material.SelectedIndex,
                    AccentSource = (AccentSource)accent.SelectedIndex,
                    CustomAccent = custom.Text
                });
                error.IsOpen = false;
            }
            catch (Exception exception)
            {
                error.Message = $"无法应用外观：{exception.Message}";
                error.IsOpen = true;
                Restore();
            }
        }

        void AddRow(string title, string description, FrameworkElement control)
        {
            var row = new Grid { ColumnSpacing = 20 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var labels = new StackPanel { Spacing = 4 };
            labels.Children.Add(new TextBlock { Text = title, FontSize = 16 });
            labels.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap,
                Foreground = FluentTheme.Brush(this, ThemeToken.TextSecondary) });
            row.Children.Add(labels);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            content.Children.Add(row);
        }

        AddRow("配色方案", "统一窗口、控件和内容区域的配色", preset);
        AddRow("应用外观", "明暗模式同时应用于主窗口和所属控件", mode);
        AddRow("背景材质", "纯色提供稳定底色，玻璃保留背景氛围", material);
        AddRow("强调色", "按钮、选中状态和焦点使用同一套配色", accent);
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        customRow.Children.Add(custom);
        customRow.Children.Add(applyColor);
        AddRow("自定义颜色", "输入不透明颜色；系统会调整明度以保持可读性", customRow);
        content.Children.Add(error);
        Restore();
        preset.SelectionChanged += (_, _) => Apply();
        mode.SelectionChanged += (_, _) => Apply();
        material.SelectionChanged += (_, _) => Apply();
        accent.SelectionChanged += (_, _) =>
        {
            custom.IsEnabled = accent.SelectedIndex == (int)AccentSource.Custom;
            applyColor.IsEnabled = custom.IsEnabled;
            Apply();
        };
        applyColor.Click += (_, _) => Apply();
        return new Border
        {
            Padding = new Thickness(26, 20, 24, 20),
            Background = FluentTheme.CardBrush(this),
            BorderBrush = FluentTheme.Brush(this, ThemeToken.Divider),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = content
        };
    }
}
