using System.Text.Json;

namespace IDVBuff.Lifecycle;

public sealed class MainProgramPreferences
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(AppDataPaths.RootDirectory, "main-program.json");

    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool UseLegacyTheme { get; set; }

    public static MainProgramPreferences Load()
    {
        lock (SyncRoot)
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<MainProgramPreferences>(File.ReadAllText(FilePath)) ?? new();
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
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporaryPath, FilePath, true);
        }
    }
}
