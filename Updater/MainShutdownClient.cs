using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using IDVBuff.UpdateCore;

namespace IDVBuff.Updater;

internal static class MainShutdownClient
{
    public static async Task RequestAndWaitAsync(
        int mainProcessId,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        if (mainProcessId <= 0)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(
            ".",
            UpdateProtocol.ShutdownPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), timeout.Token);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        var request = new UpdateShutdownRequest(
            UpdateProtocol.PipeSchemaVersion,
            "prepare_shutdown",
            targetVersion,
            Environment.ProcessId);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, UpdateProtocol.JsonOptions));
        var responseLine = await reader.ReadLineAsync(timeout.Token)
            ?? throw new IOException("主程序没有返回关闭确认。");
        var response = JsonSerializer.Deserialize<UpdateShutdownResponse>(
            responseLine,
            UpdateProtocol.JsonOptions)
            ?? throw new IOException("主程序返回了无效的关闭确认。");
        if (!response.Accepted)
            throw new InvalidOperationException(response.Error ?? "主程序拒绝了更新关闭请求。");
        if (response.MainProcessId != mainProcessId)
            throw new InvalidOperationException("主程序 PID 与更新请求不一致。");

        try
        {
            using var process = Process.GetProcessById(mainProcessId);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException)
        {
            // It exited between the response and process lookup.
        }
    }
}
