using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
/// <summary>Reads and writes the portable, untrusted IDVM interchange format.</summary>
public sealed partial class IdvmPackageService
{

    private sealed class AnnotationDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public int ColorIndex { get; set; }
        public string? Color { get; set; }
        public RectangleDto? Bounds { get; set; }
        public PointDto? Start { get; set; }
        public PointDto? End { get; set; }
        public string? Text { get; set; }
        public string? FontFamily { get; set; }
        public double? FontSize { get; set; }
        public bool? IsBold { get; set; }
        public bool? IsItalic { get; set; }
        public bool? IsStrikethrough { get; set; }
    }

    private sealed class PointDto
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    private sealed class RectangleDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
