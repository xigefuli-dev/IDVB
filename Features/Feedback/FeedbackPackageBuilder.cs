using System.IO.Compression;

namespace IDVBuff.Features.Feedback;

/// <summary>
/// 反馈数据打包结果。
/// </summary>
public sealed class FeedbackPackageBuildResult
{
    public bool Success { get; set; }

    public string? LogsZipPath { get; set; }

    public long LogsZipSizeBytes { get; set; }

    public int LogsFileCount { get; set; }

    public string? DiagnosticsZipPath { get; set; }

    public long DiagnosticsZipSizeBytes { get; set; }

    public int DiagnosticsFileCount { get; set; }

    public string? OutputDirectory { get; set; }

    public List<string> Warnings { get; } = [];

    public Exception? Error { get; set; }
}

/// <summary>
/// 负责在后台安全、容错地将日志和诊断数据按相对路径打包为独立的 ZIP 文件。
/// </summary>
public static class FeedbackPackageBuilder
{
    /// <summary>
    /// 获取默认日志源目录。
    /// </summary>
    public static string DefaultLogDirectory =>
        Path.Combine(AppDataPaths.RootDirectory, "Logs");

    /// <summary>
    /// 获取默认诊断数据源目录。
    /// </summary>
    public static string DefaultDiagnosticsDirectory =>
        Path.Combine(AppDataPaths.RootDirectory, "诊断模式");

    /// <summary>
    /// 执行反馈数据打包。
    /// 无论用户是否开启对应功能、无论是否有数据、无论中间是否产生读写冲突，均不会抛出未捕获异常，绝不卡死主程序。
    /// </summary>
    public static async Task<FeedbackPackageBuildResult> BuildPackagesAsync(
        bool includeLogs,
        bool includeDiagnostics,
        string? customLogDirectory = null,
        string? customDiagnosticsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var result = new FeedbackPackageBuildResult();

        await Task.Run(() =>
        {
            try
            {
                var tempOutputDir = Path.Combine(
                    Path.GetTempPath(),
                    "IDVB_Feedback",
                    $"{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");

                Directory.CreateDirectory(tempOutputDir);
                result.OutputDirectory = tempOutputDir;

                if (includeLogs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var logSource = customLogDirectory ?? DefaultLogDirectory;
                    var targetLogZip = Path.Combine(tempOutputDir, "logs.zip");
                    var (count, size, warnings) = ArchiveDirectorySafe(logSource, targetLogZip, cancellationToken);
                    result.LogsZipPath = targetLogZip;
                    result.LogsFileCount = count;
                    result.LogsZipSizeBytes = size;
                    result.Warnings.AddRange(warnings);
                }

                if (includeDiagnostics)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var diagSource = customDiagnosticsDirectory ?? DefaultDiagnosticsDirectory;
                    var targetDiagZip = Path.Combine(tempOutputDir, "diagnostics.zip");
                    var (count, size, warnings) = ArchiveDirectorySafe(diagSource, targetDiagZip, cancellationToken);
                    result.DiagnosticsZipPath = targetDiagZip;
                    result.DiagnosticsFileCount = count;
                    result.DiagnosticsZipSizeBytes = size;
                    result.Warnings.AddRange(warnings);
                }

                result.Success = true;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Warnings.Add("打包过程被用户取消");
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.Error = exception;
                result.Warnings.Add($"打包发生未预期异常: {exception.Message}");
            }
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// 安全地将源目录中的文件按原有相对路径压缩到目标 ZIP 文件中。
    /// 若源目录不存在或无文件，则生成合法空 ZIP 或优雅返回，不抛异常。
    /// </summary>
    public static (int FileCount, long SizeBytes, List<string> Warnings) ArchiveDirectorySafe(
        string sourceDirectory,
        string targetZipPath,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        int fileCount = 0;

        try
        {
            var zipDir = Path.GetDirectoryName(targetZipPath);
            if (!string.IsNullOrEmpty(zipDir))
                Directory.CreateDirectory(zipDir);

            using var zipStream = new FileStream(targetZipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);

            if (!Directory.Exists(sourceDirectory))
            {
                // 源目录不存在，生成空的合法 zip 即可
                return (0, zipStream.Length, warnings);
            }

            var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories);

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // 计算保留的相对路径，并标准化为正斜杠 '/'
                    var relativePath = Path.GetRelativePath(sourceDirectory, filePath)
                        .Replace('\\', '/');

                    var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);

                    // 允许共享读写与删除，防止读取正在写入的日志时发生 IOException 锁死
                    using var sourceStream = new FileStream(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    using var entryStream = entry.Open();
                    sourceStream.CopyTo(entryStream);
                    fileCount++;
                }
                catch (Exception fileException)
                {
                    // 单个文件读取失败只记录警告，跳过并继续打包其它文件，保证主流程不炸
                    warnings.Add($"跳过文件 {Path.GetFileName(filePath)}: {fileException.Message}");
                }
            }

            // 归档关闭前刷新
            archive.Dispose();
            long finalLength = File.Exists(targetZipPath) ? new FileInfo(targetZipPath).Length : 0;
            return (fileCount, finalLength, warnings);
        }
        catch (Exception exception)
        {
            warnings.Add($"归档目录 {sourceDirectory} 时出错: {exception.Message}");
            long len = File.Exists(targetZipPath) ? new FileInfo(targetZipPath).Length : 0;
            return (fileCount, len, warnings);
        }
    }

    /// <summary>
    /// 安全清理打包临时生成的目录。
    /// </summary>
    public static void CleanupTempDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            return;

        try
        {
            if (Directory.Exists(directoryPath))
                Directory.Delete(directoryPath, recursive: true);
        }
        catch
        {
            // 清理临时文件失败静默忽略
        }
    }
}
