// IDVB Real CLI — JSON 输出写入器

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.RealCLI.Output;

/// <summary>
/// 将识别结果序列化为结构化 JSON 输出。
/// </summary>
public static class RealCliOutputWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>输出到 stdout。</summary>
    public static void WriteLine(RealCliSessionResult result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        Console.WriteLine(json);
    }

    /// <summary>输出单条结果到文件。</summary>
    public static async Task WriteAsync(RealCliSessionResult result, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(result, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>输出批量汇总到文件。</summary>
    public static async Task WriteBatchAsync(RealCliBatchSummary summary, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(summary, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    public static async Task WriteObjectAsync<T>(T value, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }
}
