using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

public sealed partial class MapGlobalInputService
{
    private void DispatchMouseWheel(MouseWheelInputEventArgs input)
    {
        try
        {
            _ = _dispatcher.TryEnqueue(() => MouseWheelScrolled?.Invoke(this, input));
        }
        catch
        {
            // 输入钩子不能因 UI 线程关闭或队列拒绝而抛出到 Win32 回调。
        }
    }

    private void DispatchInput(
        MapInputInvokedEventArgs invoked,
        string device,
        string binding,
        string actionName,
        Action handler)
    {
        LogInputDispatch(MapLogLevel.Info, "input-matched", invoked,
            device, binding, actionName);

        try
        {
            if (_dispatcher.TryEnqueue(() => handler()))
            {
                LogInputDispatch(MapLogLevel.Info, "dispatch-accepted", invoked,
                    device, binding, actionName);
                return;
            }

            LogInputDispatch(MapLogLevel.Error, "dispatch-rejected", invoked,
                device, binding, actionName);
        }
        catch (Exception exception)
        {
            LogInputDispatch(MapLogLevel.Error, "dispatch-rejected", invoked,
                device, binding, actionName, exception);
        }
    }

    private static void LogInputDispatch(
        MapLogLevel level,
        string outcome,
        MapInputInvokedEventArgs invoked,
        string device,
        string binding,
        string actionName,
        Exception? exception = null)
    {
        try
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.System,
                level,
                $"Global input: {actionName} · {outcome}",
                details: new()
                {
                    ["outcome"] = outcome,
                    ["action"] = actionName,
                    ["device"] = device,
                    ["binding"] = binding,
                    ["inputTimestamp"] = invoked.Timestamp,
                    ["exceptionType"] = exception?.GetType().FullName,
                    ["exception"] = exception?.ToString()
                });
        }
        catch
        {
        }
    }
}
/*
 * 文件职责：MapGlobalInputService.Diagnostics。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
