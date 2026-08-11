using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Idvm;

internal sealed class SurveyIdvmManifest
{
    public string Format { get; set; } = "idvm";
    public string FormatVersion { get; set; } = "1.2";
    public string MinimumReaderVersion { get; set; } = "1.2";
    public string PackageType { get; set; } = "survey-project";
    public Guid PackageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public SurveyProjectSnapshot? Project { get; set; }
    public List<SurveyIdvmFile> Files { get; set; } = [];
}

internal sealed class SurveyIdvmFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
}
