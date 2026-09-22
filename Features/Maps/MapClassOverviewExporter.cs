using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using IDVBuff.Appearance;

namespace IDVBuff.Features.Maps;

/// <summary>Builds one contact sheet per floor from a single map class.</summary>
public static class MapClassOverviewExporter
{
    private const int TileWidth = 600;
    private const int TileHeight = 460;
    private const int HeaderHeight = 64;
    private const int Gap = 20;
    private const int Margin = 30;

    public static IReadOnlyList<string> Export(
        string directory,
        IReadOnlyList<MapRecord> maps,
        Func<MapRecord, string, string> imagePath,
        Func<MapRecord, MapCardColors?> variantColors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(imagePath);
        ArgumentNullException.ThrowIfNull(variantColors);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"导出文件夹不存在：{directory}");
        if (maps.Count == 0)
            throw new InvalidOperationException("当前地图类没有地图可导出。");

        var orderedMaps = maps.OrderBy(map => map.DisplayName, NaturalMapNameComparer.Instance)
            .ThenBy(map => map.SequenceNumber)
            .ThenBy(map => map.Id)
            .ToArray();
        var floors = orderedMaps.SelectMany(MapFloorRules.GetOrderedFloors)
            .GroupBy(floor => floor.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(floor => floor.SortOrder)
            .ThenBy(floor => floor.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (floors.Length == 0)
            throw new InvalidOperationException("当前地图类没有楼层可导出。");

        var plans = floors.Select((floor, index) => new FloorPlan(
            floor.Key,
            Path.Combine(directory, $"overview_floor_{index + 1:D3}.png"),
            orderedMaps.Where(map => MapFloorRules.GetOrderedFloors(map).Any(candidate =>
                string.Equals(candidate.Key, floor.Key, StringComparison.OrdinalIgnoreCase)))
                .ToArray())).ToArray();
        foreach (var plan in plans)
        {
            if (File.Exists(plan.OutputPath))
                throw new IOException($"目标文件已存在，请选择其他文件夹：{plan.OutputPath}");
            foreach (var map in plan.Maps)
            {
                var floor = MapFloorRules.GetOrderedFloors(map).First(candidate =>
                    string.Equals(candidate.Key, plan.FloorKey, StringComparison.OrdinalIgnoreCase));
                var path = imagePath(map, floor.Key);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"{map.DisplayName} · {floor.DisplayName} 缺少地图图片。", path);
            }
        }

        var staged = new List<(string Temporary, string Output)>();
        var committed = new List<string>();
        try
        {
            foreach (var plan in plans)
            {
                var temporary = Path.Combine(directory, $".overview_{Guid.NewGuid():N}.tmp");
                staged.Add((temporary, plan.OutputPath));
                RenderFloor(plan, temporary, imagePath, variantColors);
            }
            foreach (var (temporary, output) in staged)
            {
                File.Move(temporary, output);
                committed.Add(output);
            }
            return committed.ToArray();
        }
        catch
        {
            foreach (var output in committed)
                File.Delete(output);
            throw;
        }
        finally
        {
            foreach (var (temporary, _) in staged)
                if (File.Exists(temporary))
                    File.Delete(temporary);
        }
    }

    private static void RenderFloor(
        FloorPlan plan,
        string output,
        Func<MapRecord, string, string> imagePath,
        Func<MapRecord, MapCardColors?> variantColors)
    {
        var columns = Math.Min(5, plan.Maps.Length);
        var rows = (plan.Maps.Length + 4) / 5;
        var width = checked(Margin * 2 + columns * TileWidth + (columns - 1) * Gap);
        var height = checked(Margin * 2 + rows * TileHeight + (rows - 1) * Gap);
        using var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.Clear(Color.FromArgb(246, 247, 249));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Microsoft YaHei UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
        for (var index = 0; index < plan.Maps.Length; index++)
        {
            var map = plan.Maps[index];
            var floor = MapFloorRules.GetOrderedFloors(map).First(candidate =>
                string.Equals(candidate.Key, plan.FloorKey, StringComparison.OrdinalIgnoreCase));
            var tile = new Rectangle(
                Margin + index % 5 * (TileWidth + Gap),
                Margin + index / 5 * (TileHeight + Gap),
                TileWidth, TileHeight);
            var palette = variantColors(map);
            var fill = palette is null ? Color.FromArgb(232, 235, 239) : ToColor(palette.Fill);
            var border = palette is null ? Color.FromArgb(170, 178, 189) : ToColor(palette.Border);
            var labelColor = palette is null ? Color.FromArgb(30, 38, 50) : ToColor(palette.Text);
            using var fillBrush = new SolidBrush(fill);
            using var borderPen = new Pen(border, palette is null ? 2 : 4);
            using var labelBrush = new SolidBrush(labelColor);
            graphics.FillRectangle(Brushes.White, tile);
            graphics.FillRectangle(fillBrush, tile.X, tile.Y, tile.Width, HeaderHeight);
            graphics.DrawRectangle(borderPen, tile.X + 2, tile.Y + 2, tile.Width - 4, tile.Height - 4);
            graphics.DrawString($"{map.DisplayName} | {floor.DisplayName}", font,
                labelBrush, tile.X + 18, tile.Y + 16);

            var sourcePath = imagePath(map, floor.Key);
            using var source = System.Drawing.Image.FromFile(sourcePath);
            var profile = MapFloorRules.GetFloorProfile(map, floor.Key);
            var polygon = (profile?.FreeCropPoints ?? []).Where(point => point.IsValid).ToArray();
            var region = ResolveCropRegion(profile, polygon);
            var crop = PixelCrop(region, source.Width, source.Height);
            var imageArea = new Rectangle(tile.X + 10, tile.Y + HeaderHeight + 10,
                tile.Width - 20, tile.Height - HeaderHeight - 20);
            var scale = Math.Min(imageArea.Width / (double)crop.Width,
                imageArea.Height / (double)crop.Height);
            var imageWidth = Math.Max(1, (int)Math.Round(crop.Width * scale));
            var imageHeight = Math.Max(1, (int)Math.Round(crop.Height * scale));
            var destination = new Rectangle(
                imageArea.X + (imageArea.Width - imageWidth) / 2,
                imageArea.Y + (imageArea.Height - imageHeight) / 2,
                imageWidth, imageHeight);
            var state = graphics.Save();
            try
            {
                if (polygon.Length >= 3)
                {
                    using var outline = new GraphicsPath();
                    outline.AddPolygon(polygon.Select(point => new PointF(
                        (float)(destination.X + (point.X * (source.Width - 1) - crop.X)
                            * destination.Width / crop.Width),
                        (float)(destination.Y + (point.Y * (source.Height - 1) - crop.Y)
                            * destination.Height / crop.Height))).ToArray());
                    graphics.SetClip(outline, CombineMode.Intersect);
                }
                graphics.DrawImage(source, destination, crop, GraphicsUnit.Pixel);
            }
            finally
            {
                graphics.Restore(state);
            }
        }
        canvas.Save(output, ImageFormat.Png);
    }

    private static NormalizedRectangle ResolveCropRegion(
        FloorRecognitionProfile? profile,
        IReadOnlyList<NormalizedPoint> polygon)
    {
        if (polygon.Count < 3)
            return profile?.GetEffectiveRecognitionRegion()
                ?? new NormalizedRectangle { Width = 1, Height = 1 };
        var left = polygon.Min(point => point.X);
        var top = polygon.Min(point => point.Y);
        var right = polygon.Max(point => point.X);
        var bottom = polygon.Max(point => point.Y);
        return new NormalizedRectangle
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
    }

    private static Rectangle PixelCrop(NormalizedRectangle region, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(region.X * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(region.Y * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * height), top + 1, height);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    private static Color ToColor(RgbColor color) => Color.FromArgb(color.R, color.G, color.B);

    private sealed record FloorPlan(string FloorKey, string OutputPath, MapRecord[] Maps);
}

public sealed class NaturalMapNameComparer : IComparer<string>
{
    public static NaturalMapNameComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;
        var left = 0;
        var right = 0;
        while (true)
        {
            while (left < x.Length && char.IsWhiteSpace(x[left])) left++;
            while (right < y.Length && char.IsWhiteSpace(y[right])) right++;
            if (left == x.Length || right == y.Length)
                return (x.Length - left).CompareTo(y.Length - right);
            if (char.IsAsciiDigit(x[left]) && char.IsAsciiDigit(y[right]))
            {
                var leftStart = left;
                var rightStart = right;
                while (left < x.Length && char.IsAsciiDigit(x[left])) left++;
                while (right < y.Length && char.IsAsciiDigit(y[right])) right++;
                var leftDigits = x.AsSpan(leftStart, left - leftStart).TrimStart('0');
                var rightDigits = y.AsSpan(rightStart, right - rightStart).TrimStart('0');
                var lengthOrder = leftDigits.Length.CompareTo(rightDigits.Length);
                if (lengthOrder != 0) return lengthOrder;
                var numberOrder = leftDigits.CompareTo(rightDigits, StringComparison.Ordinal);
                if (numberOrder != 0) return numberOrder;
                continue;
            }
            var order = char.ToUpperInvariant(x[left]).CompareTo(char.ToUpperInvariant(y[right]));
            if (order != 0) return order;
            left++;
            right++;
        }
    }
}
