using System.Text.Json;

namespace IDVBuff.MapAlignment.Probe.Cli;

/// <summary>
/// 命令行选项 POCO，绑定 CLI 参数。
/// </summary>
public sealed class CliOptions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string? Image { get; set; }
    public string? Out { get; set; }
    public string? MaskOut { get; set; }
    public string? MaskDirectory { get; set; }
    public bool GuideMap { get; set; }
    public string? Settings { get; set; }
    public string? ResearchSession { get; set; }
    public string? MapRoot { get; set; }
    public string? Gate { get; set; }
    public string? Viewport { get; set; }
    public double ViewportMargin { get; set; } = 0.20;
    public bool Full { get; set; }
    public bool Structure { get; set; }
    public bool Ecc { get; set; }
    public bool ForceBest { get; set; }
    public int Top { get; set; } = 1;
    public int TopCandidates { get; set; } = 6;
    public double Downscale { get; set; } = 0.5;
    public double ClientWidth { get; set; } = 2560d;
    public double Threshold { get; set; } = -1d;
    public string? Files { get; set; }
    public int Parallel { get; set; } = 1;
    public string? First { get; set; }
    public string? Second { get; set; }
    public int SideTop { get; set; } = 10;
    public Guid? SideMapId { get; set; }

    public static CliOptions Parse(string[] args)
    {
        var options = ParseAsDictionary(args);
        return new CliOptions
        {
            Image = Get(options, "image"),
            Out = Get(options, "out"),
            MaskOut = Get(options, "mask-out"),
            MaskDirectory = Get(options, "mask-dir"),
            GuideMap = Flag(options, "guide-map"),
            Settings = Get(options, "settings"),
            ResearchSession = Get(options, "research"),
            MapRoot = Get(options, "map-root"),
            Gate = Get(options, "gate"),
            Viewport = Get(options, "viewport"),
            ViewportMargin = Double(options, "viewport-margin", 0.20),
            Full = Flag(options, "full"),
            Structure = Flag(options, "structure"),
            Ecc = Flag(options, "ecc"),
            ForceBest = Flag(options, "force-best"),
            Top = Int(options, "top", 1),
            TopCandidates = Int(options, "top-candidates", 6),
            Downscale = Double(options, "downscale", 0.5),
            ClientWidth = Double(options, "client-width", 2560d),
            Threshold = Double(options, "threshold", -1d),
            Files = Get(options, "files"),
            Parallel = Int(options, "parallel", 1),
            First = Get(options, "first"),
            Second = Get(options, "second"),
            SideTop = Int(options, "top", 10),
            SideMapId = Guid.TryParse(Get(options, "map-id"), out var g) ? g : null
        };
    }

    private static Dictionary<string, string> ParseAsDictionary(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
                continue;
            var key = args[index][2..];
            var value = index + 1 < args.Length
                && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            options[key] = value;
        }
        return options;
    }

    private static string? Get(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) ? value : null;

    private static int Int(Dictionary<string, string> options, string key, int fallback) =>
        options.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed : fallback;

    private static double Double(Dictionary<string, string> options, string key, double fallback) =>
        options.TryGetValue(key, out var value) && double.TryParse(value, out var parsed)
            ? parsed : fallback;

    private static bool Flag(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value)
        && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
