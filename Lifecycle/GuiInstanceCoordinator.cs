using System.IO.Pipes;
using System.Text;

namespace IDVBuff.Lifecycle;

internal sealed class GuiInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\IdentityVisionBridge.Gui";
    private const string ActivationPipeName = "IdentityVisionBridge.GuiActivation.v1";
    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _mutex;
    private Task? _listener;

    public static event EventHandler? ActivationRequested;

    public bool TryAcquirePrimary()
    {
        _mutex = new Mutex(true, MutexName, out var ownsMutex);
        return ownsMutex;
    }

    public void StartListening()
    {
        _listener ??= Task.Run(() => ListenAsync(_shutdown.Token));
    }

    public void NotifyPrimaryInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                ActivationPipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine("activate");
        }
        catch
        {
            // A primary process that is already shutting down may close the
            // activation pipe before this short-lived secondary process connects.
        }
    }

    private static void RaiseActivationRequested() =>
        ActivationRequested?.Invoke(null, EventArgs.Empty);

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                ActivationPipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, true);
                if (string.Equals(
                        await reader.ReadLineAsync(cancellationToken),
                        "activate",
                        StringComparison.Ordinal))
                    RaiseActivationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _shutdown.Dispose();
        _mutex?.Dispose();
    }
}
