namespace IDVBuff.Appearance;

public enum AppearanceMode { System, Light, Dark }
public enum ThemeMaterial { Solid, Frosted }
public enum AccentSource { System, ThemeDefault, Custom }
public enum ThemeProfile { Application, EditorDark, OverlayDark }

public sealed record AppearancePreferences
{
    public const string DefaultThemeId = "idvb-neutral";
    public int SchemaVersion { get; init; } = 1;
    public string ThemeId { get; init; } = DefaultThemeId;
    public AppearanceMode Mode { get; init; } = AppearanceMode.System;
    public ThemeMaterial Material { get; init; } = ThemeMaterial.Solid;
    public AccentSource AccentSource { get; init; } = AccentSource.System;
    public string? CustomAccent { get; init; }
    public bool AccentFollowsScanMode { get; init; } = true;

    public static AppearancePreferences FromLegacy(bool followSystem, bool dark, bool legacy) => new()
    {
        Mode = followSystem ? AppearanceMode.System : dark ? AppearanceMode.Dark : AppearanceMode.Light,
        Material = legacy ? ThemeMaterial.Solid : ThemeMaterial.Frosted
    };

    public void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(ThemeId))
            throw new ArgumentException("不支持的主题配置版本或配色方案。");
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(Material) || !Enum.IsDefined(AccentSource))
            throw new ArgumentException("主题配置包含无效选项。");
        if (AccentSource == AccentSource.Custom && !RgbColor.TryParse(CustomAccent, out _))
            throw new ArgumentException("强调色必须是 #RRGGBB 格式的不透明颜色。");
    }
}

public sealed record ContrastColors(RgbColor Background, RgbColor Foreground,
    RgbColor Highlight, RgbColor HighlightText, RgbColor Disabled, RgbColor Link);

public sealed record SystemAppearance(bool IsDark, RgbColor Accent,
    bool TransparencyEnabled = true, ContrastColors? HighContrast = null);
