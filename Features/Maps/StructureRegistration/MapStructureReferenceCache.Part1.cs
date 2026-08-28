using OpenCvSharp;
using System.Text.Json;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Non-authoritative derived cache. It never writes into MapRepository or
/// changes maps.json.
/// </summary>
public sealed partial class MapStructureReferenceCache : IDisposable
{

    private sealed class KeyPointDocument
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Size { get; set; }
        public float Angle { get; set; }
        public float Response { get; set; }
        public int Octave { get; set; }
        public int ClassId { get; set; }

        public static KeyPointDocument From(KeyPoint point) => new()
        {
            X = point.Pt.X,
            Y = point.Pt.Y,
            Size = point.Size,
            Angle = point.Angle,
            Response = point.Response,
            Octave = point.Octave,
            ClassId = point.ClassId
        };

        public KeyPoint ToKeyPoint() => new(
            X,
            Y,
            Size,
            Angle,
            Response,
            Octave,
            ClassId);
    }
}
