using IDVBuff.ModuleContracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Modules;

/// <summary>
/// WinUI-facing adapter for a module imported through the framework-neutral contracts project.
/// </summary>
public sealed class AppModule
{
    private readonly IAppModule _source;

    public AppModule(IAppModule source)
    {
        _source = source;
        Id = source.Id;
        DisplayName = source.DisplayName;
        Icon = Enum.TryParse<Symbol>(source.IconKey, ignoreCase: true, out var icon) ? icon : Symbol.Page;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public Symbol Icon { get; }

    /// <summary>Creates a fresh WinUI root view whenever the user enters this module.</summary>
    public FrameworkElement CreateView() => _source.CreateView() as FrameworkElement
        ?? throw new InvalidOperationException($"Module '{Id}' must return a WinUI FrameworkElement from CreateView().");
}
