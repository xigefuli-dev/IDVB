namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private static void ValidateLayoutIdentity(MetadataDto metadata, bool allowLayoutIdentities)
    {
        if (metadata.SchemaVersion == 4 && !allowLayoutIdentities)
            throw new InvalidDataException("metadata schema 4 要求 layoutIdentities 能力。");
        if (metadata.Map.LayoutIdentity is not { } identity) return;
        if (metadata.SchemaVersion != 4 || !allowLayoutIdentities)
            throw new InvalidDataException("布局身份必须使用 metadata schema 4。");
        identity.Validate(metadata.Floors.Select(floor => floor.Key));
    }
}
