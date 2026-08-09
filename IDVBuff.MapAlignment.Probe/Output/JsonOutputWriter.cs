using System.Text.Json;

namespace IDVBuff.MapAlignment.Probe.Output;

/// <summary>
/// 统一的 JSON 序列化输出，使用一致的格式化选项。
/// </summary>
public static class JsonOutputWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Write(ProbeResult result, Stream stream)
    {
        JsonSerializer.Serialize(stream, result, Options);
    }

    public static string Write(ProbeResult result)
    {
        return JsonSerializer.Serialize(result, Options);
    }

    public static void WriteLine(ProbeResult result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, Options));
    }

    public static async Task WriteAsync(ProbeResult result, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
            Directory.CreateDirectory(directory);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, result, Options);
    }
}
