namespace IDVBuff.PluginContracts;

/// <summary>
/// 默认的进程切换等待时间。调用截图接口后，用户应在这段时间内切换到被宿主监测的进程。
/// </summary>
public static class PluginScreenshotDefaults
{
    public static readonly TimeSpan SwitchDelay = TimeSpan.FromSeconds(3);
}

/// <summary>
/// 为插件提供一次性的被监测进程截图。
///
/// 调用开始后宿主不会替插件激活或切换窗口；等待时间结束时，只有被监测进程仍为前台窗口，
/// 截图才会成功返回。
/// </summary>
public interface IPluginScreenshotService
{
    Task<PluginScreenshotResult> CaptureAsync(
        TimeSpan? switchDelay = null,
        CancellationToken cancellationToken = default);
}

/// <summary>截图调用的结果。失败时 <see cref="Screenshot"/> 为空。</summary>
public sealed class PluginScreenshotResult
{
    private PluginScreenshotResult(
        PluginScreenshot? screenshot,
        string? failureReason,
        TimeSpan elapsed)
    {
        Screenshot = screenshot;
        FailureReason = failureReason;
        Elapsed = elapsed;
    }

    public bool Succeeded => Screenshot is not null;

    public PluginScreenshot? Screenshot { get; }

    public string? FailureReason { get; }

    /// <summary>从调用开始到截图完成或失败的耗时。</summary>
    public TimeSpan Elapsed { get; }

    public static PluginScreenshotResult Success(
        PluginScreenshot screenshot,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        return new PluginScreenshotResult(screenshot, null, elapsed);
    }

    public static PluginScreenshotResult Failure(
        string failureReason,
        TimeSpan elapsed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        return new PluginScreenshotResult(null, failureReason, elapsed);
    }
}

/// <summary>
/// 插件可直接消费的 PNG 图像及其基本信息。图像字节由截图结果独占，插件可以自行保存或解码。
/// </summary>
public sealed class PluginScreenshot
{
    public PluginScreenshot(
        byte[] imageBytes,
        int width,
        int height,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
            throw new ArgumentException("截图图像不能为空。", nameof(imageBytes));
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        ImageBytes = imageBytes;
        Width = width;
        Height = height;
        CapturedAt = capturedAt;
    }

    /// <summary>编码格式。当前版本固定为 image/png。</summary>
    public string ContentType => "image/png";

    public byte[] ImageBytes { get; }

    public int Width { get; }

    public int Height { get; }

    public DateTimeOffset CapturedAt { get; }
}
