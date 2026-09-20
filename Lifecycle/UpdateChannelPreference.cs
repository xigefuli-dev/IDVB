using IDVBuff.UpdateCore;

namespace IDVBuff.Lifecycle;

internal static class UpdateChannelPreference
{
    // User-level channel override stored under AppData; distinct from the
    // "update-channel.txt" marker that UpdateChannelPolicy ships in the
    // install directory.
    private const string FileName = "update-channel.txt";

    public static bool IsPreviewEnabled =>
        string.Equals(TryRead(), UpdateProtocol.TestChannel, StringComparison.Ordinal);

    public static string? TryRead()
    {
        try
        {
            var path = Path.Combine(AppDataPaths.RootDirectory, FileName);
            if (!File.Exists(path))
                return null;
            var channel = File.ReadAllText(path).Trim();
            return UpdateProtocol.IsKnownChannel(channel) ? channel : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SetPreviewEnabled(bool enabled)
    {
        Directory.CreateDirectory(AppDataPaths.RootDirectory);
        var path = Path.Combine(AppDataPaths.RootDirectory, FileName);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            enabled ? UpdateProtocol.TestChannel : UpdateProtocol.StableChannel);
        File.Move(temporaryPath, path, true);
    }
}
