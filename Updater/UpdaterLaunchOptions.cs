using IDVBuff.UpdateCore;

namespace IDVBuff.Updater;

internal sealed record UpdaterLaunchOptions(
    string Channel,
    Uri UpdateRoot,
    int MainProcessId,
    bool Background)
{
    public static UpdaterLaunchOptions Parse(string[] args)
    {
        var channel = UpdateProtocol.DefaultChannel;
        var updateRoot = new Uri(UpdateProtocol.DefaultUpdateRoot, UriKind.Absolute);
        var mainProcessId = 0;
        var background = false;
        for (var index = 1; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--channel", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
                channel = args[++index];
            else if (string.Equals(args[index], "--update-root", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length
                && Uri.TryCreate(args[++index], UriKind.Absolute, out var parsedRoot))
                updateRoot = parsedRoot;
            else if (string.Equals(args[index], "--from-main-pid", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
                _ = int.TryParse(args[++index], out mainProcessId);
            else if (string.Equals(args[index], "--background", StringComparison.OrdinalIgnoreCase))
                background = true;
        }

        if (!UpdateProtocol.IsKnownChannel(channel))
            throw new ArgumentException($"不支持的更新通道：{channel}");
        return new UpdaterLaunchOptions(channel, updateRoot, mainProcessId, background);
    }
}
