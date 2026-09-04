using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private static void ValidateJsonShape(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"{name} 包含重复字段：{property.Name}");
                ValidateJsonShape(property.Value, name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > 100_000)
                throw new InvalidDataException($"{name} 包含过大的 JSON 数组。");
            foreach (var item in element.EnumerateArray())
                ValidateJsonShape(item, name);
        }
    }
}
