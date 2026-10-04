using System.Text.Json;
using System.Text.Json.Nodes;
using IDVBuff.Appearance;
using IDVBuff.Lifecycle;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AppearanceThemeTests
{
    [Theory]
    [InlineData(AppearanceMode.Light)]
    [InlineData(AppearanceMode.Dark)]
    public void VariantCardsRetainAllTwelveFillsAfterSelectionAndAccentChanges(AppearanceMode mode)
    {
        var preferences = new AppearancePreferences { Mode = mode, AccentSource = AccentSource.Custom, CustomAccent = "#FFFF00" };
        var system = new SystemAppearance(false, RgbColor.Parse("#245DD8"));
        var theme = ThemeResolver.Resolve(preferences, system);
        var changed = ThemeResolver.Resolve(preferences with { CustomAccent = "#00FFFF" }, system);
        var fills = new HashSet<RgbColor>();
        for (var slot = 0; slot < 12; slot++)
        {
            var group = MapCardPalette.Resolve(theme, slot, false);
            Assert.NotEqual(theme[ThemeToken.Card], group.Fill);
            Assert.True(fills.Add(group.Fill));
            Assert.True(RgbColor.Contrast(group.Text, group.Fill) >= 4.5);
            Assert.True(RgbColor.Contrast(group.SecondaryText, group.Fill) >= 4.5);
            Assert.Equal(theme[ThemeToken.Selection], MapCardPalette.Resolve(theme, slot, true).Fill);
            Assert.Equal(group, MapCardPalette.Resolve(changed, slot, false));
        }
    }

    [Fact]
    public void SemanticButtonsKeepReadableStatesAndRespectHighContrast()
    {
        foreach (var mode in new[] { AppearanceMode.Light, AppearanceMode.Dark })
        foreach (var seed in new[] { "#000000", "#FFFFFF", "#FFFF00", "#00FFFF" })
        foreach (var role in Enum.GetValues<ThemeButtonRole>())
        {
            var theme = ThemeResolver.Resolve(new() { Mode = mode, AccentSource = AccentSource.Custom, CustomAccent = seed },
                new(false, RgbColor.Parse("#245DD8")));
            var colors = ButtonPalette.Resolve(theme, role);
            foreach (var state in new[] { colors.Normal, colors.Hover, colors.Pressed })
                Assert.True(RgbColor.Contrast(state.Text, state.Fill) >= 4.5);
            Assert.Equal(theme[ThemeToken.ControlDisabled], colors.Disabled.Fill);
        }
        var hc = new ContrastColors(new(0, 0, 0), new(255, 255, 255), new(255, 255, 0), new(0, 0, 0), new(128, 128, 128), new(0, 255, 255));
        var contrast = ThemeResolver.Resolve(new(), new(false, new(0, 0, 255), HighContrast: hc));
        foreach (var role in Enum.GetValues<ThemeButtonRole>().Where(role => role != ThemeButtonRole.Standard))
        {
            var colors = ButtonPalette.Resolve(contrast, role);
            Assert.Equal(hc.Highlight, colors.Normal.Fill);
            Assert.Equal(hc.HighlightText, colors.Hover.Text);
            Assert.Equal(hc.Disabled, colors.Disabled.Text);
        }
        Assert.Equal(hc.Background, MapCardPalette.Resolve(contrast, 2, false).Fill);
        Assert.Equal(hc.Highlight, MapCardPalette.Resolve(contrast, 2, true).Fill);
    }

    [Fact]
    public void OrdinarySettingsSaveCannotRestoreStaleAppearanceOrRemoveFutureFields()
    {
        var oldSettings = new MainProgramPreferences { Appearance = new() { Mode = AppearanceMode.Light } };
        const string existing = """{"FutureSetting":{"enabled":true}}""";
        var currentAppearance = new AppearancePreferences { Mode = AppearanceMode.Dark, CustomAccent = "#008050", AccentSource = AccentSource.Custom };
        var latest = MainProgramPreferences.MergeAppearanceJson(existing, currentAppearance);
        oldSettings.MinimizeToTray = true;
        var saved = MainProgramPreferences.MergeGeneralPreferencesJson(latest, oldSettings);
        var loaded = JsonSerializer.Deserialize<MainProgramPreferences>(saved)!;
        Assert.Equal(currentAppearance, loaded.GetAppearance());
        Assert.True(loaded.MinimizeToTray);
        Assert.True(JsonNode.Parse(saved)!["FutureSetting"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void ScanModeAccentDefaultsOnAndCanReturnToSavedAccentSource()
    {
        var saved = JsonSerializer.Deserialize<AppearancePreferences>("{}")!;
        Assert.True(saved.AccentFollowsScanMode);
        var system = new SystemAppearance(false, RgbColor.Parse("#245DD8"));
        var green = RgbColor.Parse("#32DA89");
        var purple = RgbColor.Parse("#B65BF2");
        var followedGreen = ThemeResolver.Resolve(saved, system, scanModeAccent: green);
        var followedPurple = ThemeResolver.Resolve(saved, system, scanModeAccent: purple);
        Assert.NotEqual(followedGreen[ThemeToken.Accent], followedPurple[ThemeToken.Accent]);
        var disabled = saved with { AccentFollowsScanMode = false };
        Assert.Equal(ThemeResolver.Resolve(disabled, system)[ThemeToken.Accent],
            ThemeResolver.Resolve(disabled, system, scanModeAccent: purple)[ThemeToken.Accent]);
        Assert.False(JsonSerializer.Deserialize<AppearancePreferences>(
            JsonSerializer.Serialize(disabled))!.AccentFollowsScanMode);
    }

    [Theory]
    [InlineData(AppearanceMode.Light)]
    [InlineData(AppearanceMode.Dark)]
    public void ScanModeTransitionKeepsIntermediatePalettesReadable(AppearanceMode mode)
    {
        var preferences = new AppearancePreferences { Mode = mode };
        var system = new SystemAppearance(false, RgbColor.Parse("#245DD8"));
        var seeds = new[]
        {
            RgbColor.Parse("#32DA89"),
            RgbColor.Parse("#3097FF"),
            RgbColor.Parse("#B65BF2")
        };
        foreach (var from in seeds)
        foreach (var to in seeds)
        for (var frame = 0; frame <= 255; frame++)
        {
            var theme = ThemeResolver.Resolve(preferences, system,
                scanModeAccent: RgbColor.Mix(from, to, frame / 255d));
            Assert.True(RgbColor.Contrast(theme[ThemeToken.OnAccent], theme[ThemeToken.Accent]) >= 4.5);
            Assert.True(RgbColor.Contrast(theme[ThemeToken.SelectionText], theme[ThemeToken.Selection]) >= 4.5);
        }
    }

    [Fact]
    public void RegisteringPresetNeedsOnlyDataAndRejectsIncompletePalettes()
    {
        var original = ThemeRegistry.BuiltIn.Get(AppearancePreferences.DefaultThemeId);
        var alternate = new ThemeDefinition("alternate", "Alternate", original.Light, original.Dark,
            RgbColor.Parse("#008050"), RgbColor.Parse("#65DEA0"), [ThemeMaterial.Solid]);
        var registry = new ThemeRegistry([original, alternate]);
        var preferences = new AppearancePreferences { ThemeId = alternate.Id, AccentSource = AccentSource.ThemeDefault,
            Material = ThemeMaterial.Frosted, Mode = AppearanceMode.Light };
        var resolved = ThemeResolver.Resolve(preferences, new(false, RgbColor.Parse("#000000")), registry: registry);
        Assert.Equal(ThemeMaterial.Solid, resolved.EffectiveMaterial);
        Assert.Equal("UnsupportedMaterial", resolved.FallbackReason);
        Assert.NotEqual(ThemeResolver.Resolve(preferences with { ThemeId = original.Id },
            new(false, RgbColor.Parse("#000000")))[ThemeToken.Accent], resolved[ThemeToken.Accent]);
        Assert.Throws<ArgumentException>(() => ThemeRegistry.BuiltIn.Get(alternate.Id));
        var broken = original.Light.ToDictionary();
        broken.Remove(ThemeToken.Card);
        Assert.Throws<ArgumentException>(() => new ThemeDefinition("broken", "Broken", broken, original.Dark,
            original.LightAccent, original.DarkAccent, [ThemeMaterial.Solid]));
    }

    [Theory]
    [InlineData(false, false, false, AppearanceMode.Light, ThemeMaterial.Frosted)]
    [InlineData(false, true, true, AppearanceMode.Dark, ThemeMaterial.Solid)]
    [InlineData(true, true, false, AppearanceMode.System, ThemeMaterial.Frosted)]
    [InlineData(true, false, true, AppearanceMode.System, ThemeMaterial.Solid)]
    public void LegacyPreferencesRetainUserIntent(bool follow, bool dark, bool legacy, AppearanceMode mode, ThemeMaterial material)
    {
        var preferences = JsonSerializer.Deserialize<MainProgramPreferences>(
            $$"""{"FollowSystemTheme":{{follow.ToString().ToLowerInvariant()}},"UseDarkTheme":{{dark.ToString().ToLowerInvariant()}},"UseLegacyTheme":{{legacy.ToString().ToLowerInvariant()}}}""")!;
        var appearance = preferences.GetAppearance();
        Assert.Equal(mode, appearance.Mode);
        Assert.Equal(material, appearance.Material);
        Assert.Equal(AccentSource.System, appearance.AccentSource);
    }

    [Fact]
    public void AppearanceMergePreservesUnrelatedAndFutureSettings()
    {
        const string old = """{"SafeMode":false,"FutureSetting":{"values":[1,2,3]},"UseDarkTheme":true}""";
        var appearance = new AppearancePreferences { Mode = AppearanceMode.Light, AccentSource = AccentSource.Custom, CustomAccent = "#FFFF00" };
        var merged = MainProgramPreferences.MergeAppearanceJson(old, appearance);
        Assert.Equal(JsonNode.Parse(old)!["FutureSetting"]!.ToJsonString(), JsonNode.Parse(merged)!["FutureSetting"]!.ToJsonString());
        var preferences = JsonSerializer.Deserialize<MainProgramPreferences>(merged)!;
        Assert.False(preferences.SafeMode);
        Assert.Equal(appearance, preferences.GetAppearance());
        Assert.Equal(merged, MainProgramPreferences.MergeAppearanceJson(merged, appearance));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryMaterialAndExtremeAccentProducesCompleteReadablePalette(bool dark)
    {
        foreach (var material in Enum.GetValues<ThemeMaterial>())
        foreach (var seed in new[] { "#FFFFFF", "#000000", "#FFFF00", "#00FFFF", "#080030", "#808080", "#245DD8", "#32DA89", "#B65BF2" })
        {
            var preferences = new AppearancePreferences { Mode = dark ? AppearanceMode.Dark : AppearanceMode.Light,
                Material = material, AccentSource = AccentSource.Custom, CustomAccent = seed };
            var theme = ThemeResolver.Resolve(preferences, new(!dark, RgbColor.Parse("#000000")));
            Assert.Equal(dark, theme.IsDark);
            Assert.Equal(Enum.GetValues<ThemeToken>().Length, theme.Colors.Count);
            ThemeContrastValidator.Validate(theme);
            Assert.True(RgbColor.Contrast(theme[ThemeToken.OnAccent], theme[ThemeToken.Accent]) >= 4.5);
            Assert.True(RgbColor.Contrast(theme[ThemeToken.SelectionText], theme[ThemeToken.SelectionHover]) >= 4.5);
        }
    }

    [Fact]
    public void SystemModeDoesNotRetainPriorForcedModeAndProfilesStayIndependent()
    {
        var system = new SystemAppearance(false, RgbColor.Parse("#245DD8"));
        var preferences = new AppearancePreferences { Mode = AppearanceMode.Dark };
        var previous = ThemeResolver.Resolve(preferences, system);
        preferences = preferences with { Mode = AppearanceMode.System };
        Assert.False(ThemeResolver.Resolve(preferences, system).IsDark);
        Assert.True(ThemeResolver.Resolve(preferences, system with { IsDark = true }).IsDark);
        Assert.True(ThemeResolver.Resolve(preferences, system, ThemeProfile.EditorDark).IsDark);
        Assert.True(previous.IsDark);
        Assert.False(ThemeResolver.Resolve(preferences, system).IsDark);
    }

    [Fact]
    public void DisabledTransparencyChangesEffectiveMaterialWithoutChangingPreference()
    {
        var preferences = new AppearancePreferences { Material = ThemeMaterial.Frosted };
        var system = new SystemAppearance(false, RgbColor.Parse("#245DD8"), false);
        var theme = ThemeResolver.Resolve(preferences, system);
        Assert.Equal(ThemeMaterial.Frosted, theme.RequestedMaterial);
        Assert.Equal(ThemeMaterial.Solid, theme.EffectiveMaterial);
        Assert.Equal("TransparencyDisabled", theme.FallbackReason);
        Assert.Equal(ThemeMaterial.Frosted, ThemeResolver.Resolve(preferences, system with { TransparencyEnabled = true }).EffectiveMaterial);
    }

    [Fact]
    public void HighContrastOverridesCustomColorsAndWorkspaces()
    {
        var colors = new ContrastColors(RgbColor.Parse("#000000"), RgbColor.Parse("#FFFFFF"),
            RgbColor.Parse("#FFFF00"), RgbColor.Parse("#000000"), RgbColor.Parse("#CCCCCC"), RgbColor.Parse("#00FFFF"));
        var preferences = new AppearancePreferences { AccentSource = AccentSource.Custom, CustomAccent = "#FF00FF", Material = ThemeMaterial.Frosted };
        foreach (var profile in Enum.GetValues<ThemeProfile>())
        {
            var theme = ThemeResolver.Resolve(preferences, new(false, RgbColor.Parse("#245DD8"), HighContrast: colors), profile);
            Assert.Equal(colors.Background, theme[ThemeToken.Card]);
            Assert.Equal(colors.Foreground, theme[ThemeToken.Text]);
            Assert.Equal(colors.Highlight, theme[ThemeToken.Selection]);
            Assert.Equal(colors.HighlightText, theme[ThemeToken.SelectionText]);
            Assert.Equal(ThemeMaterial.Solid, theme.EffectiveMaterial);
        }
    }

    [Fact]
    public void AccentDoesNotRecolorSemanticStatusOrMutatePreviousSnapshot()
    {
        var preferences = new AppearancePreferences { AccentSource = AccentSource.Custom, CustomAccent = "#FF0000" };
        var system = new SystemAppearance(false, RgbColor.Parse("#245DD8"));
        var first = ThemeResolver.Resolve(preferences, system);
        var second = ThemeResolver.Resolve(preferences with { CustomAccent = "#00FFFF" }, system);
        Assert.NotEqual(first[ThemeToken.Accent], second[ThemeToken.Accent]);
        Assert.Equal(first[ThemeToken.WarningText], second[ThemeToken.WarningText]);
        Assert.Equal(first[ThemeToken.ErrorFill], second[ThemeToken.ErrorFill]);
        Assert.Equal(first[ThemeToken.Accent], ThemeResolver.Resolve(preferences, system)[ThemeToken.Accent]);
    }

    [Theory]
    [InlineData("#80FF0000")]
    [InlineData("red")]
    [InlineData("#GG0000")]
    [InlineData(null)]
    public void InvalidCustomAccentIsRejectedBeforeResolution(string? value)
    {
        Assert.Throws<ArgumentException>(() => ThemeResolver.Resolve(
            new() { AccentSource = AccentSource.Custom, CustomAccent = value }, new(false, RgbColor.Parse("#245DD8"))));
    }
}
