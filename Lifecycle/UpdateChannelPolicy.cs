using IDVBuff.UpdateCore;

namespace IDVBuff.Lifecycle;

internal static class UpdateChannelPolicy
{
    private const string ChannelFileName = "update-channel.txt";

    public static string Resolve()
    {
#if IDVBUFF_TEST_BUILD
        return UpdateProtocol.TestChannel;
#else
        var preferredChannel = UpdateChannelPreference.TryRead();
        if (preferredChannel is not null)
            return preferredChannel;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, ChannelFileName);
            if (File.Exists(path))
            {
                var channel = File.ReadAllText(path).Trim();
                if (UpdateProtocol.IsKnownChannel(channel))
                    return channel;
            }
        }
        catch
        {
            // A missing or unreadable marker always fails closed to stable.
        }
        return UpdateProtocol.StableChannel;
#endif
    }
}
