namespace IdentityVisionBridge.PluginRuntime;

public sealed class PluginStateRepository
{
    private readonly AtomicJsonStore<PluginCatalog> _catalog;
    private readonly AtomicJsonStore<TrustedPublisherCatalog> _publishers;

    public PluginStateRepository(PluginDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        _catalog = new AtomicJsonStore<PluginCatalog>(directories.CatalogPath);
        _publishers = new AtomicJsonStore<TrustedPublisherCatalog>(directories.TrustedPublishersPath);
    }

    public Task<PluginCatalog> ReadCatalogAsync(CancellationToken cancellationToken = default) =>
        _catalog.ReadAsync(cancellationToken);

    public Task<TrustedPublisherCatalog> ReadPublishersAsync(CancellationToken cancellationToken = default) =>
        _publishers.ReadAsync(cancellationToken);

    public Task<PluginCatalog> UpdateCatalogAsync(
        Func<PluginCatalog, PluginCatalog> update,
        CancellationToken cancellationToken = default) =>
        _catalog.UpdateAsync(update, cancellationToken);

    public Task<TrustedPublisherCatalog> UpdatePublishersAsync(
        Func<TrustedPublisherCatalog, TrustedPublisherCatalog> update,
        CancellationToken cancellationToken = default) =>
        _publishers.UpdateAsync(update, cancellationToken);
}
