namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    internal async Task<IReadOnlyList<MapRecord>> GetExportMapsAsync(
        IdvmExportScope scope,
        string? className,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SelectMaps((await _repository.GetCatalogSnapshotAsync()).Maps, scope, className);
    }

    private static MapRecord[] SelectMaps(
        IEnumerable<MapRecord> maps,
        IdvmExportScope scope,
        string? className) => scope == IdvmExportScope.CurrentClass
            ? maps.Where(map => string.Equals(
                map.Class,
                className,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : maps.ToArray();
}
