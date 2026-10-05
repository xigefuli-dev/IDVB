using System.Reflection;
using IDVBuff.PluginContracts;
using OpenCvSharp;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>一次按需抓取的结果：灰度帧 + 客户区屏幕坐标 + 窗口句柄。</summary>
internal sealed class GrabbedFrame(Mat gray, PluginClientBounds bounds, IntPtr windowHandle) : IDisposable
{
    public Mat Gray { get; } = gray;

    public PluginClientBounds Bounds { get; } = bounds;

    public IntPtr WindowHandle { get; } = windowHandle;

    public Size Size => new(Gray.Width, Gray.Height);

    public void Dispose() => Gray.Dispose();
}

/// <summary>
/// 按需抓取一帧前台游戏画面（不做常驻采集）。
///
/// 首选路径：宿主已经注册的 <c>IGameWindowCapture</c>（GDI 抓客户区，直接返回 OpenCV Mat，
/// 无需 PNG 编解码）。插件工程不引用主程序集，因此用反射解析该契约类型；解析不到时退回
/// 插件 SDK 的 <see cref="IPluginScreenshotService"/>（一次性 PNG 截图）。两条路径都要求
/// dwrg.exe 在前台窗口。
/// </summary>
internal sealed class GameFrameGrabber : ISceneQuickActionsFrameGrabber
{
    private const string CaptureContractTypeName = "IDVBuff.Core.Contracts.IGameWindowCapture";
    private static readonly TimeSpan ReflectionRetryInterval = TimeSpan.FromSeconds(5);

    private readonly IPluginContext _context;
    private readonly IPluginScreenshotService? _screenshot;
    private readonly IPluginGameWindowService? _gameWindow;
    private readonly Action<string> _log;

    private object? _captureService;
    private MethodInfo? _tryCaptureClient;
    private PropertyInfo? _imageProperty;
    private PropertyInfo? _clientBoundsProperty;
    private PropertyInfo? _windowHandleProperty;
    private DateTime _lastReflectionAttempt = DateTime.MinValue;
    private bool _reflectionFailureLogged;

    public GameFrameGrabber(
        IPluginContext context,
        IPluginScreenshotService? screenshot,
        IPluginGameWindowService? gameWindow,
        Action<string> log)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _screenshot = screenshot;
        _gameWindow = gameWindow;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>是否拿到了零编码的快速抓帧路径。</summary>
    public bool HasFastPath => _captureService is not null && _tryCaptureClient is not null;

    public bool TryGrab(out GrabbedFrame? frame, out string failure, CancellationToken cancellationToken)
    {
        frame = null;
        failure = string.Empty;

        EnsureFastPath();
        if (HasFastPath && TryGrabByCaptureContract(out frame, out failure))
            return true;

        if (TryGrabByScreenshot(out frame, out failure, cancellationToken))
            return true;

        if (string.IsNullOrWhiteSpace(failure))
            failure = "无法获取游戏画面。";
        return false;
    }

    private void EnsureFastPath()
    {
        if (HasFastPath)
            return;
        if (DateTime.UtcNow - _lastReflectionAttempt < ReflectionRetryInterval)
            return;
        _lastReflectionAttempt = DateTime.UtcNow;

        try
        {
            var contractType = ResolveType(CaptureContractTypeName);
            if (contractType is null)
                return;

            var service = _context.GetService(contractType);
            var captureMethod = contractType.GetMethod("TryCaptureClient");
            if (service is null || captureMethod is null)
                return;

            _imageProperty = null;
            _clientBoundsProperty = null;
            _windowHandleProperty = null;
            _tryCaptureClient = captureMethod;
            _captureService = service;
            _log("已启用零编码抓帧路径（宿主 IGameWindowCapture）。");
        }
        catch (Exception exception)
        {
            if (!_reflectionFailureLogged)
            {
                _reflectionFailureLogged = true;
                _log($"解析宿主抓帧契约失败，将退回 PNG 截图：{exception.Message}");
            }
        }
    }

    private static Type? ResolveType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
                if (type is not null)
                    return type;
            }
            catch
            {
                // 个别程序集反射失败时直接跳过。
            }
        }

        return null;
    }

    private bool TryGrabByCaptureContract(out GrabbedFrame? frame, out string failure)
    {
        frame = null;
        failure = string.Empty;

        object? rawFrame = null;
        Mat? gray = null;
        try
        {
            object?[] arguments = [null, null];
            var succeeded = _tryCaptureClient!.Invoke(_captureService, arguments) is true;
            rawFrame = arguments[0];
            if (!succeeded || rawFrame is null)
            {
                failure = arguments[1] as string ?? "宿主未返回游戏画面。";
                return false;
            }

            var frameType = rawFrame.GetType();
            _imageProperty ??= frameType.GetProperty("Image");
            _clientBoundsProperty ??= frameType.GetProperty("ClientBounds");
            _windowHandleProperty ??= frameType.GetProperty("WindowHandle");
            if (_imageProperty?.GetValue(rawFrame) is not Mat image || image.Empty())
            {
                failure = "宿主返回了空画面。";
                return false;
            }

            if (!TryReadBounds(rawFrame, out var bounds))
            {
                failure = "无法读取游戏客户区位置。";
                return false;
            }

            gray = ToGray(image);
            if (gray.Empty())
            {
                failure = "画面转换为灰度失败。";
                return false;
            }

            frame = new GrabbedFrame(gray, bounds, ReadWindowHandle(rawFrame));
            gray = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"抓取游戏画面失败：{Unwrap(exception).Message}";
            return false;
        }
        finally
        {
            gray?.Dispose();
            (rawFrame as IDisposable)?.Dispose();
        }
    }

    private bool TryGrabByScreenshot(out GrabbedFrame? frame, out string failure, CancellationToken cancellationToken)
    {
        frame = null;
        failure = string.Empty;

        if (_screenshot is null || _gameWindow is null)
        {
            failure = "宿主没有提供截图能力。";
            return false;
        }

        try
        {
            // switchDelay 传 0：插件按自己的检测间隔抓帧，不需要额外的切回游戏等待。
            var result = _screenshot
                .CaptureAsync(TimeSpan.Zero, cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (!result.Succeeded || result.Screenshot is null)
            {
                failure = result.FailureReason ?? "宿主截图失败。";
                return false;
            }

            if (!_gameWindow.TryGetForegroundClientBounds(out var bounds, out var handle, out var boundsFailure)
                || !bounds.IsValid)
            {
                failure = string.IsNullOrWhiteSpace(boundsFailure)
                    ? "无法读取游戏客户区位置。"
                    : boundsFailure;
                return false;
            }

            var gray = Cv2.ImDecode(result.Screenshot.ImageBytes, ImreadModes.Grayscale);
            if (gray.Empty())
            {
                gray.Dispose();
                failure = "截图解码失败。";
                return false;
            }

            frame = new GrabbedFrame(gray, bounds, handle);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = $"截图失败：{Unwrap(exception).Message}";
            return false;
        }
    }

    private bool TryReadBounds(object rawFrame, out PluginClientBounds bounds)
    {
        bounds = default;
        if (_clientBoundsProperty?.GetValue(rawFrame) is not { } boxed)
            return TryReadBoundsFromService(out bounds);

        var type = boxed.GetType();
        var x = type.GetProperty("X")?.GetValue(boxed);
        var y = type.GetProperty("Y")?.GetValue(boxed);
        var width = type.GetProperty("Width")?.GetValue(boxed);
        var height = type.GetProperty("Height")?.GetValue(boxed);
        if (x is null || y is null || width is null || height is null)
            return TryReadBoundsFromService(out bounds);

        bounds = new PluginClientBounds(
            Convert.ToInt32(x),
            Convert.ToInt32(y),
            Convert.ToInt32(width),
            Convert.ToInt32(height));
        return bounds.IsValid || TryReadBoundsFromService(out bounds);
    }

    private bool TryReadBoundsFromService(out PluginClientBounds bounds)
    {
        bounds = default;
        if (_gameWindow is null)
            return false;
        return _gameWindow.TryGetForegroundClientBounds(out bounds, out _, out _) && bounds.IsValid;
    }

    private IntPtr ReadWindowHandle(object rawFrame)
    {
        if (_windowHandleProperty?.GetValue(rawFrame) is IntPtr handle && handle != IntPtr.Zero)
            return handle;
        return _gameWindow is not null
            && _gameWindow.TryGetForegroundClientBounds(out _, out var serviceHandle, out _)
                ? serviceHandle
                : IntPtr.Zero;
    }

    private static Mat ToGray(Mat image)
    {
        var gray = new Mat();
        switch (image.Channels())
        {
            case 4:
                Cv2.CvtColor(image, gray, ColorConversionCodes.BGRA2GRAY);
                break;
            case 3:
                Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 1:
                image.CopyTo(gray);
                break;
            default:
                break;
        }

        return gray;
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
}
