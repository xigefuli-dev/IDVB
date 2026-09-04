using OpenCvSharp;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvaStructureLineEngine
{
    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"IDVA 缺少对象字段 {name}。");
        return value;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"IDVA 缺少数组字段 {name}。");
        return value;
    }

    private static string RequireNonEmptyString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"IDVA 缺少字符串字段 {name}。");
        return value.GetString()!;
    }

    private static void RequireString(JsonElement parent, string name, string expected)
    {
        var actual = RequireNonEmptyString(parent, name);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"IDVA 字段 {name} 必须为 {expected}。");
    }

    private static bool ReadBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"IDVA 字段 {name} 必须为布尔值。");
        return value.GetBoolean();
    }

    private static int ReadBoundedInt(JsonElement parent, string name, int minimum, int maximum)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result)
            || result < minimum
            || result > maximum)
            throw new InvalidDataException($"IDVA 字段 {name} 超出允许范围。");
        return result;
    }

    private static double ReadBoundedDouble(JsonElement parent, string name, double minimum, double maximum)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var result)
            || !double.IsFinite(result)
            || result < minimum
            || result > maximum)
            throw new InvalidDataException($"IDVA 字段 {name} 超出允许范围。");
        return result;
    }

    private static int[] ReadTriplet(JsonElement parent, string name)
    {
        return ReadTripletElement(RequireArray(parent, name), name);
    }

    private static int[] ReadTripletElement(JsonElement value, string name)
    {
        var values = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            .Select(itemValue => itemValue.ValueKind == JsonValueKind.Number && itemValue.TryGetInt32(out var item)
                ? item
                : -1)
            .ToArray()
            : [];
        if (values.Length != 3 || values.Any(value => value is < 0 or > 255))
            throw new InvalidDataException($"IDVA 字段 {name} 必须是三个 0..255 整数。");
        return values;
    }

    private static IReadOnlyList<Vec3b> ReadCenters(JsonElement parent, string name)
    {
        var centers = RequireArray(parent, name).EnumerateArray()
            .Select(element =>
            {
                var triplet = element.ValueKind == JsonValueKind.Array
                    ? element.EnumerateArray().Select(value => value.TryGetInt32(out var item) ? item : -1).ToArray()
                    : [];
                if (triplet.Length != 3 || triplet.Any(value => value is < 0 or > 255))
                    throw new InvalidDataException($"IDVA 字段 {name} 包含无效颜色中心。");
                return new Vec3b((byte)triplet[0], (byte)triplet[1], (byte)triplet[2]);
            })
            .ToArray();
        if (centers.Length is <= 0 or > 32)
            throw new InvalidDataException($"IDVA 字段 {name} 必须包含 1 到 32 个颜色中心。");
        return centers;
    }

    private static Size ReadSize(JsonElement parent, string name)
    {
        var size = RequireArray(parent, name).EnumerateArray()
            .Select(value => value.TryGetInt32(out var item) ? item : 0)
            .ToArray();
        if (size.Length != 2 || size.Any(value => value is <= 0 or > 255 || value % 2 == 0))
            throw new InvalidDataException($"IDVA 字段 {name} 必须是两个不大于 255 的正奇数。");
        return new Size(size[0], size[1]);
    }

    private static IReadOnlyList<Size> ReadSizes(JsonElement parent, string name)
    {
        var array = RequireArray(parent, name);
        if (array.GetArrayLength() is <= 0 or > 16)
            throw new InvalidDataException($"IDVA 字段 {name} 的数组长度无效。");
        return array.EnumerateArray()
            .Select(value => ReadSizeElement(value, name))
            .ToArray();
    }

    private static Size ReadSizeElement(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"IDVA 字段 {name} 包含无效尺寸。");
        var size = value.EnumerateArray()
            .Select(item => item.TryGetInt32(out var number) ? number : 0)
            .ToArray();
        if (size.Length != 2 || size.Any(number => number is <= 0 or > 255 || number % 2 == 0))
            throw new InvalidDataException($"IDVA 字段 {name} 必须只包含正奇数尺寸。");
        return new Size(size[0], size[1]);
    }

    private static RetrievalModes ReadRetrieval(JsonElement stage)
    {
        RequireString(stage, "chain", "CHAIN_APPROX_SIMPLE");
        return RequireNonEmptyString(stage, "retrieval") switch
        {
            "RETR_LIST" => RetrievalModes.List,
            "RETR_EXTERNAL" => RetrievalModes.External,
            "RETR_CCOMP" => RetrievalModes.CComp,
            _ => throw new InvalidDataException("IDVA contour retrieval 不受支持。")
        };
    }

    private static void ReplaceMasks(PipelineState state, Mat room, Mat corridor)
    {
        state.ReplaceRoom(room);
        state.ReplaceCorridor(corridor);
    }

    private sealed class PipelineState : IDisposable
    {
        private Mat _room;
        private Mat _corridor;
        private Mat? _roomDistance;
        private Mat? _corridorDistance;
        private Mat? _edges;

        private PipelineState(Mat bgr, Mat room, Mat corridor)
        {
            Bgr = bgr;
            _room = room;
            _corridor = corridor;
        }

        public Mat Bgr { get; }
        public Mat Room => _room;
        public Mat Corridor => _corridor;
        public Mat? RoomDistance => _roomDistance;
        public Mat? CorridorDistance => _corridorDistance;
        public Mat? Edges => _edges;
        public RetrievalModes RoomRetrieval { get; set; } = RetrievalModes.List;
        public RetrievalModes CorridorRetrieval { get; set; } = RetrievalModes.List;

        public static PipelineState Create(Mat source)
        {
            var bgr = new Mat();
            switch (source.Channels())
            {
                case 4:
                    Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
                    break;
                case 3:
                    source.CopyTo(bgr);
                    break;
                case 1:
                    Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
                    break;
                default:
                    bgr.Dispose();
                    throw new InvalidDataException("IDVA 输入只支持灰度、BGR 或 BGRA 图像。");
            }
            return new PipelineState(
                bgr,
                new Mat(source.Size(), MatType.CV_8UC1, Scalar.Black),
                new Mat(source.Size(), MatType.CV_8UC1, Scalar.Black));
        }

        public void ReplaceRoom(Mat value) => Replace(ref _room, value);
        public void ReplaceCorridor(Mat value) => Replace(ref _corridor, value);
        public void ReplaceDistances(Mat room, Mat corridor)
        {
            ReplaceNullable(ref _roomDistance, room);
            ReplaceNullable(ref _corridorDistance, corridor);
        }

        public void CombineEdges(int thickness)
        {
            using var room = DrawContours(Room, RoomRetrieval, thickness);
            using var corridor = DrawContours(Corridor, CorridorRetrieval, thickness);
            var combined = new Mat();
            Cv2.BitwiseOr(room, corridor, combined);
            ReplaceNullable(ref _edges, combined);
        }

        private static void Replace(ref Mat field, Mat value)
        {
            field.Dispose();
            field = value;
        }

        private static void ReplaceNullable(ref Mat? field, Mat value)
        {
            field?.Dispose();
            field = value;
        }

        public void Dispose()
        {
            Bgr.Dispose();
            _room.Dispose();
            _corridor.Dispose();
            _roomDistance?.Dispose();
            _corridorDistance?.Dispose();
            _edges?.Dispose();
        }
    }
}
