using System.Text.Json;
using System.Text.Json.Nodes;
using IDVBuff.Appearance;

namespace IDVBuff.Lifecycle;

public sealed class MainProgramPreferences
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(AppDataPaths.RootDirectory, "main-program.json");

    public bool StartWithWindows { get; set; }
    public bool SafeMode { get; set; } = true;
    public bool SafeModeFirstRunIntroductionCompleted { get; set; }
    public bool ModelImprovementConsentPromptCompleted { get; set; }
    public bool HelpImproveModels { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool UseLegacyTheme { get; set; }
    public bool FollowSystemTheme { get; set; } = true;
    public bool UseDarkTheme { get; set; }
    public AppearancePreferences? Appearance { get; set; }

    public AppearancePreferences GetAppearance() => Appearance
        ?? AppearancePreferences.FromLegacy(FollowSystemTheme, UseDarkTheme, UseLegacyTheme);
    public bool AllowUnsafePluginRandomDelayMinimums { get; set; }
    public bool AllowSurveyMode { get; set; }
    public bool DeveloperMode { get; set; }
    public bool RealtimePerformanceOverlayEnabled { get; set; }
    public bool EnhancedMiniMapEnabled { get; set; }
    public bool DisableScaleLocking { get; set; }

    public static MainProgramPreferences Load()
    {
        lock (SyncRoot)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var preferences = JsonSerializer.Deserialize<MainProgramPreferences>(File.ReadAllText(FilePath)) ?? new();
                    // Survey is temporarily unavailable in the public surface.
                    // Imported preference files cannot restore its entry point.
                    preferences.AllowSurveyMode = false;
                    return preferences;
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to load main program preferences: {exception.Message}");
            }

            return new MainProgramPreferences();
        }
    }

    public void Save()
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(AppDataPaths.RootDirectory);
            var temporaryPath = FilePath + ".tmp";
            var existing = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "{}";
            File.WriteAllText(temporaryPath, MergeGeneralPreferencesJson(existing, this));
            File.Move(temporaryPath, FilePath, true);
        }
    }

    internal static string MergeGeneralPreferencesJson(string json, MainProgramPreferences preferences)
    {
        var document = JsonNode.Parse(json)?.AsObject()
            ?? throw new JsonException("主程序偏好文件不是有效对象。");
        // Preserve fields from newer versions and the appearance service's latest write.
        foreach (var (key, value) in JsonSerializer.SerializeToNode(preferences, JsonOptions)!.AsObject())
        {
            if (key == nameof(Appearance) && document[key] is not null) continue;
            document[key] = value?.DeepClone();
        }
        return document.ToJsonString(JsonOptions);
    }

    public static void SaveAppearance(AppearancePreferences appearance)
    {
        appearance.Validate();
        lock (SyncRoot)
        {
            var json = File.Exists(FilePath) ? File.ReadAllText(FilePath)
                : JsonSerializer.Serialize(new MainProgramPreferences(), JsonOptions);
            Directory.CreateDirectory(AppDataPaths.RootDirectory);
            File.WriteAllText(FilePath + ".tmp", MergeAppearanceJson(json, appearance));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
    }

    internal static string MergeAppearanceJson(string json, AppearancePreferences appearance)
    {
        appearance.Validate();
        var document = JsonNode.Parse(json)?.AsObject()
            ?? throw new JsonException("主程序偏好文件不是有效对象。");
        document["Appearance"] = JsonSerializer.SerializeToNode(appearance, JsonOptions);
        return document.ToJsonString(JsonOptions);
    }
}
