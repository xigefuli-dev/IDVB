namespace IDVBuff.Features.Maps;

public sealed class MapWindowSignature : IEquatable<MapWindowSignature>
{
    public long WindowHandle { get; init; }
    public int ClientX { get; init; }
    public int ClientY { get; init; }
    public int ClientWidth { get; init; }
    public int ClientHeight { get; init; }
    public int ViewportX { get; init; }
    public int ViewportY { get; init; }
    public int ViewportWidth { get; init; }
    public int ViewportHeight { get; init; }
    public uint Dpi { get; init; } = 96;

    public bool Equals(MapWindowSignature? other) => other is not null
        && WindowHandle == other.WindowHandle
        && ClientX == other.ClientX
        && ClientY == other.ClientY
        && ClientWidth == other.ClientWidth
        && ClientHeight == other.ClientHeight
        && ViewportX == other.ViewportX
        && ViewportY == other.ViewportY
        && ViewportWidth == other.ViewportWidth
        && ViewportHeight == other.ViewportHeight
        && Dpi == other.Dpi;

    public override bool Equals(object? obj) => Equals(obj as MapWindowSignature);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(WindowHandle);
        hash.Add(ClientX);
        hash.Add(ClientY);
        hash.Add(ClientWidth);
        hash.Add(ClientHeight);
        hash.Add(ViewportX);
        hash.Add(ViewportY);
        hash.Add(ViewportWidth);
        hash.Add(ViewportHeight);
        hash.Add(Dpi);
        return hash.ToHashCode();
    }
}
/*
 * 文件职责：MapSessionModels.WindowSignature。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
