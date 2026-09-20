using System.IO.Pipes;
using System.Text;

namespace IDVBuff.Lifecycle;

internal sealed class GuiInstanceCoordinator : IDisposable
{
    private const string StandardMutexName = "Local\\IdentityVisionBridge.Gui";
    private const string DevMutexName = "Local\\IdentityVisionBridge.Gui.Dev";
    private const string StandardActivationPipeName = "IdentityVisionBridge.GuiActivation.v1";
    private const string DevActivationPipeName = "IdentityVisionBridge.GuiActivation.Dev.v1";

    private readonly bool _isDevelopmentInstance;
    private readonly string _primaryMutexName;
    private readonly string _activationPipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _mutex;
    private Mutex? _companionMutex;
    private Task? _listener;

    public static event EventHandler? ActivationRequested;

    public GuiInstanceCoordinator(bool isDevelopmentInstance = false)
    {
        _isDevelopmentInstance = isDevelopmentInstance;
        _primaryMutexName = isDevelopmentInstance ? DevMutexName : StandardMutexName;
        _activationPipeName = isDevelopmentInstance ? DevActivationPipeName : StandardActivationPipeName;
    }

    public bool TryAcquirePrimary()
    {
        try
        {
            _mutex = new Mutex(true, _primaryMutexName, out var ownsPrimary);
            if (!ownsPrimary)
                return false;

            // 开发模式启动时，同时占位标准互斥体，防止用户同时拉起正式版造成双开；
            // 正式版启动时，同时占位开发互斥体，彻底保证全系统最多只有一个 GUI 进程。
            var companionName = _isDevelopmentInstance ? StandardMutexName : DevMutexName;
            try
            {
                _companionMutex = new Mutex(true, companionName, out var ownsCompanion);
                if (!ownsCompanion)
                {
                    _mutex.Dispose();
                    _mutex = null;
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // 前序进程异常退出已遗弃互斥体，当前线程成功接管
            }

            return true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartListening()
    {
        _listener ??= Task.Run(() => ListenAsync(_shutdown.Token));
    }

    public void NotifyPrimaryInstance()
    {
        // 尝试唤醒主实例（先尝试自身通道，若连不上再尝试伴生通道）
        if (TryNotifyPipe(_activationPipeName))
            return;

        var companionPipe = _isDevelopmentInstance ? StandardActivationPipeName : DevActivationPipeName;
        TryNotifyPipe(companionPipe);
    }

    private static bool TryNotifyPipe(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(500);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RaiseActivationRequested() =>
        ActivationRequested?.Invoke(null, EventArgs.Empty);

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _activationPipeName,
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
        _companionMutex?.Dispose();
        _mutex?.Dispose();
    }
}
