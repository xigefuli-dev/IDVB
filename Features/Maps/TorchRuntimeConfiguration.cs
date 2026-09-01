using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

internal static class TorchRuntimeConfiguration
{
    private const string RuntimePathVariable = "IDVB_TORCH_RUNTIME_PATH";

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? path);

    internal static void Configure()
    {
        var path = Environment.GetEnvironmentVariable(RuntimePathVariable);
        if (string.IsNullOrWhiteSpace(path))
            return;

        path = Path.GetFullPath(path);
        if (!Directory.Exists(path) || !SetDllDirectory(path))
            throw new InvalidOperationException(
                $"{RuntimePathVariable} 必须指向包含 LibTorch 原生 DLL 的目录。");
    }
}
