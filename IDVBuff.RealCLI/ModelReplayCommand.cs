using Microsoft.UI.Dispatching;

namespace IDVBuff.RealCLI;

internal static class ModelReplayCommand
{
    public static Task<int> RunAsync(
        string[] args,
        DispatcherQueue dispatcher)
    {
        var forwarded = new List<string>(args.Length + 2);
        forwarded.AddRange(args);
        return MapOpenReplayCommand.RunAsync(
            forwarded.ToArray(),
            dispatcher,
            readOnlyModelReplay: true);
    }
}
