using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;
public sealed partial class IdvmPackageService
{

    private static void ValidateTags(IReadOnlyList<MetadataTagDto> tags, bool allowTags)
    {
        if (!allowTags && tags.Count != 0)
            throw new InvalidDataException("旧版 IDVM metadata 不能声明地图标签。");
        if (tags.Count > 256)
            throw new InvalidDataException("地图标签数量超过限制。");
        var groupIds = new HashSet<Guid>();
        foreach (var tag in tags)
        {
            if (tag is null || tag.GroupId == Guid.Empty || !groupIds.Add(tag.GroupId)
                || string.IsNullOrWhiteSpace(tag.GroupName) || tag.GroupName.Length > 128
                || tag.GroupName.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(tag.Value) || tag.Value.Length > 256
                || tag.Value.Any(char.IsControl)
                || tag.GroupName != tag.GroupName.Trim() || tag.Value != tag.Value.Trim())
            {
                throw new InvalidDataException("地图标签包含无效或重复的数据。");
            }
        }
    }

    private static void ValidatePoint(PointDto? point, string name)
    {
        if (point is null
            || !double.IsFinite(point.X)
            || !double.IsFinite(point.Y)
            || point.X is < 0d or > 1d
            || point.Y is < 0d or > 1d)
        {
            throw new InvalidDataException($"{name} 包含超出 0..1 的坐标。");
        }
    }

    private static RectangleDto NormalizeBounds(MapReferenceBounds? bounds, int width, int height)
    {
        if (bounds?.IsValid is not true || width <= 0 || height <= 0)
            return new RectangleDto { Width = 1d, Height = 1d };
        return new RectangleDto
        {
            X = bounds.X / width,
            Y = bounds.Y / height,
            Width = bounds.Width / width,
            Height = bounds.Height / height
        };
    }

    private static void ValidateRectangle(RectangleDto? rectangle, bool allowNull, string name)
    {
        if (rectangle is null)
        {
            if (allowNull)
                return;
            throw new InvalidDataException($"{name} 不能为空。");
        }
        if (!double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y)
            || !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height)
            || rectangle.X < -CoordinateTolerance || rectangle.Y < -CoordinateTolerance
            || rectangle.Width <= 0d || rectangle.Height <= 0d
            || rectangle.X + rectangle.Width > 1d + CoordinateTolerance
            || rectangle.Y + rectangle.Height > 1d + CoordinateTolerance)
        {
            throw new InvalidDataException($"{name} 包含超出 0..1 的坐标。");
        }
    }
}
