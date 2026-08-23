using System.Text.Json;

namespace IDVBuff.Views;

internal sealed class ShellLayoutMemory
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        AppDataPaths.RootDirectory,
        "ui-memory.json");

    public bool NavigationCompact { get; set; }
    public bool SurveyProjectsCollapsed { get; set; } = true;

    public static ShellLayoutMemory Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new ShellLayoutMemory();

                return JsonSerializer.Deserialize<ShellLayoutMemory>(
                    File.ReadAllText(FilePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new ShellLayoutMemory();
            }
            catch
            {
                return new ShellLayoutMemory();
            }
        }
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temporaryPath = $"{FilePath}.tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save UI memory: {exception.Message}");
            }
        }
    }
}
