using System.Collections.ObjectModel;
using IDVBuff.ModuleContracts;

namespace IDVBuff.Modules;

/// <summary>
/// Central, ordered list of modules visible in the app shell.
/// Imported C# projects register one <see cref="IAppModule"/> here at startup.
/// </summary>
public sealed class ModuleCatalog
{
    private readonly ObservableCollection<AppModule> _modules = [];
    private readonly ReadOnlyObservableCollection<AppModule> _readOnlyModules;

    public ModuleCatalog()
    {
        _readOnlyModules = new ReadOnlyObservableCollection<AppModule>(_modules);
    }

    public ReadOnlyObservableCollection<AppModule> Modules => _readOnlyModules;

    public void Register(IAppModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(module.Id))
            throw new ArgumentException("A module must have a stable, non-empty ID.", nameof(module));
        if (_modules.Any(existing => string.Equals(existing.Id, module.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The module ID '{module.Id}' is already registered.");

        _modules.Add(new AppModule(module));
    }

    public AppModule GetRequired(string id) =>
        _modules.FirstOrDefault(module => string.Equals(module.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"No module is registered with ID '{id}'.");
}
