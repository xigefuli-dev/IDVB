using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
public static partial class MapFeatureCacheRules
{

    public static double GetCandidateMargin(MapRecognitionResult result) =>
        result.EvidenceKind == MapAlignmentEvidenceKind.Structure
            ? result.StructureCandidateMargin
            : result.GeometryMargin;
}
